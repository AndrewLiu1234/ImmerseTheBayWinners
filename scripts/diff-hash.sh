#!/usr/bin/env bash
# Hash of Core/test/content changes (tracked + untracked). The stop gate compares it with the last green verify.
# Keep PATHS in sync with .claude/hooks/stop-gate.sh.
cd "$(git rev-parse --show-toplevel)" || exit 0
PATHS=( unity/Assets/UntitledGuitarApp/Core/ core-build/ core-tests/ unity/Assets/StreamingAssets/ tools/flags/ )
hash_cmd() { if command -v shasum >/dev/null; then shasum; else sha1sum; fi; }
{ git diff HEAD -- "${PATHS[@]}" 2>/dev/null
  git ls-files --others --exclude-standard -- "${PATHS[@]}" | while read -r f; do echo "== $f"; cat "$f"; done
} | hash_cmd | cut -d' ' -f1
