"""XREAL Air-family IMU: HID packet parsing, command building, 6-axis fusion and the Unity wire format.

Pure Python 3 standard library. Nothing here touches hardware or the network, so it is safe to import
from tests. Every protocol constant below comes from docs/apis/glasses/imu-protocol.md, which quotes the
Monado `xreal_air` driver, xrealAirLinuxDriver and ar-drivers-rs with commit hashes. The section numbers
(§N) in the comments refer to that file. Anything marked "bridge parameter" is our own engineering
choice, not a protocol fact.
"""

import json
import math
from collections import namedtuple

# --------------------------------------------------------------------------------------------------
# §2 USB ids and HID interfaces
# --------------------------------------------------------------------------------------------------

VENDOR_ID = 0x3318

Model = namedtuple("Model", "name imu_iface mcu_iface imu_report_max")

# pid -> Model. Interfaces and the IMU report size come from Monado target_builder_xreal_air.c
# (driver_handle_ifaces / driver_control_ifaces / driver_max_sensor_buffer_sizes), matched by
# xrealAirLinuxDriver hid_ids.c and ar-drivers-rs nreal_air.rs (§2).
MODELS = {
    0x0424: Model("XREAL Air", 3, 4, 64),
    0x0428: Model("XREAL Air 2", 3, 4, 64),
    0x0432: Model("XREAL Air 2 Pro", 3, 4, 64),
    0x0426: Model("XREAL Air 2 Ultra", 2, 0, 512),
}
AIR_2_ULTRA_PID = 0x0426

# --------------------------------------------------------------------------------------------------
# §3 Commands on the IMU interface, §4 responses
# --------------------------------------------------------------------------------------------------

COMMAND_HEAD = 0xAA
MSG_GET_CAL_DATA_LENGTH = 0x14
MSG_CAL_DATA_GET_NEXT_SEGMENT = 0x15
MSG_START_IMU_DATA = 0x19  # data: 0x01 = stream on, 0x00 = stream off (§3)
MSG_GET_STATIC_ID = 0x1A
COMMAND_PAD = 64  # ar-drivers-rs writes every IMU command as a zero-padded 64-byte buffer (§3)


def _crc32_table():
    table = []
    for n in range(256):
        c = n
        for _ in range(8):
            c = (c >> 1) ^ 0xEDB88320 if c & 1 else c >> 1
        table.append(c)
    return tuple(table)


# Monado's crc32_table[256] is the standard reflected CRC-32 table for polynomial 0xEDB88320
# (§3; checked entry by entry against the Monado source, and test_xreal_imu.py spot-checks it).
CRC32_TABLE = _crc32_table()


def crc32_checksum(data):
    """Port of Monado xreal_air_hmd.c crc32_checksum() (§3). Equal to zlib.crc32."""
    crc = 0xFFFFFFFF
    for b in data:
        crc = ((crc >> 8) & 0xFFFFFF) ^ CRC32_TABLE[(crc ^ b) & 0xFF]
    return (~crc) & 0xFFFFFFFF


def build_command(msgid, data=b"", pad_to=0):
    """One IMU-interface command, as Monado send_payload_to_sensor() lays it out (§3):

    [0] 0xAA | [1:5] CRC-32 (LE) of bytes [5:5+len] | [5:7] len = 3 + n (LE) | [7] msgid | [8:8+n] data

    pad_to > 0 appends zero bytes up to that length (ar-drivers-rs sends 64 bytes; Monado and
    xrealAirLinuxDriver send exactly 8 + n). The CRC never covers the padding.
    """
    data = bytes(data)
    length = 3 + len(data)
    body = length.to_bytes(2, "little") + bytes([msgid & 0xFF]) + data
    out = bytes([COMMAND_HEAD]) + crc32_checksum(body).to_bytes(4, "little") + body
    if pad_to and len(out) < pad_to:
        out += bytes(pad_to - len(out))
    return out


def start_imu_command(pad_to=COMMAND_PAD):
    """MSG_START_IMU_DATA with 0x01: turn the IMU stream on (§3)."""
    return build_command(MSG_START_IMU_DATA, b"\x01", pad_to)


def stop_imu_command(pad_to=COMMAND_PAD):
    """MSG_START_IMU_DATA with 0x00: turn the IMU stream off (xrealAirLinuxDriver does this on close, §3)."""
    return build_command(MSG_START_IMU_DATA, b"\x00", pad_to)


CommandResponse = namedtuple("CommandResponse", "msgid length data crc crc_ok")


def parse_command_response(buf):
    """Parse an 0xAA packet read from the IMU interface (Monado
    xreal_air_parse_sensor_control_data_packet, §4). Returns None if buf is not one.
    crc_ok is informational only: no source validates response CRCs."""
    buf = bytes(buf)
    if len(buf) < 8 or buf[0] != COMMAND_HEAD:
        return None
    crc = int.from_bytes(buf[1:5], "little")
    length = int.from_bytes(buf[5:7], "little")
    msgid = buf[7]
    data = buf[8:8 + max(0, length - 3)]
    crc_ok = 5 + length <= len(buf) and crc32_checksum(buf[5:5 + length]) == crc
    return CommandResponse(msgid, length, data, crc, crc_ok)


# --------------------------------------------------------------------------------------------------
# §5 IMU data packet (the first 64 bytes of every report that starts 0x01 0x02)
# --------------------------------------------------------------------------------------------------

IMU_SIGNATURE = b"\x01\x02"
IMU_PACKET_LEN = 64
GRAVITY_M_S2 = 9.80665  # only used to print m/s^2; the fusion works in g


class ImuPacket(namedtuple("ImuPacket", [
        "signature", "temperature_raw", "timestamp_ns",
        "gyro_multiplier", "gyro_divisor", "gyro_raw",
        "accel_multiplier", "accel_divisor", "accel_raw",
        "mag_multiplier", "mag_divisor", "mag_raw", "checksum"])):
    """Raw fields of one IMU packet. Offsets and encodings: §5 table."""
    __slots__ = ()

    @property
    def temperature_c(self):
        # Monado/xrealAirLinuxDriver: "According to the ICM-42688-P datasheet: (offset: 25 °C,
        # sensitivity: 132.48 LSB/°C)" (§5). Not used by the fusion.
        return self.temperature_raw / 132.48 + 25.0

    @property
    def gyro_dps(self):
        """Angular rate in degrees/second, device axes (§5: raw * multiplier / divisor)."""
        f = self.gyro_multiplier / self.gyro_divisor
        return (self.gyro_raw[0] * f, self.gyro_raw[1] * f, self.gyro_raw[2] * f)

    @property
    def accel_g(self):
        """Specific force in g, device axes (§5: raw * multiplier / divisor)."""
        f = self.accel_multiplier / self.accel_divisor
        return (self.accel_raw[0] * f, self.accel_raw[1] * f, self.accel_raw[2] * f)

    @property
    def mag(self):
        """Magnetometer, raw * multiplier / divisor, device axes. Unit not documented (§5)."""
        if self.mag_divisor == 0:
            return (float("nan"),) * 3
        f = self.mag_multiplier / self.mag_divisor
        return (self.mag_raw[0] * f, self.mag_raw[1] * f, self.mag_raw[2] * f)


def _i16le(b, o):
    return int.from_bytes(b[o:o + 2], "little", signed=True)


def _i24le(b, o):
    return int.from_bytes(b[o:o + 3], "little", signed=True)


def _i32le(b, o):
    return int.from_bytes(b[o:o + 4], "little", signed=True)


def _mag16(b, o):
    # Monado read_i15_to_i32: 16-bit little endian with the sign bit flipped (v ^ 0x8000), as int16.
    return int.from_bytes(b[o:o + 2], "little") - 0x8000


def is_imu_report(buf):
    return len(buf) >= IMU_PACKET_LEN and buf[0] == 0x01 and buf[1] == 0x02


def parse_imu_packet(buf):
    """Parse the first 64 bytes of an IMU report. Returns ImuPacket, or None when the report is
    shorter than 64 bytes, does not start 0x01 0x02, or has a zero gyro/accel divisor.

    Older models send 64-byte reports; Monado reads 512-byte reports on the Air 2 Ultra and parses
    the same 64-byte layout from the front (§2, §5), so any length >= 64 is accepted.
    """
    b = bytes(buf[:IMU_PACKET_LEN])
    if not is_imu_report(b):
        return None
    gyro_div = _i32le(b, 0x0E)
    accel_div = _i32le(b, 0x1D)
    if gyro_div == 0 or accel_div == 0:
        return None
    return ImuPacket(
        signature=b[0:2],
        temperature_raw=_i16le(b, 0x02),
        timestamp_ns=int.from_bytes(b[0x04:0x0C], "little"),
        gyro_multiplier=_i16le(b, 0x0C),
        gyro_divisor=gyro_div,
        gyro_raw=(_i24le(b, 0x12), _i24le(b, 0x15), _i24le(b, 0x18)),
        accel_multiplier=_i16le(b, 0x1B),
        accel_divisor=accel_div,
        accel_raw=(_i24le(b, 0x21), _i24le(b, 0x24), _i24le(b, 0x27)),
        mag_multiplier=int.from_bytes(b[0x2A:0x2C], "big", signed=True),
        mag_divisor=int.from_bytes(b[0x2C:0x30], "big", signed=True),
        mag_raw=(_mag16(b, 0x30), _mag16(b, 0x32), _mag16(b, 0x34)),
        checksum=int.from_bytes(b[0x36:0x3A], "little"),
    )


def encode_imu_packet(temperature_raw=0, timestamp_ns=0, gyro_multiplier=1, gyro_divisor=1000,
                      gyro_raw=(0, 0, 0), accel_multiplier=1, accel_divisor=8192, accel_raw=(0, 0, 8192),
                      mag_multiplier=1, mag_divisor=100, mag_raw=(0, 0, 0), checksum=0, length=IMU_PACKET_LEN):
    """Inverse of parse_imu_packet (for tests and synthetic streams; the defaults are test values,
    not device constants). length > 64 zero-pads like an Air 2 Ultra 512-byte report."""
    b = bytearray(max(length, IMU_PACKET_LEN))
    b[0:2] = IMU_SIGNATURE
    b[0x02:0x04] = int(temperature_raw).to_bytes(2, "little", signed=True)
    b[0x04:0x0C] = int(timestamp_ns).to_bytes(8, "little")
    b[0x0C:0x0E] = int(gyro_multiplier).to_bytes(2, "little", signed=True)
    b[0x0E:0x12] = int(gyro_divisor).to_bytes(4, "little", signed=True)
    for i, off in enumerate((0x12, 0x15, 0x18)):
        b[off:off + 3] = int(gyro_raw[i]).to_bytes(3, "little", signed=True)
    b[0x1B:0x1D] = int(accel_multiplier).to_bytes(2, "little", signed=True)
    b[0x1D:0x21] = int(accel_divisor).to_bytes(4, "little", signed=True)
    for i, off in enumerate((0x21, 0x24, 0x27)):
        b[off:off + 3] = int(accel_raw[i]).to_bytes(3, "little", signed=True)
    b[0x2A:0x2C] = int(mag_multiplier).to_bytes(2, "big", signed=True)
    b[0x2C:0x30] = int(mag_divisor).to_bytes(4, "big", signed=True)
    for i, off in enumerate((0x30, 0x32, 0x34)):
        b[off:off + 2] = ((int(mag_raw[i]) + 0x8000) & 0xFFFF).to_bytes(2, "little")
    b[0x36:0x3A] = int(checksum).to_bytes(4, "little")
    return bytes(b)


# --------------------------------------------------------------------------------------------------
# §7 Axes. Device axes -> "RUB" (+X right, +Y up, +Z back, right-handed; the frame ar-drivers-rs
# documents). §8 RUB -> Unity (+X right, +Y up, +Z forward, left-handed).
# --------------------------------------------------------------------------------------------------

def device_to_rub(v):
    """ar-drivers-rs parse_report and Monado pre/post_biased_coordinate_system both map device
    (x, y, z) to (-x, z, y) (§7)."""
    return (-v[0], v[2], v[1])


def rub_to_unity_quat(q):
    """Mirror Z (RUB -> Unity): q = (x, y, z, w) becomes (-x, -y, z, w) (§8 derivation)."""
    return (-q[0], -q[1], q[2], q[3])


# Quaternions are (x, y, z, w) tuples, Hamilton product, unit length; q maps local -> world.

def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def q_conj(q):
    return (-q[0], -q[1], -q[2], q[3])


def q_normalize(q):
    n = math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3])
    if n < 1e-12:
        return (0.0, 0.0, 0.0, 1.0)
    return (q[0] / n, q[1] / n, q[2] / n, q[3] / n)


def q_from_axis_angle(axis, angle):
    s = math.sin(angle * 0.5)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(angle * 0.5))


def q_rotate(q, v):
    """Rotate vector v by unit quaternion q (same formula as Unity's Quaternion * Vector3, §8)."""
    x, y, z, w = q
    tx = 2.0 * (y * v[2] - z * v[1])
    ty = 2.0 * (z * v[0] - x * v[2])
    tz = 2.0 * (x * v[1] - y * v[0])
    return (v[0] + w * tx + (y * tz - z * ty),
            v[1] + w * ty + (z * tx - x * tz),
            v[2] + w * tz + (x * ty - y * tx))


def q_from_to(u, v):
    """Shortest-arc rotation taking direction u to direction v."""
    u = _unit(u)
    v = _unit(v)
    d = u[0] * v[0] + u[1] * v[1] + u[2] * v[2]
    if d < -0.999999:  # opposite: any perpendicular axis
        axis = _unit(_cross((1.0, 0.0, 0.0), u)) if abs(u[0]) < 0.9 else _unit(_cross((0.0, 1.0, 0.0), u))
        return q_from_axis_angle(axis, math.pi)
    c = _cross(u, v)
    return q_normalize((c[0], c[1], c[2], 1.0 + d))


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _norm(v):
    return math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])


def _unit(v):
    n = _norm(v)
    return (v[0] / n, v[1] / n, v[2] / n) if n > 1e-12 else (0.0, 0.0, 0.0)


def unity_euler_deg(q):
    """Signed Unity-style Euler angles (x = pitch, y = yaw, z = roll) in degrees, for a quaternion in
    Unity's frame, using Unity's Z-then-X-then-Y order (q = qy * qx * qz, §8). Unity's own
    eulerAngles shows the same values in [0, 360)."""
    x, y, z, w = q
    sx = max(-1.0, min(1.0, 2.0 * (w * x - y * z)))
    pitch = math.degrees(math.asin(sx))
    yaw = math.degrees(math.atan2(2.0 * (x * z + w * y), 1.0 - 2.0 * (x * x + y * y)))
    roll = math.degrees(math.atan2(2.0 * (x * y + w * z), 1.0 - 2.0 * (x * x + z * z)))
    return (pitch, yaw, roll)


def unity_quat_from_euler_deg(pitch, yaw, roll):
    """Unity's Quaternion.Euler(x, y, z): z about Z, then x about X, then y about Y (§8)."""
    qx = q_from_axis_angle((1.0, 0.0, 0.0), math.radians(pitch))
    qy = q_from_axis_angle((0.0, 1.0, 0.0), math.radians(yaw))
    qz = q_from_axis_angle((0.0, 0.0, 1.0), math.radians(roll))
    return q_mul(qy, q_mul(qx, qz))


# --------------------------------------------------------------------------------------------------
# Fusion (bridge parameters; documented in imu-protocol.md "Bridge fusion")
# --------------------------------------------------------------------------------------------------

WORLD_UP_RUB = (0.0, 1.0, 0.0)
FORWARD_RUB = (0.0, 0.0, -1.0)
RIGHT_RUB = (1.0, 0.0, 0.0)


def zero_heading(q):
    """Remove the rotation about world up so that the head's forward vector has heading 0
    (keeps pitch and roll). Works in the RUB frame."""
    f = q_rotate(q, FORWARD_RUB)
    if f[0] * f[0] + f[2] * f[2] > 1e-4:
        heading = math.atan2(-f[0], -f[2])  # rotation about +Y that takes (0,0,-1) to f's heading
    else:  # looking straight up or down: use the right vector instead
        r = q_rotate(q, RIGHT_RUB)
        heading = math.atan2(-r[2], r[0])
    return q_normalize(q_mul(q_from_axis_angle(WORLD_UP_RUB, -heading), q))


class MahonyFilter(object):
    """6-axis Mahony complementary filter (Mahony, Hamel, Pflimlin 2008), in the RUB frame.

    The world frame is gravity-aligned (+Y up); yaw is relative to the start heading.
    kp: gravity-correction gain in rad/s per unit error (|error| = sin of the tilt error), so the
    tilt time constant is about 1/kp seconds. ki: integral gain (0 = off; gyro bias is handled by
    StillBiasEstimator and GyroOffsetTracker). accel_tolerance_g: skip the gravity correction when
    | |a| - 1 g | is larger (the head is accelerating).
    """

    def __init__(self, kp=0.5, ki=0.0, accel_tolerance_g=0.1):
        self.kp = kp
        self.ki = ki
        self.accel_tolerance_g = accel_tolerance_g
        self.q = (0.0, 0.0, 0.0, 1.0)
        self.integral = (0.0, 0.0, 0.0)

    def init_from_accel(self, accel_g):
        """Level from gravity, heading 0 (the 'facing the way it faced at start' reference)."""
        self.q = zero_heading(q_from_to(accel_g, WORLD_UP_RUB))
        self.integral = (0.0, 0.0, 0.0)
        return self.q

    def update(self, gyro_rad_s, accel_g, dt):
        gx, gy, gz = gyro_rad_s
        n = _norm(accel_g)
        if n > 1e-9 and abs(n - 1.0) <= self.accel_tolerance_g:
            a = (accel_g[0] / n, accel_g[1] / n, accel_g[2] / n)
            v = q_rotate(q_conj(self.q), WORLD_UP_RUB)  # where "up" should be, in head coordinates
            e = _cross(a, v)
            if self.ki > 0.0:
                ix, iy, iz = self.integral
                self.integral = (ix + self.ki * e[0] * dt, iy + self.ki * e[1] * dt, iz + self.ki * e[2] * dt)
            gx += self.kp * e[0] + self.integral[0]
            gy += self.kp * e[1] + self.integral[1]
            gz += self.kp * e[2] + self.integral[2]
        rate = math.sqrt(gx * gx + gy * gy + gz * gz)
        if rate > 1e-12 and dt > 0.0:
            axis = (gx / rate, gy / rate, gz / rate)
            self.q = q_normalize(q_mul(self.q, q_from_axis_angle(axis, rate * dt)))
        return self.q


class StillBiasEstimator(object):
    """Start-up gyro bias: mean gyro over `seconds` of stillness (bridge parameters).

    'Still' means every gyro axis stays within +-max_abs_gyro_dps and spans less than
    max_gyro_spread_dps, and |a| spans less than max_accel_spread_g, over the window (a slow steady
    turn has no spread, hence the absolute bound); otherwise the window restarts. After
    give_up_seconds it finishes anyway and sets `forced`: with the current still window's mean if
    there is one, else with zero bias (the run-time GyroOffsetTracker can still learn it later).
    """

    def __init__(self, seconds=1.5, max_gyro_spread_dps=2.0, max_abs_gyro_dps=5.0, max_accel_spread_g=0.05,
                 give_up_seconds=15.0):
        self.seconds = seconds
        self.max_gyro_spread_dps = max_gyro_spread_dps
        self.max_abs_gyro_dps = max_abs_gyro_dps
        self.max_accel_spread_g = max_accel_spread_g
        self.give_up_seconds = give_up_seconds
        self.elapsed = 0.0
        self.restarts = 0
        self.done = False
        self.forced = False
        self.bias_dps = (0.0, 0.0, 0.0)
        self.mean_accel_g = (0.0, 1.0, 0.0)
        self._reset_window()

    def _reset_window(self):
        self._n = 0
        self._t = 0.0
        self._gsum = [0.0, 0.0, 0.0]
        self._asum = [0.0, 0.0, 0.0]
        self._gmin = [float("inf")] * 3
        self._gmax = [float("-inf")] * 3
        self._amin = float("inf")
        self._amax = float("-inf")

    def add(self, gyro_dps, accel_g, dt):
        """Feed one sample (RUB axes). Returns True once the bias is ready."""
        if self.done:
            return True
        self.elapsed += dt
        self._n += 1
        self._t += dt
        for i in range(3):
            self._gsum[i] += gyro_dps[i]
            self._asum[i] += accel_g[i]
            self._gmin[i] = min(self._gmin[i], gyro_dps[i])
            self._gmax[i] = max(self._gmax[i], gyro_dps[i])
        an = _norm(accel_g)
        self._amin = min(self._amin, an)
        self._amax = max(self._amax, an)
        moving = (max(self._gmax[i] - self._gmin[i] for i in range(3)) > self.max_gyro_spread_dps
                  or max(abs(g) for g in gyro_dps) > self.max_abs_gyro_dps
                  or self._amax - self._amin > self.max_accel_spread_g)
        if moving:
            if self.elapsed >= self.give_up_seconds:
                self.bias_dps = (0.0, 0.0, 0.0)
                self.mean_accel_g = tuple(accel_g)
                self.forced = self.done = True
                return True
            self.restarts += 1
            self._reset_window()
            return False
        if self._t >= self.seconds or self.elapsed >= self.give_up_seconds:
            self.bias_dps = tuple(s / self._n for s in self._gsum)
            self.mean_accel_g = tuple(s / self._n for s in self._asum)
            self.forced = self._t < self.seconds
            self.done = True
        return self.done


class GyroOffsetTracker(object):
    """Run-time gyro offset correction: a time-based port of Fusion's FusionOffset.c (§7), which
    xrealAirLinuxDriver runs on these glasses. While every axis stays under THRESHOLD_DPS for
    TIMEOUT_S, the offset follows the gyro through a CUTOFF_HZ low-pass."""

    THRESHOLD_DPS = 3.0
    TIMEOUT_S = 5.0
    CUTOFF_HZ = 0.02

    def __init__(self, enabled=True):
        self.enabled = enabled
        self.offset_dps = (0.0, 0.0, 0.0)
        self.timer = 0.0

    def update(self, gyro_dps, dt):
        """Returns the corrected gyro (dps). Same order as FusionOffsetUpdate: subtract, then adapt."""
        o = self.offset_dps
        g = (gyro_dps[0] - o[0], gyro_dps[1] - o[1], gyro_dps[2] - o[2])
        if not self.enabled:
            return g
        if abs(g[0]) > self.THRESHOLD_DPS or abs(g[1]) > self.THRESHOLD_DPS or abs(g[2]) > self.THRESHOLD_DPS:
            self.timer = 0.0
            return g
        if self.timer < self.TIMEOUT_S:
            self.timer += dt
            return g
        k = 2.0 * math.pi * self.CUTOFF_HZ * dt
        self.offset_dps = (o[0] + g[0] * k, o[1] + g[1] * k, o[2] + g[2] * k)
        return g


class ImuPipeline(object):
    """Report bytes in, Unity-frame orientation out.

    feed(report) classifies the report (IMU packet / 0xAA response / other), parses it, maps the
    axes, estimates the start-up bias, fuses, and returns the head orientation as a Unity quaternion
    (x, y, z, w), or None while calibrating or when the report is not an IMU packet.

    dt_source "device" integrates with the packet timestamps (nanoseconds, §5); "host" uses the
    host_time passed to feed() (a fallback in case the Ultra's timestamp unit differs).
    """

    MAX_DT_S = 0.1  # bridge parameter: larger gaps between samples are skipped, not integrated

    def __init__(self, kp=0.5, still_seconds=1.5, bias_tracking=True, give_up_seconds=15.0, dt_source="device"):
        self.filter = MahonyFilter(kp=kp)
        self.bias = StillBiasEstimator(seconds=still_seconds, give_up_seconds=give_up_seconds)
        self.tracker = GyroOffsetTracker(enabled=bias_tracking)
        self.dt_source = dt_source
        self.last_ts_ns = None
        self.last_host_time = None
        self.last_packet = None
        self.last_gyro_dps = (0.0, 0.0, 0.0)  # corrected, RUB
        self.last_dt = 0.0
        self.imu_packets = 0
        self.bad_dt = 0
        self.last_response = None
        self.response_count = 0
        self.other = {}  # first-two-bytes hex -> count, for reports that are neither IMU nor 0xAA
        self.started = False

    def feed(self, report, host_time=None):
        b = bytes(report)
        if not b:
            return None
        pkt = parse_imu_packet(b)
        if pkt is None:
            resp = parse_command_response(b)
            if resp is not None:
                self.last_response = resp
                self.response_count += 1
            else:
                key = b[:2].hex()
                self.other[key] = self.other.get(key, 0) + 1
            return None
        return self.feed_packet(pkt, host_time)

    def unity_q(self):
        return rub_to_unity_quat(self.filter.q)

    def _dt(self, pkt, host_time):
        if self.dt_source == "host" and host_time is not None:
            prev, self.last_host_time = self.last_host_time, host_time
            return None if prev is None else host_time - prev
        prev, self.last_ts_ns = self.last_ts_ns, pkt.timestamp_ns
        return None if prev is None else (pkt.timestamp_ns - prev) * 1e-9

    def feed_packet(self, pkt, host_time=None):
        self.imu_packets += 1
        self.last_packet = pkt
        dt = self._dt(pkt, host_time)
        if dt is None:
            return None
        if not (0.0 < dt <= self.MAX_DT_S):
            self.bad_dt += 1
            return None
        self.last_dt = dt
        gyro_dps = device_to_rub(pkt.gyro_dps)
        accel_g = device_to_rub(pkt.accel_g)
        if not self.bias.done:
            if self.bias.add(gyro_dps, accel_g, dt):
                self.filter.init_from_accel(self.bias.mean_accel_g)
                self.started = True
            return None
        b = self.bias.bias_dps
        g = self.tracker.update((gyro_dps[0] - b[0], gyro_dps[1] - b[1], gyro_dps[2] - b[2]), dt)
        self.last_gyro_dps = g
        k = math.pi / 180.0
        q = self.filter.update((g[0] * k, g[1] * k, g[2] * k), accel_g, dt)
        return rub_to_unity_quat(q)


# --------------------------------------------------------------------------------------------------
# Status: rate and yaw drift (bridge parameters)
# --------------------------------------------------------------------------------------------------

def wrap_deg(a):
    return (a + 180.0) % 360.0 - 180.0


class DriftMonitor(object):
    """Unwrapped Unity yaw over time, the share of 'still' samples, and the device sample rate."""

    STILL_DPS = GyroOffsetTracker.THRESHOLD_DPS

    def __init__(self):
        self.yaw_total = 0.0
        self._last_yaw = None
        self._win_yaw0 = 0.0
        self._win_n = 0
        self._win_still = 0
        self._win_ts0 = None
        self._win_ts1 = None

    def add(self, q_unity, gyro_dps=(0.0, 0.0, 0.0), ts_ns=None):
        yaw = unity_euler_deg(q_unity)[1]
        if self._last_yaw is not None:
            self.yaw_total += wrap_deg(yaw - self._last_yaw)
        self._last_yaw = yaw
        self._win_n += 1
        if max(abs(gyro_dps[0]), abs(gyro_dps[1]), abs(gyro_dps[2])) < self.STILL_DPS:
            self._win_still += 1
        if ts_ns is not None:
            if self._win_ts0 is None:
                self._win_ts0 = ts_ns
            self._win_ts1 = ts_ns

    def window(self, seconds):
        """Close the current window. Returns (yaw drift deg/min, still share 0..1, device Hz or None)."""
        drift = (self.yaw_total - self._win_yaw0) / seconds * 60.0 if seconds > 0 else 0.0
        still = self._win_still / self._win_n if self._win_n else 0.0
        dev_hz = None
        if self._win_ts0 is not None and self._win_ts1 is not None and self._win_ts1 > self._win_ts0:
            dev_hz = (self._win_n - 1) / ((self._win_ts1 - self._win_ts0) * 1e-9)
        self._win_yaw0 = self.yaw_total
        self._win_n = 0
        self._win_still = 0
        self._win_ts0 = None
        self._win_ts1 = None
        return drift, still, dev_hz


# --------------------------------------------------------------------------------------------------
# §8 Wire format (pinned): one UDP datagram = {"t": seconds since bridge start, "q": [x, y, z, w]}
# --------------------------------------------------------------------------------------------------

def encode_datagram(t, q, **extra):
    """UTF-8 JSON, keys in order t, q, then any extras. q must already be in Unity's frame.
    Raises ValueError on a non-finite value (JSON has no NaN)."""
    vals = (t, q[0], q[1], q[2], q[3])
    if not all(math.isfinite(v) for v in vals):
        raise ValueError("non-finite value in datagram")
    s = '{"t":%.6f,"q":[%.7f,%.7f,%.7f,%.7f]' % vals
    for k, v in extra.items():
        s += "," + json.dumps(k) + ":" + json.dumps(v)
    return (s + "}").encode("utf-8")


class FakeHeadMotion(object):
    """Smooth synthetic head motion for --fake (Unity frame): slow yaw/pitch/roll sines, in degrees."""

    YAW_DEG, YAW_HZ = 25.0, 0.10
    PITCH_DEG, PITCH_HZ = 12.0, 0.17
    ROLL_DEG, ROLL_HZ = 6.0, 0.23

    def euler(self, t):
        tau = 2.0 * math.pi
        return (self.PITCH_DEG * math.sin(tau * self.PITCH_HZ * t),
                self.YAW_DEG * math.sin(tau * self.YAW_HZ * t),
                self.ROLL_DEG * math.sin(tau * self.ROLL_HZ * t))

    def quat(self, t):
        p, y, r = self.euler(t)
        return unity_quat_from_euler_deg(p, y, r)


def choose_imu_interface(devices):
    """Pick the IMU interface from hid.enumerate() dicts: the first device with a known XREAL pid
    whose interface_number is that model's IMU interface (§2). Returns (device dict, Model) or
    (None, None)."""
    for d in devices:
        if d.get("vendor_id") != VENDOR_ID:
            continue
        model = MODELS.get(d.get("product_id"))
        if model is not None and d.get("interface_number") == model.imu_iface:
            return d, model
    return None, None
