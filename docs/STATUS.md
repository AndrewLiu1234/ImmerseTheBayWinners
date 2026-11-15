# STATUS — newest first (scribe keeps ≤40 lines; union-merged in git)
<!-- - Sat HH:MM · Person 1 or Person 2 · track · what changed · level -->
## Now
- Sat 18:29 · Person 2 · SL3 · real-guitar strum check (744a3d1, merged): 98 Hz body air resonance out of the stray-energy score, stricter evidence for rival chords/faults, onset refractory 130 ms, window 80–300 ms → strum_chord_acc_quiet 68.4→90.1 %, false_clean 2.6→0.0 %, onsets 76→71 (DEV set, tuned on — venue set is the real test; body band F2–G#2 is this guitar's) · verified to L2 · pending venue eval, L3–L5 (Person 1)
- Sat 18:14 · Person 2 · SL2/SL3 L2 · 112 quiet dev clips in (ce29f1f). Real-guitar first pass: strum_chord_acc_quiet 68.4% (clean D 0/6, Em 0/8; 76 onsets from 55 strums), strum_false_clean 2.6%; pluck_correct_quiet 67.3% (WrongString ×10, onset missed on ~20 clips) — new report-only PluckRecordedEvalTests. dsp-engineer re-tuning the strum path now · verified to L2 · pending venue eval set, L3–L5 (Person 1)
- Sat 18:14 · Person 2 · decisions · WrongString tip approved; Riptide credit names James Keogh; Happy Birthday PD cites Marya v. Warner/Chappell (2015) · L1
- Sat 15:38 · Person 2 · SL1 L2 · 18 recorded open-string dev clips (quiet, MacBook Voice Memos) committed; tuner 18/18 within ±10 c, max 6.3 c (first pass 12/18: both E strings ~12 c flat → guitar retuned + re-recorded). E4 take 1 had 20% octave frames (median fine) — watch needle flicker on device · verified to L2 · pending venue eval set, baseline, L3–L5 (Person 1)
- Sat 15:10 · Person 2 · handoff · All Person 2 Core code is on main (82f42bc), 408 tests, verify --eval green: SL0 Theory, SL8 NeckSolver, SL1 Tuner, SL2 PluckVerifier + Tips + LearnChordSession, SL3 StrumVerifier + StrumFeed + StrumRouting, SL5 Songs + PlayAlongSession, SL6 PatternClock + PatternJudge + SpectralFluxOnset, SL7 ChangeDrillSession, SL10 Coach (rule-based + LLM prompt/parser). All numbers synthetic · verified to L1 (+L2 harness with no clips) · pending L2 recordings, L3–L5 (Person 1)
- Sat 15:10 · Person 2 · key numbers (synthetic) · tuner max 0.069 c; pluck verdicts 100% clean/20/10 dB (96.5% at 5 dB); strum 99.6% acc, 0.3% false-clean; onset F1 ≥ 0.979; NeckSolver 0 mm clean but nut error ~5× the RMS residual under gaze noise · L1
- Sat 15:05 · Person 2 · contracts · Contracts v1 merged (PR #1, PR #2 doc fix); Core duplicates renamed: PatternClockSlot, PlayAlongTarget, TunerState · L1
- Sat 01:00 · Person 1 · harness · people relabelled Person 1 / Person 2; lane guard added (`./scripts/set-person.sh`, protect-files, session hook). Person 2: run `./scripts/set-person.sh 2` after cloning · verified to L0 (hook tests)
- Sat 00:20 · plan · repo scaffold + docs written (planning only; nothing built) · verified to L0 (scripts syntax, JSON, hook tests)
## Blockers
- Person 2 → Person 1: L3 compile of Core (Theory, Geometry, Audio, Songs, Practice, Coach, Contracts) in Unity + commit the generated .meta files. Send measured scale length / nut string span to replace FretboardConfig.Default (Q-REG-06).
- Person 2 → Person 1: wiring — Tuner.Process(…, targetString) → TunerState (capture 44.1/48 kHz; pass target string); StrumFeed from the audio worker → StrumRouting → PlayAlong/ChangeDrill/LearnChord, show Tips.ForStrum; HUD reads PlayAlongTarget; audio.strumMinMargin → AudioConfig.StrumMinMargin; onsets read +11…14 ms late → audio.latencyOffsetMs; coach HTTP only when flag coach ≠ "off" (model CoachPrompt.ModelId, 3 s timeout, key from env var, fallback RuleBasedCoach).
- Person 2 → Person 1 (G1 caution): NeckSolver residual understates true nut error ~5× (depth) — check the nut against a physical marker; aim for dwell jitter ≤0.5°.
- Person 2 → recorder: /record-fixtures — open strings DONE (quiet); still needed: pluck faults from MASTER_PLAN Appendix B2; clean + faulty strums of G C D Em Am; quiet + venue. Then ./scripts/audio-eval.sh; a human freezes the eval set and writes docs/AUDIO_BASELINE.md (SL1/SL2/SL3/SL11).
- Open Q (Person 2): tracks Q-AI-01/02 (DEMO.md `[TRACKS]`). Q-TEAM-07, Q-SKILL-01, Q-TEAM-02 resolved Sat 18:20 (DECISIONS).
- Person 2 → Person 1 (URGENT): unity/…/Runtime has 0 .cs files on main at Sat 18:20 — push local Unity work, or agree a minimal demo tonight. Also needed for the DEMO.md fallbacks: StreamingAssets/fixtures-demo/learn-g.wav (copy a G clip) + the SIMULATED INPUT badge.
## Contract changes
- Sat 15:05 · Person 2 · Contracts v1 (PR #1, Person 1 approved) + v1.1 doc fix (PR #2: StrumResult.Confidence = 0–1 hypothesis score). See docs/ARCHITECTURE.md Deltas.
## Spike results
- M-S0: … · M-S1: … · M-S2: … · M-S3: … · M-S4: … · M-S5: … · M-S6: …
## Earlier
- Sat 00:23 · plan · Mac route confirmed (no emulator, no Android build)
- Fri 23:42 · plan · project named "Untitled Guitar App"
- Fri 23:10 · plan · Mac route chosen (glasses-only, laptop tether, no webcam)
