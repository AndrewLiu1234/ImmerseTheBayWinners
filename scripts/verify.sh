#!/usr/bin/env bash
# Fast deterministic gate (L0–L1, optional L2). Keep it < 60 s.   Usage: ./scripts/verify.sh [--eval]
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
CORE=unity/Assets/UntitledGuitarApp/Core
echo "▶ L0 Core purity"
if [ -d "$CORE" ] && grep -rnE '^[[:space:]]*using[[:space:]]+Unity(Engine|Editor)' "$CORE" --include='*.cs'; then
  echo "✗ Core must not reference UnityEngine/UnityEditor"; exit 1; fi
echo "▶ L0 JSON validity"
json_ok() { if command -v jq >/dev/null; then jq empty "$1" >/dev/null 2>&1; else python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$1" 2>/dev/null; fi; }
for f in unity/Assets/StreamingAssets/songs/*.json unity/Assets/StreamingAssets/poses/*.json unity/Assets/StreamingAssets/flags*.json tools/flags/*.json; do
  [ -e "$f" ] || continue; json_ok "$f" || { echo "✗ invalid JSON: $f"; exit 1; }; done
echo "▶ L1 tests (builds Core as netstandard2.1 via the project reference, then unit + synthetic + geometry + songs)"
dotnet test core-tests -c Release -v q --nologo --filter "Category!=AudioEval"
LEVEL="L1"
if [[ "${1:-}" == "--eval" ]]; then ./scripts/audio-eval.sh; LEVEL="L1+L2"; fi
mkdir -p .claude && ./scripts/diff-hash.sh > .claude/.last-green-verify
echo "✅ verify passed ($LEVEL). Unity compile (L3) and glasses (L5) are separate levels."
