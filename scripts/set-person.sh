#!/usr/bin/env bash
# Tell THIS clone which person it works for, so the hooks can keep the two lanes apart (docs/WORKSPLIT.md).
#   ./scripts/set-person.sh 1    Person 1 = XR   (the Mac with Unity and the glasses)
#   ./scripts/set-person.sh 2    Person 2 = Core (audio, logic, songs, data; no Unity needed)
#   ./scripts/set-person.sh      show the current setting
# A HUMAN runs this once per clone. It is stored in .git/config as uga.person: never committed, shared by worktrees.
# Agents must not run it or change uga.person (denied in .claude/settings.json).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
case "${1:-}" in
  1|2) git config --local uga.person "$1" ;;
  "")  ;;
  *)   echo "usage: ./scripts/set-person.sh [1|2]" >&2; exit 2 ;;
esac
P=$(git config --get uga.person || true)
case "$P" in
  1) echo "This clone works for PERSON 1 = XR (Unity, the glasses, ghost hands, HUD, Blender, demo ops)."
     echo "  agents may edit:  unity/** (except Core/** and StreamingAssets/songs/**) · tools/imu-bridge/** · art/**"
     echo "  agents may NOT:   Core/** · core-build/** · core-tests/** · StreamingAssets/songs/** · tools/flags/** · docs/DATA.md · docs/SONGS.md" ;;
  2) echo "This clone works for PERSON 2 = Core (audio/DSP, theory, songs, practice logic, data, eval, Devpost)."
     echo "  agents may edit:  unity/Assets/UntitledGuitarApp/Core/** (not Contracts) · core-build/** · core-tests/** · StreamingAssets/songs/** · tools/flags/** · docs/DATA.md · docs/SONGS.md"
     echo "  agents may NOT:   anything else under unity/ · tools/imu-bridge/** · art/**   (and no Unity, no glasses, no L3-L5 claims)" ;;
  *) echo "No person set for this clone yet." >&2
     echo "  ./scripts/set-person.sh 1    if this is the Mac with Unity and the glasses (Person 1 = XR)" >&2
     echo "  ./scripts/set-person.sh 2    if this is the Core laptop (Person 2 = Core)" >&2
     exit 1 ;;
esac
echo "Shared by both: docs/STATUS.md, docs/DECISIONS.md, docs/TODO.md (your own lines), Core/Contracts (both must OK). Full table: docs/WORKSPLIT.md"
