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
echo "next slice: $(grep -m1 -E '^## SL[0-9]+.*status: (doing|todo)' docs/TODO.md 2>/dev/null || echo 'see docs/TODO.md')"
echo "--- STATUS (newest first) ---"; head -n 15 docs/STATUS.md 2>/dev/null
echo "--- uncommitted ---"; git status --short 2>/dev/null | head -n 12
exit 0
