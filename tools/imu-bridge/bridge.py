#!/usr/bin/env python3
"""IMU bridge: XREAL Air 2 Ultra (USB HID) -> orientation -> UDP JSON for Unity.

Protocol (ids, interfaces, enable command, packet layout, axes): docs/apis/glasses/imu-protocol.md,
copied from the Monado xreal_air driver, ar-drivers-rs and xrealAirLinuxDriver. Do not guess bytes.
hidapi calls: docs/apis/python/hidapi.md.

Wire format (pinned, the Unity receiver depends on it): one UDP datagram to 127.0.0.1:<port> is one
UTF-8 JSON object {"t": <seconds since the bridge started>, "q": [x, y, z, w], ...extra keys}, with q
already in Unity's frame (left-handed, +X right, +Y up, +Z forward).

  python3 tools/imu-bridge/bridge.py --list       # show the glasses' HID interfaces
  python3 tools/imu-bridge/bridge.py              # stream the real IMU (needs hidapi)
  python3 tools/imu-bridge/bridge.py --fake       # synthetic head motion, no glasses, no hidapi
"""
import argparse
import os
import signal
import socket
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xreal_imu as xi  # noqa: E402  (pure module, no hardware access)

HOST = "127.0.0.1"
STATUS_EVERY_S = 5.0
NO_DATA_RETRY_S = 2.0
EXIT_OK, EXIT_NO_HIDAPI, EXIT_NO_DEVICE, EXIT_OPEN_FAILED, EXIT_NO_DATA, EXIT_LOST = 0, 2, 3, 4, 5, 6

# How a command is written (imu-protocol.md §3): ar-drivers-rs's zero-padded 64-byte frame, Monado's and
# xrealAirLinuxDriver's exact frame, and both behind ar-drivers-rs's Windows-style leading 0x00 (on macOS
# hidapi strips a leading 0x00 and passes report id 0; hidapi.md). Retries cycle through all four.
CMD_FORMS = (("64", 64, False), ("exact", 0, False), ("0x00+64", 64, True), ("0x00+exact", 0, True))


def command_bytes(make, form):
    """make: xi.start_imu_command or xi.stop_imu_command; form: an entry of CMD_FORMS."""
    _, pad, report_id_zero = form
    frame = make(pad)
    return b"\x00" + frame if report_id_zero else frame


def forms_from(first):
    i = [f[0] for f in CMD_FORMS].index(first)
    return CMD_FORMS[i:] + CMD_FORMS[:i]


def import_hid():
    """Import the hidapi package (module name `hid`) or exit with a clear message."""
    try:
        import hid
    except ImportError:
        print("hidapi is not installed. Run:  python3 -m pip install --user hidapi   (or use --fake)",
              file=sys.stderr)
        sys.exit(EXIT_NO_HIDAPI)
    if not hasattr(hid, "device") or not hasattr(hid, "enumerate"):
        print("The `hid` module found is not the hidapi package (%s). Run:  python3 -m pip uninstall hid  "
              "then  python3 -m pip install --user hidapi" % getattr(hid, "__file__", "?"), file=sys.stderr)
        sys.exit(EXIT_NO_HIDAPI)
    return hid


def describe(d):
    pid = d.get("product_id", 0)
    iface = d.get("interface_number")
    model = xi.MODELS.get(pid)
    role = ""
    if model is not None:
        role = " <- IMU" if iface == model.imu_iface else (" <- MCU" if iface == model.mcu_iface else "")
    return ("pid=0x%04x (%s) iface=%s usage_page=0x%x usage=0x%x path=%r product=%r%s"
            % (pid, model.name if model else "unknown model", iface, d.get("usage_page", 0) or 0,
               d.get("usage", 0) or 0, d.get("path"), d.get("product_string", ""), role))


def list_devices(hid):
    devs = hid.enumerate(xi.VENDOR_ID, 0)
    if not devs:
        print("No XREAL HID devices (vendor 0x%04x). Plug the glasses into the Mac and run --list again."
              % xi.VENDOR_ID)
        return EXIT_NO_DEVICE
    for d in sorted(devs, key=lambda d: (d.get("product_id", 0), d.get("interface_number", -1),
                                         d.get("usage_page", 0), d.get("usage", 0))):
        print(describe(d))
    return EXIT_OK


def hexdump(b):
    return " ".join("%02x" % x for x in b)


class Status(object):
    """Counts for the 5-second status line."""

    def __init__(self, t0):
        self.t0 = t0
        self.win_start = t0
        self.next_at = t0 + STATUS_EVERY_S
        self.imu = 0
        self.sent = 0
        self.send_errors = 0

    def due(self, now):
        return now >= self.next_at

    def close(self, now):
        span = max(now - self.win_start, 1e-9)
        imu_hz, udp_hz = self.imu / span, self.sent / span
        self.imu = self.sent = 0
        self.win_start = now
        self.next_at = now + STATUS_EVERY_S
        return span, imu_hz, udp_hz


class Sender(object):
    """Rate-limited UDP sender. send_hz <= 0 sends every sample."""

    def __init__(self, port, send_hz):
        self.addr = (HOST, port)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.interval = 1.0 / send_hz if send_hz > 0 else 0.0
        self.next_at = 0.0
        self.seq = 0

    def maybe_send(self, now, t, q, src, status):
        if self.interval and now < self.next_at:
            return False
        self.next_at = max(self.next_at + self.interval, now - self.interval) if self.interval else 0.0
        try:
            self.sock.sendto(xi.encode_datagram(t, q, seq=self.seq, src=src), self.addr)
            status.sent += 1
        except (OSError, ValueError):
            status.send_errors += 1
        self.seq += 1
        return True

    def close(self):
        self.sock.close()


def write_command(dev, cmd):
    n = dev.write(cmd)
    return n if n is not None else -1


def run_device(args):
    hid = import_hid()
    devs = hid.enumerate(xi.VENDOR_ID, 0)
    info, model = None, None
    if args.path:
        want = args.path.encode() if isinstance(args.path, str) else args.path
        info = next((d for d in devs if d.get("path") == want), None)
        model = xi.MODELS.get(info.get("product_id")) if info else None
    else:
        info, model = xi.choose_imu_interface(devs)
    if info is None:
        if not devs:
            print("No XREAL glasses found (no HID device with vendor 0x%04x). Plug them in, then run --list."
                  % xi.VENDOR_ID, file=sys.stderr)
        else:
            print("XREAL HID devices are present, but none is a known IMU interface:", file=sys.stderr)
            for d in devs:
                print("  " + describe(d), file=sys.stderr)
            print("If iface shows -1, pick the IMU entry and pass --path '<path>'.", file=sys.stderr)
        return EXIT_NO_DEVICE
    read_len = max(model.imu_report_max if model else 512, xi.IMU_PACKET_LEN)
    print("IMU interface: " + describe(info))

    dev = hid.device()
    try:
        dev.open_path(info["path"])
    except (IOError, OSError) as e:
        print("Could not open the IMU interface: %s (%s)." % (e, dev.error()), file=sys.stderr)
        print("hidapi opens HID devices exclusively on macOS: quit any other program that uses the glasses "
              "(XREAL's app, another bridge) and retry.", file=sys.stderr)
        return EXIT_OPEN_FAILED

    sender = Sender(args.port, args.send_hz)
    pipeline = xi.ImuPipeline(kp=args.kp, still_seconds=args.still_seconds,
                              bias_tracking=not args.no_bias_tracking, dt_source=args.dt_source)
    monitor = xi.DriftMonitor()
    forms = forms_from(args.cmd_len)
    form = forms[0]
    code = EXIT_OK

    def send_start(attempt):
        """Write START_IMU_DATA in forms[attempt]; on a write error try the next form at once."""
        while attempt < len(forms):
            f = forms[attempt]
            n = write_command(dev, command_bytes(xi.start_imu_command, f))
            if n >= 0:
                print("Sent START_IMU_DATA (0x19, 0x01) as '%s': %d bytes." % (f[0], n))
                return attempt, f
            print("Writing START_IMU_DATA as '%s' failed (%s)." % (f[0], dev.error()))
            attempt += 1
        return attempt, forms[-1]

    try:
        # Drop anything already queued (Monado sensor_clear_queue does the same), then start the stream.
        for _ in range(64):
            if not dev.read(read_len, 5):
                break
        t0 = time.monotonic()
        status = Status(t0)
        attempt, form = send_start(0)
        print("Hold the glasses still for ~2 s (start-up gyro bias)...")
        last_start = t0
        dumped = 0
        announced = False
        flowing = False
        responses_shown = 0
        last_watch = 0.0
        while True:
            data = dev.read(read_len, 20)
            now = time.monotonic()
            if args.duration and now - t0 >= args.duration:
                break
            if data:
                if dumped < args.dump:
                    raw = bytes(data)
                    print("report %d: len=%d first64: %s%s" % (
                        dumped, len(raw), hexdump(raw[:64]),
                        "  (+%d more bytes, %d non-zero)" % (len(raw) - 64, sum(1 for x in raw[64:] if x))
                        if len(raw) > 64 else ""))
                    dumped += 1
                imu_before, responses_before = pipeline.imu_packets, pipeline.response_count
                q = pipeline.feed(data, host_time=now)
                status.imu += pipeline.imu_packets - imu_before
                if pipeline.imu_packets and not flowing:
                    print("IMU packets are arriving (%d-byte reports; start command form '%s')." % (len(data), form[0]))
                    flowing = True
                if pipeline.response_count != responses_before and responses_shown < 8:
                    r = pipeline.last_response
                    print("response: msgid=0x%02x len=%d data=%s crc_ok=%s" % (
                        r.msgid, r.length, hexdump(r.data[:8]), r.crc_ok))
                    responses_shown += 1
                if pipeline.started and not announced:
                    b = pipeline.bias
                    print("Gyro bias (deg/s, RUB axes) %+.3f %+.3f %+.3f%s; streaming to %s:%d"
                          % (b.bias_dps[0], b.bias_dps[1], b.bias_dps[2],
                             " (FORCED: the glasses never stayed still)" if b.forced else "",
                             HOST, args.port))
                    announced = True
                if q is not None:
                    monitor.add(q, pipeline.last_gyro_dps, pipeline.last_packet.timestamp_ns)
                    sender.maybe_send(now, now - t0, q, "imu", status)
                    if args.watch and now - last_watch >= 0.25:
                        p, y, r = xi.unity_euler_deg(q)
                        sys.stdout.write("\rUnity euler  pitch(x) %+7.1f  yaw(y) %+7.1f  roll(z) %+7.1f   "
                                         % (p, y, r))
                        sys.stdout.flush()
                        last_watch = now
            if pipeline.imu_packets == 0:
                if now - t0 >= args.no_data_timeout:
                    print("\nNo IMU packets in %.0f s (tried command forms: %s). Other reports seen: %s. "
                          "Replies: %d. Check that the glasses are plugged in and no other app holds them, "
                          "then run --list and --dump 20." % (
                              args.no_data_timeout, ", ".join(f[0] for f in forms[:attempt + 1]),
                              pipeline.other or "none", pipeline.response_count), file=sys.stderr)
                    code = EXIT_NO_DATA
                    break
                if now - last_start >= NO_DATA_RETRY_S and attempt + 1 < len(forms):
                    print("No IMU data yet.")
                    attempt, form = send_start(attempt + 1)
                    last_start = now
            if status.due(now):
                print_status(status, now, pipeline, monitor, args)
    except KeyboardInterrupt:
        print("\nCtrl+C")
    except (IOError, OSError) as e:
        print("\nLost the glasses: %s (%s)" % (e, dev.error()), file=sys.stderr)
        code = EXIT_LOST
    finally:
        try:  # xrealAirLinuxDriver stops the stream on close; use the form the start command last used
            write_command(dev, command_bytes(xi.stop_imu_command, form))
        except (IOError, OSError, ValueError):
            pass
        dev.close()
        sender.close()
    print("Stopped: %d IMU packets, %d datagrams sent." % (pipeline.imu_packets, sender.seq))
    return code


def print_status(status, now, pipeline, monitor, args):
    span, imu_hz, udp_hz = status.close(now)
    head = "\n" if args.watch else ""
    prefix = "%s[%6.1f s] imu %6.1f Hz" % (head, now - status.t0, imu_hz)
    if not pipeline.started:
        print("%s | calibrating gyro bias: hold the glasses still (restarts %d)%s"
              % (prefix, pipeline.bias.restarts, other_text(pipeline)))
        return
    drift, still, dev_hz = monitor.window(span)
    p, y, r = xi.unity_euler_deg(pipeline.unity_q())
    warn = ""
    if dev_hz and imu_hz > 0 and not (0.5 < dev_hz / imu_hz < 2.0):
        warn = " | WARNING device clock %.0f Hz vs host %.0f Hz: timestamp units? try --dt-source host" % (
            dev_hz, imu_hz)
    print("%s (device clock %s) | udp %6.1f Hz -> %s:%d | yaw drift %+6.2f deg/min (still %3.0f%%%s) | "
          "pitch %+6.1f yaw %+6.1f roll %+6.1f deg | %.1f C%s%s%s"
          % (prefix, "%.0f Hz" % dev_hz if dev_hz else "n/a", udp_hz, HOST, args.port, drift, still * 100,
             "" if still >= 0.9 else ", moving: not a drift number", p, y, r,
             pipeline.last_packet.temperature_c, " | bad dt %d" % pipeline.bad_dt if pipeline.bad_dt else "",
             " | send errors %d" % status.send_errors if status.send_errors else "", warn + other_text(pipeline)))


def other_text(pipeline):
    return " | other reports %s" % pipeline.other if pipeline.other else ""


def run_fake(args):
    motion = xi.FakeHeadMotion()
    hz = args.send_hz if args.send_hz > 0 else 250.0
    interval = 1.0 / hz
    sender = Sender(args.port, 0)  # the loop below already paces the samples
    t0 = time.monotonic()
    status = Status(t0)
    next_at = t0
    print("FAKE mode: synthetic head motion at %.0f Hz to %s:%d (no glasses). Ctrl+C to stop." % (hz, HOST, args.port))
    try:
        while True:
            now = time.monotonic()
            if args.duration and now - t0 >= args.duration:
                break
            if now < next_at:
                time.sleep(min(next_at - now, 0.002))
                continue
            t = now - t0
            q = motion.quat(t)
            sender.sock.sendto(xi.encode_datagram(t, q, seq=sender.seq, src="fake"), sender.addr)
            sender.seq += 1
            status.sent += 1
            next_at = max(next_at + interval, now - interval)
            if status.due(now):
                span, _, udp_hz = status.close(now)
                p, y, r = xi.unity_euler_deg(q)
                print("[%6.1f s] fake | udp %6.1f Hz -> %s:%d | pitch %+6.1f yaw %+6.1f roll %+6.1f deg (synthetic)"
                      % (t, udp_hz, HOST, args.port, p, y, r))
    except KeyboardInterrupt:
        print("\nCtrl+C")
    finally:
        sender.close()
    print("Stopped: %d datagrams sent." % sender.seq)
    return EXIT_OK


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=47800, help="UDP port on 127.0.0.1 (default 47800)")
    ap.add_argument("--list", action="store_true", help="list the glasses' HID interfaces and exit")
    ap.add_argument("--fake", action="store_true", help="stream synthetic head motion; needs no glasses")
    ap.add_argument("--send-hz", type=float, default=250.0, help="UDP rate cap (default 250; 0 = every IMU sample)")
    ap.add_argument("--duration", type=float, default=0.0, help="stop after this many seconds (0 = run until Ctrl+C)")
    ap.add_argument("--path", help="open this HID path (from --list) instead of picking the IMU interface")
    ap.add_argument("--dump", type=int, default=0, metavar="N", help="print the first N raw reports in hex")
    ap.add_argument("--watch", action="store_true", help="print the live Unity Euler angles 4 times a second")
    ap.add_argument("--still-seconds", type=float, default=1.5, help="still time for the start-up gyro bias")
    ap.add_argument("--kp", type=float, default=0.5, help="Mahony gravity gain, rad/s per unit error (default 0.5)")
    ap.add_argument("--no-bias-tracking", action="store_true", help="disable the run-time gyro offset tracker")
    ap.add_argument("--cmd-len", choices=[f[0] for f in CMD_FORMS], default="64",
                    help="first command form to write: 64 zero-padded bytes (ar-drivers-rs), exact (Monado), "
                         "or either behind a 0x00 byte; retries try the others (default 64)")
    ap.add_argument("--dt-source", choices=["device", "host"], default="device",
                    help="integrate with device timestamps (default) or host arrival times")
    ap.add_argument("--no-data-timeout", type=float, default=10.0, help="give up after this many seconds without IMU data")
    return ap.parse_args(argv)


def _interrupt(signum, frame):
    raise KeyboardInterrupt()


def main(argv=None):
    args = parse_args(argv)
    signal.signal(signal.SIGTERM, _interrupt)  # `kill` exits as cleanly as Ctrl+C
    if args.list:
        return list_devices(import_hid())
    if args.fake:
        return run_fake(args)
    return run_device(args)


if __name__ == "__main__":
    sys.exit(main())
