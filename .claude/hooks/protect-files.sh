#!/usr/bin/env bash
# Block edits to protected paths. Exit 2 = blocked; stderr goes back to Claude as the reason.
INPUT=$(cat)
json_get() {  # $1 = jq-style path like .tool_input.file_path
  if command -v jq >/dev/null 2>&1; then printf '%s' "$INPUT" | jq -r "$1 // empty"
  elif command -v python3 >/dev/null 2>&1; then printf '%s' "$INPUT" | python3 -c '
import json, sys
d = json.load(sys.stdin)
for k in sys.argv[1].lstrip(".").split("."):
    d = d.get(k) if isinstance(d, dict) else None
print("" if d is None else (str(d).lower() if isinstance(d, bool) else d))' "$1"
  else return 1; fi
}
FILE=$(json_get '.tool_input.file_path') || { echo "protect-files: couldn't parse hook input (needs jq or python3), so blocking to be safe" >&2; exit 2; }
[ -z "$FILE" ] && exit 0
FILE="${FILE//\\//}"
case "$FILE" in /*) ;; *) FILE="/$FILE" ;; esac   # relative paths still match the */ patterns
reason=""
case "$FILE" in
  */.env|*/.env.*|*/secrets*.json) reason="secrets" ;;
  */unity/ProjectSettings/*|*/unity/Packages/manifest.json|*/unity/Packages/packages-lock.json)
    reason="Unity settings/packages — ask a human (editor or Unity MCP)" ;;
  *.unity|*.prefab|*.asset|*.meta|*.mat|*.controller|*.anim|*.overrideController|*.mask|*.renderTexture|*.mixer|*.lighting|*.shadergraph|*.spriteatlas|*.physicMaterial|*.preset)
    reason="Unity-serialized YAML/GUIDs — use Unity MCP tools or an Editor script" ;;
  */core-tests/Fixtures/*) reason="fixtures are immutable ground truth; humans add recordings" ;;
  */docs/AUDIO_BASELINE.md) reason="baseline changes only by a human via /audio-eval" ;;
  */.claude/settings.json|*/.claude/hooks/*|*/scripts/verify.sh|*/scripts/diff-hash.sh|*/scripts/audio-eval.sh)
    reason="harness guardrails are human-owned" ;;
esac
[ -z "$reason" ] && exit 0
echo "BLOCKED by protect-files: $FILE — $reason. If this is truly required, stop and ask the human." >&2
exit 2
