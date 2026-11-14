#!/usr/bin/env python3
"""IMU bridge: XREAL Air 2 Ultra (USB HID) -> orientation -> UDP JSON for Unity.
Packet layouts and the IMU-enable command MUST come from docs/apis/glasses/imu-protocol.md (community writeup /
Monado xreal_air driver). Do not guess bytes."""
import argparse
import sys

import hid  # python3 -m pip install --user hidapi

XREAL_VID = 0x3318        # Monado xreal_air_interface.h
AIR2_ULTRA_PID = 0x0426   # Monado xreal_air_interface.h


def list_devices():
    for d in hid.enumerate(XREAL_VID, 0):
        print(f"pid=0x{d['product_id']:04x} iface={d.get('interface_number')} "
              f"usage_page=0x{d.get('usage_page', 0):x} path={d['path']!r}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=47800)
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()
    if args.list:
        list_devices()
        return
    # TODO(M-S2/SL8): open the IMU interface, send the documented enable command, parse gyro/accel packets,
    # fuse to a quaternion (e.g. Madgwick), and send {"t": <seconds>, "q": [x, y, z, w]} to 127.0.0.1:<port> at >=200 Hz.
    print("IMU protocol not implemented yet - see docs/apis/glasses/imu-protocol.md", file=sys.stderr)
    sys.exit(2)


if __name__ == "__main__":
    main()
