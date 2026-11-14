# STATUS — newest first (scribe keeps ≤40 lines; union-merged in git)
<!-- - Sat HH:MM · Person 1 or Person 2 · track · what changed · level -->
## Now
- Sat 12:45 · Person 2 · SL6/SL7/SL10-core · `Core/Practice`: PatternClock (slot per time; B → chord's bass string; 3/4 pickup has no slots), PatternJudge (±150 ms match window, ±60 ms on time, rushing/dragging/steady, latencyOffsetMs; timing only, no D/U), ChangeDrillSession. `Core/Coach`: CoachInput, RuleBasedCoach, CoachPrompt (model claude-haiku-5-5, 3 s), CoachResponseParser. 185 tests (517ba79) · verified to L1 · pending L3/L5 (Person 1). Open in SL6: onset detector (after SL1)
- Sat 12:45 · Person 2 · contracts · Contracts v1 stubs drafted + compiling on LOCAL branch `contracts/v1` (cde6115, not pushed yet) — needs Person 1 + Person 2 OK. Known clash to fix before merge: `PatternSlot`/`ChordTarget` exist in both Core/Practice and Contracts
- Sat 12:15 · Person 2 · review · music-teacher: all 8 ChordLibrary shapes PASS (Appendix B2 correct). B6 "Muted" tip rule gives the wrong finger when the muted string is itself fretted (G/Am with B muted) → SL2 will say "press finger N's tip" there and only "arch finger N" for open strings; WrongFret tips will say "one fret higher/lower". Songs: Riptide credit lacks the writer (to verify), Happy Birthday PD source is a news article (add the ruling) · L0 (review only)
- Sat 12:10 · Person 2 · SL5-core · `Core/Songs`: SongParser (never throws), SongValidator (SONGS.md license rules, ChordLibrary chords, slots, whole bars), SongTimeline (every tempo preset, 3/4 pickup); `Core/Practice/PlayAlongSession` (wait mode, holds at chord 0 too, escape hatch = 8 s or 6 wrong strums or Skip; best-onset; 0.3 s early grace). HUD reads `ChordTarget`; input `OnStrum(chord, nowSec)`. 140 tests (c75e5a0) · verified to L1 · pending L3/L5 (Person 1: Happy Birthday at 75%)
- Sat 11:30 · Person 2 · SL8-core · `Core/Geometry/NeckSolver`: `Solve(GazeRay[4])` (order = `AlignTarget`: nut low E, nut high E, fret 5 low E, fret 12 low E; world mm) → `NeckPose` + RMS/max/per-target residual; `Reanchor(pose, nutRay)`; 19 tests. No noise → 0.000 mm. 0.5° gaze noise → RMS 1–2 mm but nut error 6–15 mm (commit 5a0a0f2) · verified to L1 · pending L3/L5 (Person 1)
- Sat 11:00 · Person 2 · SL0-core · `Core/Theory`: Midi, Tuning, ChordShape, ChordLibrary (G C D Em Am MUST + E A Dm from Appendix B2), FretboardGeometry (B4, L=648 mm, nut span 35 → saddle 53.5 mm; frame AlongMm from nut, AcrossMm + toward string 6) + `FingertipDots(shape)` for the ghost hand; 80 tests (commit 6a798f1) · verified to L1 · pending L3 Unity compile + L5 (Person 1)
- Sat 01:00 · Person 1 · harness · people relabelled Person 1 / Person 2; lane guard added (`./scripts/set-person.sh`, protect-files, session hook). Person 2: run `./scripts/set-person.sh 2` after cloning · verified to L0 (hook tests)
- Sat 00:20 · plan · repo scaffold + docs written (planning only; nothing built) · verified to L0 (scripts syntax, JSON, hook tests)
## Blockers
- Person 2 → Person 1: SL0-core is on main — please `refresh_unity` + `read_console` to confirm Core/Theory compiles in Unity (L3); use `new FretboardGeometry(FretboardConfig.Default).FingertipDots(ChordLibrary.G)` for the G dots. Send the measured scale length / nut string span so we replace `FretboardConfig.Default` (Q-REG-06).
- Person 2 → Person 1: NeckSolver is on main. NeckAligner: send the 4 dwell rays in metres×1000 (mm), from the head pivot, in the order of `AlignTarget`; map `NeckPose` back ×0.001. **G1 caution:** the residual understates the true nut error by ~5× (depth along the gaze), so residual ≤15 mm does NOT prove nut within 2 cm — measure the nut against a physical marker for the G1 check. Rec: aim for dwell jitter ≤0.5°.
- Open: Core/Contracts/*.cs stubs (Stage 2, architect) don't exist yet; `ChordShape`/`FretboardGeometry` live in Core/Theory per ARCHITECTURE.md — rec: architect writes the Contracts stubs referencing them before SL1. Also Q-TEAM-07, Q-SKILL-01.
## Contract changes
- (none)
## Spike results
- M-S0: … · M-S1: … · M-S2: … · M-S3: … · M-S4: … · M-S5: … · M-S6: …
## Earlier
- Sat 00:23 · plan · Mac route confirmed (no emulator, no Android build)
- Fri 23:42 · plan · project named "Untitled Guitar App"
- Fri 23:10 · plan · Mac route chosen (glasses-only, laptop tether, no webcam)
