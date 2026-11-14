#!/usr/bin/env bash
# If Core/test/content files changed and ./scripts/verify.sh hasn't passed on THIS exact diff, keep Claude working (once per
# stop). Fails OPEN when it can't parse its input, so Claude is never trapped. Keep PATHS in sync with scripts/diff-hash.sh.
INPUT=$(cat)
json_get() {
  if command -v jq >/dev/null 2>&1; then printf '%s' "$INPUT" | jq -r "$1 // empty"
  elif command -v python3 >/dev/null 2>&1; then printf '%s' "$INPUT" | python3 -c '
import json, sys
d = json.load(sys.stdin)
for k in sys.argv[1].lstrip(".").split("."):
    d = d.get(k) if isinstance(d, dict) else None
print("" if d is None else (str(d).lower() if isinstance(d, bool) else d))' "$1"
  else return 1; fi
}
ACTIVE=$(json_get '.stop_hook_active') || exit 0
[ "$ACTIVE" = "true" ] && exit 0
DIR=$(json_get '.cwd'); [ -n "$DIR" ] && cd "$DIR" 2>/dev/null
ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$ROOT" || exit 0
PATHS=( unity/Assets/UntitledGuitarApp/Core/ core-build/ core-tests/ unity/Assets/StreamingAssets/ tools/flags/ )
[ -z "$(git status --porcelain -- "${PATHS[@]}" 2>/dev/null)" ] && exit 0
[ -x scripts/diff-hash.sh ] || exit 0
NOW=$(scripts/diff-hash.sh)
[ -f .claude/.last-green-verify ] && [ "$(cat .claude/.last-green-verify)" = "$NOW" ] && exit 0
echo "Stop gate: Core/test/content files changed but ./scripts/verify.sh hasn't passed on this exact diff. Run it and fix failures — or, if it truly can't run here, say so and state the verification level you reached." >&2
exit 2
