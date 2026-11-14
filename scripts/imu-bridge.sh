#!/usr/bin/env bash
# Stream the glasses' orientation to Unity over UDP 127.0.0.1:47800. Needs: python3 -m pip install --user hidapi
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
exec python3 tools/imu-bridge/bridge.py --port 47800 "$@"
