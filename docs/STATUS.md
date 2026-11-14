# STATUS — newest first (scribe keeps ≤40 lines; union-merged in git)
<!-- - Sat HH:MM · Person 1 or Person 2 · track · what changed · level -->
## Now
- Sat 08:30 · Person 1 · XR · walking skeleton on branch `person1/sl0-xr-foundation` (pushed, not yet in main): Unity project + `Main.unity` (one Bootstrap, built by `Editor/SceneBuilder`), flags hot reload, UGALog + session recorder, stereo SBS rig + `UGA > Glasses Game View`, IMU bridge + head pose (head-locked fallback), mic capture / WAV replay, HUD (chord label, mic meter, SIMULATED badge), spectator on the Mac screen, gaze dwell + reticle · L3: `./scripts/unity-tests.sh` 513/513 EditMode in Unity; built player smoke run headless against `bridge.py --fake`: Tracking at 250 Hz, 0 rejected · pending L4/L5 (glasses)
- Sat 08:30 · Person 1 · XR · in progress: floating neck, capsule ghost hand + ghost shader, Blender hand rig + chord poses, pose loader, learn-mode and play-along HUD
- Sat 01:00 · Person 1 · harness · people relabelled Person 1 / Person 2; lane guard added (`./scripts/set-person.sh`, protect-files, session hook). Person 2: run `./scripts/set-person.sh 2` after cloning · verified to L0 (hook tests)
- Sat 00:20 · plan · repo scaffold + docs written (planning only; nothing built) · verified to L0 (scripts syntax, JSON, hook tests)
## Blockers
- Person 1 → Person 2 · 08:30 · for the contract review: please make `FretboardGeometry`'s "neck-local mm" this frame (the ghost-hand poses, the Blender mock-up and the neck visual already use it): origin on the neck's centre line at the nut face, on the fretboard's playing surface; +x along the neck toward the bridge; +y up out of the fretboard; +z across toward string 1 (high E); strings 1 = high E … 6 = low E. If yours differs, say so and Person 1 converts in the adapter.
- (none other) — open questions: Q-TEAM-07, Q-SKILL-01
## Contract changes
- (none)
## Spike results
- M-S0 (machine half): Unity 2021.3.3f1 opens and builds `unity/` headlessly, licence OK, EditMode tests run, macOS arm64 app builds (human half pending: Hub, Newtonsoft + MCP for Unity packages) · M-S1 to M-S4: … (step-by-step procedures in `docs/DEVICE_TEST.md` § Spikes) · M-S5: … · M-S6: …
## Earlier
- Sat 00:23 · plan · Mac route confirmed (no emulator, no Android build)
- Fri 23:42 · plan · project named "Untitled Guitar App"
- Fri 23:10 · plan · Mac route chosen (glasses-only, laptop tether, no webcam)
