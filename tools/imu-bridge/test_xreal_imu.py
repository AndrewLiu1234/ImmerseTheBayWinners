"""Tests for tools/imu-bridge (no hardware, no network).

Run from the repo root:  python3 -m unittest discover -s tools/imu-bridge -p 'test_*.py'

The packet and command bytes below are copied from docs/apis/glasses/imu-protocol.md (§3 and §11);
DocCopyTest fails if the doc and these copies ever drift apart.
"""
import json
import math
import os
import random
import re
import sys
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import bridge  # noqa: E402  (imports no hardware modules at import time)
import xreal_imu as xi  # noqa: E402

DOC = os.path.join(HERE, "..", "..", "docs", "apis", "glasses", "imu-protocol.md")

# Copied from imu-protocol.md, block "test-bytes: imu_packet_example" (SYNTHETIC packet, §11).
IMU_PACKET_EXAMPLE_HEX = """
01 02 2D 05 00 CA 9A 3B 00 00 00 00 01 00 E8 03
00 00 DC 05 00 3C F6 FF 00 00 00 01 00 00 20 00
00 00 00 00 00 F0 FF 00 20 00 00 01 00 00 00 64
FA 80 83 7F 00 80 00 00 00 00 00 00 00 00 00 00
"""
# Copied from imu-protocol.md §3, blocks "test-bytes: start_imu_exact" and "test-bytes: stop_imu_exact".
START_IMU_EXACT_HEX = "AA C5 D1 21 42 04 00 19 01"
STOP_IMU_EXACT_HEX = "AA 53 E1 26 35 04 00 19 00"


def hx(s):
    return bytes.fromhex(" ".join(s.split()))


def doc_block(name):
    with open(DOC, encoding="utf-8") as f:
        text = f.read()
    m = re.search(r"<!-- test-bytes: %s -->\s*```\n(.*?)```" % re.escape(name), text, re.S)
    if not m:
        raise AssertionError("block %s not found in %s" % (name, DOC))
    return hx(m.group(1))


# ------------------------------------------------------------------------------------------------
# Helpers: a simulated device that rotates rigidly and emits raw packets
# ------------------------------------------------------------------------------------------------

def rub_to_device(v):
    # device_to_rub is (-x, z, y), which is its own inverse.
    return xi.device_to_rub(v)


def simulate(segments, rate_hz=1000, q0=(0.0, 0.0, 0.0, 1.0), gyro_bias_dev_dps=(0.0, 0.0, 0.0), t0_ns=10**9):
    """segments: list of (seconds, body rate in RUB deg/s). Yields (packet bytes, true q_rub)."""
    q = q0
    dt = 1.0 / rate_hz
    ts = t0_ns
    for seconds, rate_rub_dps in segments:
        for _ in range(int(round(seconds * rate_hz))):
            accel_rub = xi.q_rotate(xi.q_conj(q), (0.0, 1.0, 0.0))  # the accelerometer reads "up", in g
            gyro_dev = rub_to_device(rate_rub_dps)
            gyro_dev = tuple(gyro_dev[i] + gyro_bias_dev_dps[i] for i in range(3))
            accel_dev = rub_to_device(accel_rub)
            pkt = xi.encode_imu_packet(
                timestamp_ns=ts,
                gyro_multiplier=1, gyro_divisor=1000, gyro_raw=tuple(int(round(g * 1000)) for g in gyro_dev),
                accel_multiplier=1, accel_divisor=1000000, accel_raw=tuple(int(round(a * 1e6)) for a in accel_dev))
            yield pkt, q
            w = tuple(math.radians(c) for c in rate_rub_dps)
            n = math.sqrt(sum(c * c for c in w))
            if n > 0:
                q = xi.q_normalize(xi.q_mul(q, xi.q_from_axis_angle(tuple(c / n for c in w), n * dt)))
            ts += int(round(dt * 1e9))


def run_pipeline(segments, **kw):
    pipe = xi.ImuPipeline()
    out = None
    for pkt, _ in simulate(segments, **kw):
        q = pipe.feed(pkt)
        if q is not None:
            out = q
    return pipe, out


def unity_rotate_formula(rotation, point):
    """Literal port of Unity 2021.3 Quaternion.cs operator*(Quaternion, Vector3) (quoted in imu-protocol.md §8)."""
    rx, ry, rz, rw = rotation
    x, y, z = rx * 2.0, ry * 2.0, rz * 2.0
    xx, yy, zz = rx * x, ry * y, rz * z
    xy, xz, yz = rx * y, rx * z, ry * z
    wx, wy, wz = rw * x, rw * y, rw * z
    px, py, pz = point
    return ((1.0 - (yy + zz)) * px + (xy - wz) * py + (xz + wy) * pz,
            (xy + wz) * px + (1.0 - (xx + zz)) * py + (yz - wx) * pz,
            (xz - wy) * px + (yz + wx) * py + (1.0 - (xx + yy)) * pz)


# ------------------------------------------------------------------------------------------------
# Bytes copied from the doc
# ------------------------------------------------------------------------------------------------

class DocCopyTest(unittest.TestCase):
    def test_copies_match_the_doc(self):
        self.assertEqual(hx(IMU_PACKET_EXAMPLE_HEX), doc_block("imu_packet_example"))
        self.assertEqual(hx(START_IMU_EXACT_HEX), doc_block("start_imu_exact"))
        self.assertEqual(hx(STOP_IMU_EXACT_HEX), doc_block("stop_imu_exact"))


class PacketParserTest(unittest.TestCase):
    def setUp(self):
        self.raw = hx(IMU_PACKET_EXAMPLE_HEX)

    def test_example_packet_fields(self):
        p = xi.parse_imu_packet(self.raw)
        self.assertIsNotNone(p)
        self.assertEqual(len(self.raw), 64)
        self.assertEqual(p.signature, b"\x01\x02")
        self.assertEqual(p.temperature_raw, 1325)
        self.assertEqual(p.timestamp_ns, 1000000000)
        self.assertEqual((p.gyro_multiplier, p.gyro_divisor), (1, 1000))
        self.assertEqual(p.gyro_raw, (1500, -2500, 0))
        self.assertEqual((p.accel_multiplier, p.accel_divisor), (1, 8192))
        self.assertEqual(p.accel_raw, (0, -4096, 8192))
        self.assertEqual((p.mag_multiplier, p.mag_divisor), (1, 100))
        self.assertEqual(p.mag_raw, (250, -125, 0))
        self.assertEqual(p.checksum, 0)

    def test_example_packet_scaled(self):
        p = xi.parse_imu_packet(self.raw)
        for got, want in zip(p.gyro_dps, (1.5, -2.5, 0.0)):
            self.assertAlmostEqual(got, want, places=9)
        for got, want in zip(p.accel_g, (0.0, -0.5, 1.0)):
            self.assertAlmostEqual(got, want, places=9)
        for got, want in zip(p.mag, (2.5, -1.25, 0.0)):
            self.assertAlmostEqual(got, want, places=9)
        self.assertAlmostEqual(p.temperature_c, 1325 / 132.48 + 25.0, places=9)
        self.assertAlmostEqual(p.temperature_c, 35.0, places=1)

    def test_ultra_512_byte_report(self):
        report = self.raw + bytes(448)
        self.assertEqual(xi.parse_imu_packet(report), xi.parse_imu_packet(self.raw))

    def test_rejects_short_wrong_header_and_zero_divisor(self):
        self.assertIsNone(xi.parse_imu_packet(self.raw[:63]))
        self.assertIsNone(xi.parse_imu_packet(b"\x01\x03" + self.raw[2:]))
        self.assertIsNone(xi.parse_imu_packet(hx(STOP_IMU_EXACT_HEX) + bytes(55)))
        zero_div = bytearray(self.raw)
        zero_div[0x0E:0x12] = bytes(4)
        self.assertIsNone(xi.parse_imu_packet(bytes(zero_div)))

    def test_encoder_reproduces_the_doc_packet(self):
        made = xi.encode_imu_packet(temperature_raw=1325, timestamp_ns=1000000000, gyro_multiplier=1,
                                    gyro_divisor=1000, gyro_raw=(1500, -2500, 0), accel_multiplier=1,
                                    accel_divisor=8192, accel_raw=(0, -4096, 8192), mag_multiplier=1,
                                    mag_divisor=100, mag_raw=(250, -125, 0))
        self.assertEqual(made, self.raw)
        self.assertEqual(len(xi.encode_imu_packet(length=512)), 512)


class CommandTest(unittest.TestCase):
    def test_crc_is_standard_crc32(self):
        # Spot entries of Monado's crc32_table (quoted in imu-protocol.md §3).
        self.assertEqual(xi.CRC32_TABLE[0], 0x00000000)
        self.assertEqual(xi.CRC32_TABLE[1], 0x77073096)
        self.assertEqual(xi.CRC32_TABLE[254], 0x5A05DF1B)
        self.assertEqual(xi.CRC32_TABLE[255], 0x2D02EF8D)
        self.assertEqual(xi.crc32_checksum(b"123456789"), 0xCBF43926)
        rnd = random.Random(7)
        for _ in range(200):
            data = bytes(rnd.randrange(256) for _ in range(rnd.randrange(0, 300)))
            self.assertEqual(xi.crc32_checksum(data), zlib.crc32(data))

    def test_start_and_stop_bytes(self):
        self.assertEqual(xi.start_imu_command(pad_to=0), hx(START_IMU_EXACT_HEX))
        self.assertEqual(xi.stop_imu_command(pad_to=0), hx(STOP_IMU_EXACT_HEX))
        padded = xi.start_imu_command()  # default: ar-drivers-rs 64-byte form
        self.assertEqual(len(padded), 64)
        self.assertEqual(padded[:9], hx(START_IMU_EXACT_HEX))
        self.assertEqual(padded[9:], bytes(55))

    def test_frame_layout(self):
        cmd = xi.build_command(0x14)  # GET_CAL_DATA_LENGTH, no data
        self.assertEqual(cmd[0], 0xAA)
        self.assertEqual(int.from_bytes(cmd[5:7], "little"), 3)
        self.assertEqual(cmd[7], 0x14)
        self.assertEqual(int.from_bytes(cmd[1:5], "little"), zlib.crc32(cmd[5:8]))

    def test_reply_parser(self):
        r = xi.parse_command_response(hx(STOP_IMU_EXACT_HEX) + bytes(55))
        self.assertEqual((r.msgid, r.length, r.data, r.crc_ok), (0x19, 4, b"\x00", True))
        self.assertIsNone(xi.parse_command_response(hx(IMU_PACKET_EXAMPLE_HEX)))


class InterfaceChoiceTest(unittest.TestCase):
    def dev(self, pid, iface, path):
        return {"vendor_id": 0x3318, "product_id": pid, "interface_number": iface, "path": path,
                "usage_page": 0xFF00, "usage": 1, "product_string": "x"}

    def test_ultra_uses_interface_2(self):
        devs = [self.dev(0x0426, i, b"p%d" % i) for i in (0, 1, 2, 3)]
        d, model = xi.choose_imu_interface(devs)
        self.assertEqual((d["path"], model.name), (b"p2", "XREAL Air 2 Ultra"))
        self.assertIn("<- IMU", bridge.describe(d))
        self.assertIn("<- MCU", bridge.describe(devs[0]))

    def test_older_air_uses_interface_3_and_unknowns_are_skipped(self):
        devs = [{"vendor_id": 0x1234, "product_id": 0x0424, "interface_number": 3, "path": b"other"},
                self.dev(0x0999, 3, b"unknown"), self.dev(0x0424, 4, b"mcu"), self.dev(0x0424, 3, b"imu")]
        d, model = xi.choose_imu_interface(devs)
        self.assertEqual((d["path"], model.imu_iface), (b"imu", 3))
        self.assertEqual(xi.choose_imu_interface([self.dev(0x0426, -1, b"x")]), (None, None))


# ------------------------------------------------------------------------------------------------
# Axes and the Unity frame
# ------------------------------------------------------------------------------------------------

class AxisMappingTest(unittest.TestCase):
    def test_device_to_rub_matches_the_sources(self):
        # imu-protocol.md §7: device (x, y, z) -> (-x, z, y); level and still reads (0, 0, +1 g).
        self.assertEqual(xi.device_to_rub((1.0, 2.0, 3.0)), (-1.0, 3.0, 2.0))
        self.assertEqual(xi.device_to_rub(xi.device_to_rub((1.0, 2.0, 3.0))), (1.0, 2.0, 3.0))
        self.assertEqual(xi.device_to_rub((0.0, 0.0, 1.0)), (0.0, 1.0, 0.0))

    def test_rub_to_unity_is_the_z_mirror(self):
        rnd = random.Random(3)
        for _ in range(100):
            q = xi.q_normalize(tuple(rnd.uniform(-1, 1) for _ in range(4)))
            v = tuple(rnd.uniform(-1, 1) for _ in range(3))
            rub = xi.q_rotate(q, v)
            unity = unity_rotate_formula(xi.rub_to_unity_quat(q), (v[0], v[1], -v[2]))
            for a, b in zip(unity, (rub[0], rub[1], -rub[2])):
                self.assertAlmostEqual(a, b, places=9)

    def test_unity_euler_round_trip(self):
        for p, y, r in [(0, 0, 0), (30, 0, 0), (0, -45, 0), (0, 0, 20), (10, 120, -35), (-60, -170, 80)]:
            got = xi.unity_euler_deg(xi.unity_quat_from_euler_deg(p, y, r))
            for a, b in zip(got, (p, y, r)):
                self.assertAlmostEqual(a, b, places=6)

    def test_static_poses_from_gravity(self):
        # Raw device accel only (no rotation), as the sensor would report each pose.
        cases = [((0.0, 0.0, 1.0), (0.0, 0.0, 0.0)),                                # level
                 ((0.0, math.sin(math.radians(30)), math.cos(math.radians(30))), (30.0, 0.0, 0.0)),  # nod down
                 ((-math.sin(math.radians(20)), 0.0, math.cos(math.radians(20))), (0.0, 0.0, 20.0))]  # tilt left
        for accel_dev, want in cases:
            f = xi.MahonyFilter()
            f.init_from_accel(xi.device_to_rub(accel_dev))
            got = xi.unity_euler_deg(xi.rub_to_unity_quat(f.q))
            for a, b in zip(got, want):
                self.assertAlmostEqual(a, b, places=4)

    def _turn(self, device_axis, seconds=1.5, rate=20.0):
        rate_rub = xi.device_to_rub(tuple(c * rate for c in device_axis))
        _, q = run_pipeline([(2.0, (0.0, 0.0, 0.0)), (seconds, rate_rub)])
        return xi.unity_euler_deg(q)

    def test_gyro_axes_give_the_documented_unity_signs(self):
        # imu-protocol.md §8 human-test table, driven by raw device gyro axes.
        pitch, yaw, roll = self._turn((1.0, 0.0, 0.0))   # device +X: nod down
        self.assertAlmostEqual(pitch, 30.0, delta=0.3)
        self.assertAlmostEqual(yaw, 0.0, delta=0.3)
        pitch, yaw, roll = self._turn((0.0, 0.0, 1.0))   # device +Z (up): turn left
        self.assertAlmostEqual(yaw, -30.0, delta=0.3)
        self.assertAlmostEqual(pitch, 0.0, delta=0.3)
        pitch, yaw, roll = self._turn((0.0, 1.0, 0.0))   # device +Y (back): tilt left
        self.assertAlmostEqual(roll, 30.0, delta=0.3)
        self.assertAlmostEqual(yaw, 0.0, delta=0.3)

    def test_turn_left_moves_forward_to_minus_x(self):
        _, q = run_pipeline([(2.0, (0.0, 0.0, 0.0)), (3.0, (0.0, 30.0, 0.0))])  # 90 deg about up
        fwd = unity_rotate_formula(q, (0.0, 0.0, 1.0))
        self.assertAlmostEqual(fwd[0], -1.0, delta=0.01)
        self.assertAlmostEqual(fwd[2], 0.0, delta=0.02)


# ------------------------------------------------------------------------------------------------
# Fusion
# ------------------------------------------------------------------------------------------------

class FusionTest(unittest.TestCase):
    def test_stationary_level_is_identity_and_bias_is_estimated(self):
        bias = (0.4, -0.2, 0.1)
        pipe, q = run_pipeline([(4.0, (0.0, 0.0, 0.0))], gyro_bias_dev_dps=bias)
        self.assertTrue(pipe.started)
        for a in xi.unity_euler_deg(q):
            self.assertAlmostEqual(a, 0.0, delta=0.05)
        for a, b in zip(pipe.bias.bias_dps, xi.device_to_rub(bias)):
            self.assertAlmostEqual(a, b, delta=0.002)
        self.assertFalse(pipe.bias.forced)

    def test_stationary_tilted_keeps_absolute_pitch_and_zero_yaw(self):
        q0 = xi.q_from_axis_angle((1.0, 0.0, 0.0), math.radians(-25.0))  # RUB: nose down 25 deg
        q0 = xi.q_mul(xi.q_from_axis_angle((0.0, 1.0, 0.0), math.radians(40.0)), q0)  # facing 40 deg left
        _, q = run_pipeline([(3.0, (0.0, 0.0, 0.0))], q0=q0)
        pitch, yaw, roll = xi.unity_euler_deg(q)
        self.assertAlmostEqual(pitch, 25.0, delta=0.05)
        self.assertAlmostEqual(yaw, 0.0, delta=0.05)  # yaw is relative to the start heading
        self.assertAlmostEqual(roll, 0.0, delta=0.05)

    def test_constant_yaw_rate_gives_the_expected_angle(self):
        _, q = run_pipeline([(2.0, (0.0, 0.0, 0.0)), (2.0, (0.0, 30.0, 0.0))], gyro_bias_dev_dps=(0.3, 0.3, -0.3))
        pitch, yaw, roll = xi.unity_euler_deg(q)
        self.assertAlmostEqual(yaw, -60.0, delta=0.2)  # 30 deg/s for 2 s, turning left
        self.assertAlmostEqual(pitch, 0.0, delta=0.1)
        self.assertAlmostEqual(roll, 0.0, delta=0.1)

    def test_gravity_pulls_a_wrong_tilt_back(self):
        f = xi.MahonyFilter(kp=0.5)
        f.init_from_accel((0.0, 1.0, 0.0))
        f.q = xi.q_from_axis_angle((1.0, 0.0, 0.0), math.radians(10.0))  # wrong by 10 deg
        for _ in range(10000):  # 10 s at 1 kHz, still and level
            f.update((0.0, 0.0, 0.0), (0.0, 1.0, 0.0), 0.001)
        self.assertLess(abs(xi.unity_euler_deg(xi.rub_to_unity_quat(f.q))[0]), 0.1)

    def test_a_steady_turn_at_start_is_not_taken_as_bias(self):
        pipe = xi.ImuPipeline(give_up_seconds=1.0)
        outs = [pipe.feed(p) for p, _ in simulate([(1.5, (0.0, 40.0, 0.0))])]  # constant 40 deg/s: zero spread
        self.assertGreater(pipe.bias.restarts, 0)
        self.assertIsNone(outs[900])  # nothing sent while calibrating
        self.assertTrue(pipe.bias.forced)
        self.assertEqual(pipe.bias.bias_dps, (0.0, 0.0, 0.0))
        self.assertIsNotNone(outs[-1])

    def test_offset_tracker_follows_slow_residual_only_when_still(self):
        t = xi.GyroOffsetTracker()
        for _ in range(int(35.0 / 0.001)):
            t.update((0.5, 0.0, 0.0), 0.001)
        want = 0.5 * (1.0 - math.exp(-2.0 * math.pi * 0.02 * 30.0))
        self.assertAlmostEqual(t.offset_dps[0], want, delta=0.01)
        moving = xi.GyroOffsetTracker()
        for _ in range(int(35.0 / 0.001)):
            moving.update((10.0, 0.0, 0.0), 0.001)
        self.assertEqual(moving.offset_dps, (0.0, 0.0, 0.0))

    def test_bad_timestamps_are_skipped(self):
        pipe = xi.ImuPipeline()
        pkt = xi.encode_imu_packet(timestamp_ns=5)
        for _ in range(3):
            self.assertIsNone(pipe.feed(pkt))
        self.assertEqual((pipe.imu_packets, pipe.bad_dt), (3, 2))

    def test_reports_are_classified(self):
        pipe = xi.ImuPipeline()
        pipe.feed(hx(STOP_IMU_EXACT_HEX) + bytes(55))
        pipe.feed(b"\x02\x05" + bytes(62))
        self.assertEqual((pipe.response_count, pipe.last_response.msgid), (1, 0x19))
        self.assertEqual(pipe.other, {"0205": 1})

    def test_drift_monitor(self):
        m = xi.DriftMonitor()
        for i in range(5001):  # yaw turning right at 1 deg/s for 5 s, sampled at 1 kHz
            m.add(xi.unity_quat_from_euler_deg(0.0, i * 0.001, 0.0), (0.0, 0.0, 0.0), 10**9 + i * 10**6)
        drift, still, dev_hz = m.window(5.0)
        self.assertAlmostEqual(drift, 60.0, delta=0.1)
        self.assertEqual(still, 1.0)
        self.assertAlmostEqual(dev_hz, 1000.0, delta=0.5)


# ------------------------------------------------------------------------------------------------
# Wire format (pinned) and the bridge's command line
# ------------------------------------------------------------------------------------------------

class WireFormatTest(unittest.TestCase):
    def test_datagram_is_t_then_q_unit_quaternion(self):
        q = xi.unity_quat_from_euler_deg(10.0, -20.0, 5.0)
        raw = xi.encode_datagram(12.5, q, seq=7, src="imu")
        self.assertTrue(raw.startswith(b'{"t":'))
        obj = json.loads(raw.decode("utf-8"))
        self.assertEqual(list(obj.keys())[:2], ["t", "q"])
        self.assertIsInstance(obj["t"], float)
        self.assertAlmostEqual(obj["t"], 12.5, places=6)
        self.assertEqual(len(obj["q"]), 4)
        self.assertTrue(all(isinstance(c, float) for c in obj["q"]))
        self.assertAlmostEqual(sum(c * c for c in obj["q"]), 1.0, places=5)
        for a, b in zip(obj["q"], q):
            self.assertAlmostEqual(a, b, places=6)
        self.assertEqual((obj["seq"], obj["src"]), (7, "imu"))

    def test_non_finite_values_are_refused(self):
        with self.assertRaises(ValueError):
            xi.encode_datagram(float("nan"), (0.0, 0.0, 0.0, 1.0))
        with self.assertRaises(ValueError):
            xi.encode_datagram(1.0, (0.0, float("inf"), 0.0, 1.0))

    def test_fake_motion_is_smooth_and_unit(self):
        m = xi.FakeHeadMotion()
        prev = m.quat(0.0)
        self.assertAlmostEqual(prev[3], 1.0, places=9)
        for i in range(1, 2000):
            q = m.quat(i * 0.004)
            self.assertAlmostEqual(sum(c * c for c in q), 1.0, places=9)
            self.assertGreater(abs(sum(a * b for a, b in zip(q, prev))), 0.9999)  # < ~1.6 deg per step
            prev = q
        p, y, r = xi.unity_euler_deg(m.quat(1.3))
        for a, b in zip((p, y, r), m.euler(1.3)):
            self.assertAlmostEqual(a, b, places=6)

    def test_command_line_keeps_list_and_port(self):
        a = bridge.parse_args([])
        self.assertEqual((a.port, a.list, a.fake, a.send_hz), (47800, False, False, 250.0))
        a = bridge.parse_args(["--port", "47801", "--list"])
        self.assertEqual((a.port, a.list), (47801, True))
        self.assertTrue(bridge.parse_args(["--fake"]).fake)


if __name__ == "__main__":
    unittest.main()
