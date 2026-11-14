# TODO — vertical slices (seeded from MASTER_PLAN §24; the architect refines it in Stage 2)

Owners: **Person 1** (XR: Unity, glasses, ghost hands) · **Person 2** (Core: audio, logic, songs, data). See `WORKSPLIT.md`.
Edit only the status of your own slices. Status values: `todo` · `doing` · `done`.
Mixed slices (`owner: Person 1 · Person 2`): each person builds only their half (Person 1 = Unity/UI, Person 2 = Core) and
ticks only the boxes their half proves; Person 1 sets `done` after the device check.

## Tonight (Fri) — spikes and setup
- [ ] M-S0 (Person 1, 20 min): Unity 2021.3.3f1 opens `unity/`, MCP for Unity connects (`/mcp` shows it), an empty macOS build runs
- [ ] M-S1 (Person 1, 15 min): SBS 3D from the Mac — each eye sees only its half (`HOW_IT_WORKS.md` §4)
- [ ] M-S2 (Person 1 + Claude, 45–90 min): read the IMU with `tools/imu-bridge` (protocol from `docs/apis/glasses/`, never guessed)
- [ ] M-S3 (both, 15 min): glasses mic vs MacBook mic — which hears the guitar better?
- [ ] Person 1, if there's time: record the dev clips with `/record-fixtures` (`DATA.md`)
- [ ] Person 2, Sat 07:00: verify green · `./scripts/get-datasets.sh` · Stage 2 with `architect` (prompt P1) · review at 08:30
- [ ] Person 1: Blender neck mock-up + hand rig (`GHOST_HANDS.md` §7) · measure the guitar (scale length, nut width, nut→soundhole)

## SL0 — Walking skeleton + plumbing   [track: XR+CORE] [owner: Person 1 · Person 2] [status: doing] [depends: —]
**User-visible outcome:** the glasses show a stereo floating neck with the G fingertip dots and chord name; a mic meter moves; the Mac screen shows the spectator view.
**Owned paths:** Person 1: `Runtime/{Bootstrap,Render,Neck/FloatingNeck,Hud,Audio/MicCapture,Spectator,Flags,Logging}`, `Editor/{GlassesGameView,EditorSimRig,BuildScript}`, `Scenes/Main.unity` · Person 2: `Core/Theory/**`, `core-tests/Theory/**`
**Contracts used:** `IAudioSource`, `ChordShape`, `FretboardGeometry`
**Acceptance (automated):**
- [x] ChordLibrary has G, C, D, Em, Am exactly as Appendix B2 (frets, fingers, MIDI) — Person 2: core-tests/Theory/ChordLibraryTests.cs, L1
- [ ] FretboardGeometry matches Appendix B4 within 0.1 mm; flags parsing EditMode test; Unity console clean — Person 2 half done: FretboardGeometryTests.cs (L1); flags + console = Person 1
**Acceptance (device, human-checked):**
- [ ] SBS on display 2 at ≥60 fps; both eyes fuse; mic meter moves when you strum
**Out of scope:** alignment, audio verdicts, ghost hands
**Fallback if blocked:** 2D mode (same image to both eyes)
**Max time:** 3h, by Sat 10:00 — escalate at 2h.

## SL8 — IMU + real-neck alignment (MUST, gate G1 at Sat 11:00)   [track: XR+CORE] [owner: Person 1 · Person 2 (NeckSolver)] [status: doing] [depends: SL0]
**User-visible outcome:** 4 glances put the virtual neck on your real neck, and it stays there as you turn your head.
**Owned paths:** Person 1: `tools/imu-bridge/**`, `Runtime/{Head,Neck}/**` · Person 2: `Core/Geometry/**`, `core-tests/Geometry/**`
**Acceptance (automated):**
- [x] NeckSolver recovers a synthetic neck pose from 4 rays with residual < 5 mm (no noise) and reports residual with noise — Person 2: core-tests/Geometry/NeckSolverTests.cs (0.000 mm clean; RMS 1–2 mm at 0.5°), L1
- [ ] Bridge packet-parser tests use bytes copied from `docs/apis/glasses/imu-protocol.md`
**Acceptance (device):**
- [ ] residual ≤15 mm at alignment; nut within 2 cm for 60 s with ±30° head turns; re-anchor ≤3 s
**Fallback if blocked:** `anchorMode: headLocked` (floating neck) and say why in the pitch
**Max time:** 3h (+ M-S2).

## SL4 — Ghost hands: fretting + strumming (THE core feature)   [track: XR] [owner: Person 1] [status: todo] [depends: SL0]
**User-visible outcome:** a glowing, translucent blue fretting hand forms each chord on the neck and morphs one beat early; a strumming hand plays the pattern over the soundhole (`GHOST_HANDS.md`).
**Owned paths:** `Runtime/GhostHand/**`, `Art/**`, `StreamingAssets/poses/**`, the ghost shader
**Milestones:** SL4a = fretting hand for G and Em by 13:00 (in `good-1300`) · SL4b = C, D, Am + strumming hand by 16:00
**Acceptance (automated):**
- [ ] every pose JSON loads; each fingertip lands within 3 mm of its dot for every MUST chord (EditMode test)
**Acceptance (device):**
- [ ] the wearer copies G and Em without a diagram; the hands read as glowing blue and see-through in the demo room
- [ ] the strumming hand stays in time with the beat cursor at 50/75/100%
**Out of scope:** hand tracking (impossible on this setup)
**Fallback if blocked:** procedural capsule hand posed by IK from the dots. Never dots only.
**Max time:** 3h in Unity + Blender time.

## SL1 — Tuner   [track: CORE+XR] [owner: Person 2 (YIN) · Person 1 (needle UI)] [status: doing] [depends: SL0]
**User-visible outcome:** tune each string with a needle.
**Acceptance (automated):** - [ ] YIN within ±3 cents on synthetic E2–E4 ✓ (Person 2: max 0.069 c, core-tests/Audio/YinPitchTrackerTests.cs, L1) and within ±10 cents on recorded open strings (open: no recordings yet; TunerRecordedEvalTests ready)
**Acceptance (device):** - [ ] matches a clip-on or phone tuner within 10 cents
**Max time:** 2.5h.

## SL2 — Pluck check + finger by finger + ring-out quality   [track: CORE+XR] [owner: Person 2 · Person 1] [status: doing] [depends: SL1]
**User-visible outcome:** each string gets ✓/✕ and a tip ("Arch finger 3, B string muted"); a short sustain gets "let string N ring".
**Acceptance (automated):** - [ ] synthetic verdict classes ≥ 95% correct ✓ (Person 2: 100% clean/20/10 dB, core-tests/Audio/PluckVerifierTests.cs, L1) · every deliberate fault in the dev clips is caught (open: no clips yet)
**Acceptance (device):** - [ ] a deliberately muted B string on G is flagged in 4 of 5 tries
**Max time:** 3h.

## SL3 — Strum check (chord level)   [track: CORE+XR] [owner: Person 2 · Person 1] [status: todo] [depends: SL2]
**User-visible outcome:** a strum gets ✓ or its top chord-level issue within 350 ms.
**Acceptance (automated):** - [ ] synthetic chord accuracy ≥ 95%, false-clean ≤ 3% · dataset accuracy reported (GuitarSet, GUITAR CHORDS V3), not gated
**Acceptance (device):** - [ ] 5 chords × 4 strums: at least 18 of 20 judged right
**Max time:** 3h.

## SL5 — Songs + play-along (+ setup and library)   [track: CORE+XR] [owner: Person 2 (Core) · Person 1 (HUD)] [status: doing] [depends: SL3]
**User-visible outcome:** pick a song, pick a tempo, play along with wait mode; lyrics shown above-chord style when the song has them.
**Owned paths:** Person 2: `Core/Songs/**`, `Core/Practice/PlayAlongSession*`, `StreamingAssets/songs/**` · Person 1: neck HUD, lyric strip, library, approach rings
**Acceptance (automated):**
- [x] AllSongsValid passes for the 3 songs (incl. `chords-only` rules and the 3/4 pickup in Happy Birthday) — Person 2: core-tests/Songs/SongTests.cs, L1
- [x] timeline tests at every tempo preset; wait mode incl. the escape hatch; best-onset scoring — Person 2: core-tests/Songs/SongTimelineTests.cs + core-tests/Practice/PlayAlongSessionTests.cs, L1
**Acceptance (device):** - [ ] Happy Birthday at 75% with wait mode completes with both ghost hands animating
**Max time:** 3h. (Core part can start right after SL0.)

## SL6 — Patterns and timing   [track: CORE+XR] [owner: Person 2 · Person 1] [status: doing] [depends: SL5]
**User-visible outcome:** strum arrows and the picking strip with a cursor, in sync with the strumming hand; timing feedback.
**Acceptance (automated):** - [x] onset F1 ≥ 0.9 on synthetic audio ✓ (Person 2: F1 ≥ 0.979 at 50/75/100%, core-tests/Audio/SpectralFluxOnsetTests.cs, L1) · timing math tests ✓ Person 2: core-tests/Practice/PatternJudgeTests.cs, L1
**Acceptance (device):** - [ ] the pattern strip and the strumming hand agree; feedback makes sense
**Max time:** 2.5h.

## SL9 — Demo ops   [track: XR] [owner: Person 1] [status: todo] [depends: SL5]
**User-visible outcome:** judges watch the Mac-screen spectator; flags hot-reload in 1 s; SIMULATED INPUT badge; the standalone build runs fullscreen on display 2.
**Max time:** 2h.

## SL11 — Data + eval harness   [track: CORE] [owner: Person 2] [status: todo] [depends: —]
**User-visible outcome:** an honest accuracy table for the pitch.
**Acceptance (automated):**
- [ ] datasets in `data/` (gitignored) · dev clips labelled in `labels.csv` · AudioEval tests print a confusion matrix (report-only until a baseline exists)
- [ ] Sat afternoon: eval set recorded through the app and frozen; a human writes the baseline JSON in `AUDIO_BASELINE.md`
**Max time:** spread across Fri night + Sat.

## SL7 — Change drill (SHOULD)   [track: CORE+XR] [owner: Person 2] [status: doing] [depends: SL3]
**User-visible outcome:** a G↔C and Am↔G clean-change counter. **Max time:** 1.5h.
- [x] Core: ChangeDrillSession (clean = target heard ≤3 s after previous; best rate per 60 s; late-change tip after 2 slow) — core-tests/Practice/ChangeDrillSessionTests.cs, L1 · counter UI = Person 1 · needs SL3 strum check for real input

## SL10 — AI coach (COULD)   [track: CORE] [owner: Person 2] [status: doing] [depends: SL5]
**User-visible outcome:** a coach card after a run (≤3 s, or a rule-based fallback). **Max time:** 2h.
- [x] Core: CoachInput, RuleBasedCoach, CoachPrompt, CoachResponseParser — core-tests/Coach/CoachTests.cs, L1
- [ ] Runtime (Person 1): HTTP call only when flag `coach` ≠ "off", model `CoachPrompt.ModelId`, 3 s timeout, key from env var, fallback to RuleBasedCoach; card on the HUD
