#!/usr/bin/env bash
# Initialize unity/ as a Unity 2021.3.3f1 project (creates ProjectSettings/ and Packages/ next to our Assets/), then quit.
set -euo pipefail
cd "$(dirname "$0")/.."
UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/MacOS/Unity}"
[ -x "$UNITY_PATH" ] || { echo "Unity not found at $UNITY_PATH (set UNITY_PATH)"; exit 1; }
if [ -f unity/ProjectSettings/ProjectVersion.txt ]; then echo "unity/ is already a Unity project"; exit 0; fi
mkdir -p build
"$UNITY_PATH" -batchmode -quit -projectPath "$PWD/unity" -logFile build/unity-init.log \
  || { tail -n 60 build/unity-init.log; exit 1; }
echo "Done. Unity Hub → Add → $PWD/unity, then add com.unity.nuget.newtonsoft-json and MCP for Unity in Package Manager."
