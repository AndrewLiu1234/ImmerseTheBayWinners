---
name: seed-demo
description: Put the demo setup into a known-good state — flags profile, songs, alignment reset, cleared sessions, human checklist. Use before every rehearsal and judging slot.
argument-hint: "[demo | judge | safe]"
disable-model-invocation: true
---
# Seed demo — profile: $ARGUMENTS (empty → demo)
1. ./scripts/use-flags.sh <profile> (hot-reloads). Known-good build present in build/known-good/.
2. Songs present: warm-up (original) + the public-domain song.
3. Ask before clearing old sessions.
4. Human checklist: guitar tuned · MacBook plugged in, Do Not Disturb on, display sleep off · glasses extended + SBS on ·
   IMU bridge ≥200 Hz · noise floor re-measured in Setup · aligned (demo) or headLocked (judge) · spectator readable ·
   wipes + spare nose pads · SIMULATED INPUT badge OFF unless deliberately on.
