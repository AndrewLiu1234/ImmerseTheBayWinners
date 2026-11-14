#!/usr/bin/env bash
# Stream the glasses' orientation to Unity over UDP 127.0.0.1:47800. Needs: python3 -m pip install --user hidapi
#
# Keeps the bridge running for a demo: when bridge.py exits (glasses unplugged = 6, not found = 3, open failed = 4, no
# data = 5), it is started again after a short backoff (1 s, doubling to at most 5 s; back to 1 s after a run of 30 s or
# more), so re-seating the cable needs no restart. Ctrl-C stops it. Not retried: exit 0 (normal end, also after Ctrl+C),
# exit 2 (hidapi missing or bad arguments), and one-shot runs (--list, --duration, -h/--help).
# UGA_IMU_BRIDGE_NO_RETRY=1 runs the bridge exactly once.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1

retry=1
[ "${UGA_IMU_BRIDGE_NO_RETRY:-0}" = 1 ] && retry=0
for a in "$@"; do
  case "$a" in
    --list|--duration|--duration=*|-h|--help) retry=0 ;;
  esac
done
if [ "$retry" = 0 ]; then
  exec python3 tools/imu-bridge/bridge.py --port 47800 "$@"
fi

stop=0
trap 'stop=1' INT TERM
delay=1
while :; do
  start=$(date +%s)
  python3 tools/imu-bridge/bridge.py --port 47800 "$@"
  code=$?
  if [ "$stop" = 1 ]; then echo "imu-bridge: stopped."; exit 130; fi
  case "$code" in
    0|2|130) exit "$code" ;;
  esac
  ran=$(( $(date +%s) - start ))
  [ "$ran" -ge 30 ] && delay=1
  echo "imu-bridge: bridge.py exited with code $code after ${ran} s; starting it again in ${delay} s (Ctrl-C stops)." >&2
  sleep "$delay"
  if [ "$stop" = 1 ]; then echo "imu-bridge: stopped."; exit 130; fi
  delay=$(( delay * 2 ))
  [ "$delay" -gt 5 ] && delay=5
done
