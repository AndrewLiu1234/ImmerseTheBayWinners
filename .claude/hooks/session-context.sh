#!/usr/bin/env bash
# stdout is added to Claude's context at session start / after /clear or compaction.
cd "${CLAUDE_PROJECT_DIR:-.}" 2>/dev/null || exit 0
if command -v python3 >/dev/null; then python3 - <<'PY'
from datetime import datetime, timezone, timedelta
pt = timezone(timedelta(hours=-8)); now = datetime.now(pt)   # PST in mid-November
for name, t in (("feature freeze", datetime(2026,11,15,1,0,tzinfo=pt)), ("Devpost deadline", datetime(2026,11,15,9,0,tzinfo=pt))):
    s = int((t - now).total_seconds())
    print(f"⏱ {name}: " + (f"T-{s//3600}h{(s%3600)//60:02d}m" if s > 0 else "PASSED"))
PY
fi
echo "branch: $(git branch --show-current 2>/dev/null)"
# Which person does this clone work for? A human sets it once: ./scripts/set-person.sh 1|2 (git config uga.person).
# Keep the lanes in sync with .claude/hooks/protect-files.sh and docs/WORKSPLIT.md.
ME=$(git config --get uga.person 2>/dev/null)
case "$ME" in
  1) echo "you work for: PERSON 1 = XR (Unity, the glasses, ghost hands, HUD, Blender, demo ops)"
     echo "  your lane:  unity/** except Core/** and StreamingAssets/songs/** · tools/imu-bridge/** · art/**"
     echo "  NEVER edit: Core/** · core-build/** · core-tests/** · StreamingAssets/songs/** · tools/flags/** · docs/DATA.md · docs/SONGS.md (Person 2's lane; the hook blocks it)"
     echo "  need something there? add 'Person 1 → Person 2: <request>' under Blockers in docs/STATUS.md and keep working in your lane" ;;
  2) echo "you work for: PERSON 2 = Core (audio/DSP, theory, songs, practice logic, data, eval, Devpost)"
     echo "  your lane:  unity/Assets/UntitledGuitarApp/Core/** (not Contracts) · core-build/** · core-tests/** · StreamingAssets/songs/** · tools/flags/** · docs/DATA.md · docs/SONGS.md"
     echo "  NEVER edit: anything else under unity/ · tools/imu-bridge/** · art/** (Person 1's lane; the hook blocks it). No Unity or glasses here: stop at L1/L2 and write 'pending L3-L5 (Person 1)'"
     echo "  need something there? add 'Person 2 → Person 1: <request>' under Blockers in docs/STATUS.md and keep working in your lane" ;;
  *) ME=""
     echo "⚠ PERSON NOT SET for this clone, so edits in both lanes are blocked. Tell your human to run ./scripts/set-person.sh 1 (the Mac with Unity + glasses) or ./scripts/set-person.sh 2 (the Core laptop). Do not run it yourself." ;;
esac
OPEN=$(grep -E '^## SL[0-9]+.*status: (doing|todo)' docs/TODO.md 2>/dev/null)
if [ -n "$ME" ]; then
  MINE=$(printf '%s\n' "$OPEN" | grep -E "owner:[^]]*Person $ME")
  echo "next slice (yours): $(printf '%s\n' "$MINE" | head -n 1 | grep . || echo 'none open — see docs/TODO.md')"
  echo "your open slices: $(printf '%s\n' "$MINE" | grep -oE '^## SL[0-9]+' | sed 's/^## //' | tr '\n' ' ')"
else
  echo "next slice: $(printf '%s\n' "$OPEN" | head -n 1 | grep . || echo 'see docs/TODO.md')"
fi
echo "--- STATUS (newest first) ---"; head -n 15 docs/STATUS.md 2>/dev/null
echo "--- uncommitted ---"; git status --short 2>/dev/null | head -n 12
exit 0
