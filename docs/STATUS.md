# STATUS — newest first (scribe keeps ≤40 lines; union-merged in git)
<!-- - Sat HH:MM · Person 1 or Person 2 · track · what changed · level -->
## Now
- Sat 11:00 · Person 2 · SL0-core · `Core/Theory`: Midi, Tuning, ChordShape, ChordLibrary (G C D Em Am MUST + E A Dm from Appendix B2), FretboardGeometry (B4, L=648 mm, nut span 35 → saddle 53.5 mm; frame AlongMm from nut, AcrossMm + toward string 6) + `FingertipDots(shape)` for the ghost hand; 80 tests (commit 6a798f1) · verified to L1 · pending L3 Unity compile + L5 (Person 1)
- Sat 01:00 · Person 1 · harness · people relabelled Person 1 / Person 2; lane guard added (`./scripts/set-person.sh`, protect-files, session hook). Person 2: run `./scripts/set-person.sh 2` after cloning · verified to L0 (hook tests)
- Sat 00:20 · plan · repo scaffold + docs written (planning only; nothing built) · verified to L0 (scripts syntax, JSON, hook tests)
## Blockers
- Person 2 → Person 1: SL0-core is on main — please `refresh_unity` + `read_console` to confirm Core/Theory compiles in Unity (L3); use `new FretboardGeometry(FretboardConfig.Default).FingertipDots(ChordLibrary.G)` for the G dots. Send the measured scale length / nut string span so we replace `FretboardConfig.Default` (Q-REG-06).
- Open: Core/Contracts/*.cs stubs (Stage 2, architect) don't exist yet; `ChordShape`/`FretboardGeometry` live in Core/Theory per ARCHITECTURE.md — rec: architect writes the Contracts stubs referencing them before SL1. Also Q-TEAM-07, Q-SKILL-01.
## Contract changes
- (none)
## Spike results
- M-S0: … · M-S1: … · M-S2: … · M-S3: … · M-S4: … · M-S5: … · M-S6: …
## Earlier
- Sat 00:23 · plan · Mac route confirmed (no emulator, no Android build)
- Fri 23:42 · plan · project named "Untitled Guitar App"
- Fri 23:10 · plan · Mac route chosen (glasses-only, laptop tether, no webcam)
