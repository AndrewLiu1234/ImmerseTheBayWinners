#!/usr/bin/env bash
# Block edits to protected paths, and keep Person 1 (XR) and Person 2 (Core) out of each other's lanes.
# Exit 2 = blocked; stderr goes back to Claude as the reason.
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
ORIG="$FILE"                                       # the path as given; the lane guard below needs it unchanged
case "$FILE" in /*) ;; *) FILE="/$FILE" ;; esac   # relative paths still match the */ patterns
reason=""
case "$FILE" in
  */.env|*/.env.*|*/secrets*.json) reason="secrets" ;;
  */.git/*) reason="git internals (the person setting lives here; a human runs ./scripts/set-person.sh)" ;;
  */unity/ProjectSettings/*|*/unity/Packages/manifest.json|*/unity/Packages/packages-lock.json)
    reason="Unity settings/packages — ask a human (editor or Unity MCP)" ;;
  *.unity|*.prefab|*.asset|*.meta|*.mat|*.controller|*.anim|*.overrideController|*.mask|*.renderTexture|*.mixer|*.lighting|*.shadergraph|*.spriteatlas|*.physicMaterial|*.preset)
    reason="Unity-serialized YAML/GUIDs — use Unity MCP tools or an Editor script" ;;
  */core-tests/Fixtures/*) reason="fixtures are immutable ground truth; humans add recordings" ;;
  */docs/AUDIO_BASELINE.md) reason="baseline changes only by a human via /audio-eval" ;;
  */.claude/settings.json|*/.claude/settings.local.json|*/.claude/hooks/*|*/scripts/verify.sh|*/scripts/diff-hash.sh|*/scripts/audio-eval.sh|*/scripts/set-person.sh)
    reason="harness guardrails are human-owned" ;;
esac
if [ -n "$reason" ]; then
  echo "BLOCKED by protect-files: $FILE — $reason. If this is truly required, stop and ask the human." >&2
  exit 2
fi

# ---- Lane guard: Person 1 (XR) and Person 2 (Core) never edit each other's files -------------------------------------
# A human sets the person once per clone: ./scripts/set-person.sh 1|2 (git config uga.person; worktrees share it).
# Keep these patterns in sync with docs/WORKSPLIT.md ("Owns paths") and .claude/hooks/session-context.sh.
[ "${UGA_CROSS_LANE:-}" = "1" ] && exit 0          # emergency override; only a human can start Claude with it (WORKSPLIT.md)
case "$ORIG" in
  /*|[A-Za-z]:/*) ;;
  *) CWD=$(json_get '.cwd' 2>/dev/null); ORIG="${CWD:-$PWD}/$ORIG" ;;
esac
DIR=$(dirname "$ORIG"); TAIL=$(basename "$ORIG")
while [ ! -d "$DIR" ]; do                          # a new file in a new folder: climb to the nearest folder that exists
  UP=$(dirname "$DIR"); [ "$UP" = "$DIR" ] && break
  TAIL="$(basename "$DIR")/$TAIL"; DIR="$UP"
done
TOP=$(git -C "$DIR" rev-parse --show-toplevel 2>/dev/null) || exit 0     # not inside a git checkout: not ours
[ -f "$TOP/scripts/set-person.sh" ] || exit 0                            # some other repository: not ours
REL="$(git -C "$DIR" rev-parse --show-prefix 2>/dev/null)$TAIL"          # path relative to this checkout or worktree
case "$REL" in
  unity/Assets/UntitledGuitarApp/Core/Contracts/*) owner="" ;;           # the seam: a contract PR both people OK
  unity/Assets/UntitledGuitarApp/Core/*|core-build/*|core-tests/*|unity/Assets/StreamingAssets/songs/*|tools/flags/*|data/*|docs/DATA.md|docs/SONGS.md)
    owner=2 ;;                                                           # (fixtures and AUDIO_BASELINE.md are blocked for every agent above)
  unity/*|tools/imu-bridge/*|art/*) owner=1 ;;                           # the rest of the Unity project, the IMU bridge, Blender files
  *) owner="" ;;                                                         # shared: STATUS, DECISIONS, TODO, README, other docs, scripts
esac
[ -z "$owner" ] && exit 0
lane() { if [ "$1" = 1 ]; then echo "Person 1's lane (XR: Unity + glasses)"; else echo "Person 2's lane (Core: audio, logic, songs, data)"; fi; }
ME=$(git -C "$DIR" config --get uga.person 2>/dev/null)
case "$ME" in
  1|2) ;;
  *) echo "BLOCKED by protect-files: $REL is in $(lane "$owner"), and this clone hasn't been told which person it works for. Ask your human to run ./scripts/set-person.sh 1 (the Unity + glasses person) or ./scripts/set-person.sh 2 (the Core person). Agents must not run it themselves." >&2
     exit 2 ;;
esac
[ "$ME" = "$owner" ] && exit 0
echo "BLOCKED by protect-files: $REL is in $(lane "$owner"); this clone works for Person $ME. Read it if you need to, but don't edit it and don't route around this (no Bash writes, no changing uga.person). Put what you need in your report, or add a 'Person $ME → Person $owner: <request>' line under Blockers in docs/STATUS.md, then carry on in your own lane." >&2
exit 2
