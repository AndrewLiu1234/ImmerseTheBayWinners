---
name: glasses-run
description: Run Untitled Guitar App on the XREAL glasses from the Mac (Play Mode on display 2 or the standalone build), start the IMU bridge and logs, and hand the human a short checklist. Use when a slice needs L5.
disable-model-invocation: true
---
# Glasses run (ask before building)
**Person 1 only**: the glasses and Unity are on Person 1's Mac. If this clone works for Person 2, stop and put your ≤8-item L5
request in docs/DEVICE_TEST.md for the next sync point.
1. Preconditions: ./scripts/verify.sh green; Unity console clean (L3); glasses connected in extended mode with SBS 3D on
   (hold brightness-up ~3 s).
2. Start ./scripts/imu-bridge.sh in the background; confirm ≥200 Hz in its output (otherwise the app runs headLocked — say so).
3. Run: Unity MCP → open GlassesGameView on display 2 → enter Play Mode. For rehearsals: ./scripts/build-mac.sh, then open build/UntitledGuitarApp.app.
4. Start ./scripts/logs.sh in the background.
5. Numbered checklist (≤8) for the feature from docs/DEVICE_TEST.md; ask the human for pass/fail per item.
6. Failures → ./scripts/pull-session.sh and analyze logs/. Report L5 only for items the human passed.
7. All passed → `git tag good-$(date +%H%M)`; copy build/UntitledGuitarApp.app to build/known-good/ if a build was used.
