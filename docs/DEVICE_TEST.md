0. Guitar tuned · MacBook plugged in · glasses extended + SBS on · IMU bridge ≥200 Hz · flags profile = dev
1. Stereo: both eyes fuse the neck; eyes not swapped; text sharp (IPD slider OK)
2. Align: 4 dwells → virtual nut on the real nut (≤2 cm); turn head ±30° for 60 s → stays within 2 cm; re-anchor ≤3 s
3. Tuner: each open string within ±10 cents of the reference tuner
4. Learn G: finger-by-finger completes; deliberately muted B is flagged with the right tip
4b. Ghost hands: the fretting hand forms G and Em on the real neck (copyable without a diagram); the strumming hand keeps time
5. Strum check: Em/G/C/D × 3 strums → ≥10/12 correct verdicts
6. Play-along 75% + wait mode: approach rings land on the beat; current/next/beat readable; ghost morphs one beat early
7. Spectator: Mac screen readable from 1.5 m
8. Fallbacks: stop the IMU bridge → headLocked within 1 s; use-flags safe → WAV replay with SIMULATED INPUT badge
9. Perf: ≥60 fps for 2 minutes; no stutter when the HUD changes

## Spikes M-S1 to M-S4 (Person 1, Sat morning; tools on branch `person1/sl0-xr-foundation`)
Write each result as one line under "Spike results" in `docs/STATUS.md`.

**M-S1 stereo:** follow `art/test/README.md` (full-screen `art/test/sbs-test-3840x1080.png` on the glasses in 3840×1080 3D
mode). Pass: left eye alone sees only the red "L", right eye only the blue "R"; the frame is visible on all four sides.
Halves swapped → set `"render": {"swapEyes": true}` in the flags. Note which button and how many seconds switched on 3D.

**M-S2 IMU** (quit any XREAL app first; hidapi opens the device exclusively):
1. `./scripts/imu-bridge.sh --list` → a `pid=0x0426` line marked `iface=2 … <- IMU` (if every iface shows -1, add `--path '<path>'` below).
2. Glasses flat and still: `./scripts/imu-bridge.sh --dump 3` → "IMU packets are arriving", reports start `01 02`, then "Gyro bias …"
   without FORCED. Paste the three report lines into `docs/apis/glasses/imu-protocol.md` §11 (they replace the synthetic vector).
3. Status lines every 5 s: `imu` ≥ 200 Hz (about 1000 expected), `udp` ≈ 250 Hz, no "device clock … WARNING".
4. Still for 60 s: `yaw drift` < 2°/min with `still 100%`.
5. Wear them: `./scripts/imu-bridge.sh --watch` → nod down: pitch > 0 · turn left: yaw < 0 · left ear down: roll > 0. A wrong sign is
   a one-line fix in the bridge (`device_to_rub`), never in Unity.
6. Ctrl+C prints "Stopped: N IMU packets…". No data at all? It tries 4 command forms, then exits after 10 s with a hint: keep the output.
7. With Unity: bridge running, Play `Scenes/Main.unity` → the Mac-screen status line reads "IMU 250 Hz"; stop the bridge → "IMU off:
   head-locked" within about 0.3 s. Press R to recenter.

**M-S3 microphones** (the glasses mic vs the MacBook mic):
1. Glasses plugged in: System Settings › Sound › Input → write down the glasses' input name.
2. Play `Scenes/Main.unity`. Unity 2021.3.3f1's editor has no microphone usage string, so macOS may not prompt: allow Unity under
   Privacy & Security › Microphone if the mic meter stays empty. Run `./scripts/logs.sh` in a terminal.
3. MacBook mic first (flag `audio.device` = "MacBook"), 40-60 cm from the soundhole: 10 s silence, strum Em and let it ring, 10 s
   silence. Pass: the mic meter moves; `mic.level` strum rmsDb minus silence rmsDb ≥ 15 dB; the ring-out decays smoothly (no cliff).
4. Glasses mic: put `{ "audio": { "device": "<part of the glasses' input name>" } }` in
   `~/Library/Application Support/UntitledGuitarApp/Untitled Guitar App/flags.json`, re-enter Play, check the status line names it,
   repeat step 3.
5. Control Center › Mic Mode, if it appears: Standard or Wide Spectrum, never Voice Isolation (it gates and pumps the guitar).
6. Winner → `audio.device` in `unity/Assets/StreamingAssets/flags.default.json` + a DECISIONS line (Q-AUD-01). Delete the override.

**M-S4 Unity on the glasses:**
1. Glasses in 3D 3840×1080 (M-S1). Open `Scenes/Main.unity`, menu UGA › Glasses Game View (a borderless view on the glasses; if it
   prints steps instead, follow them), Play.
2. Select StereoRig in the Hierarchy, tick Debug Eye Colors: left eye red, right eye blue; untick.
3. The status line on the Mac screen shows "stereo on display 2  NN fps": pass at ≥ 60 fps for a minute.
4. Standalone: close the editor, `./scripts/build-mac.sh`, `open build/UntitledGuitarApp.app`; `./scripts/logs.sh "$HOME/Library/Logs/UntitledGuitarApp/Untitled Guitar App/Player.log"`
   shows `stereo.display_activated`, then `stereo.display_ok` (a `stereo.display_size` warning means the glasses are not in 3D mode).
5. If depth feels wrong on the neck later, try `"render": {"convergenceM": 1}` up to 4 (default 0 = parallel).
