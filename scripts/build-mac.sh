#!/usr/bin/env bash
# Batch-mode macOS build — ONLY when the Unity editor is closed (otherwise use Unity MCP manage_build).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
: "${UNITY_PATH:?set UNITY_PATH, e.g. /Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity}"
mkdir -p build
"$UNITY_PATH" -batchmode -quit -projectPath unity -buildTarget OSXUniversal \
  -executeMethod UntitledGuitarApp.Editor.BuildScript.BuildMac -logFile build/unity-build.log \
  || { tail -n 80 build/unity-build.log; exit 1; }
echo "Built build/UntitledGuitarApp.app — run: open build/UntitledGuitarApp.app (it goes fullscreen on display 2)"
