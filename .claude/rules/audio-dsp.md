---
paths:
  - "unity/Assets/UntitledGuitarApp/Core/Audio/**"
  - "core-tests/Audio/**"
---
# Audio/DSP
- Verification, not transcription: test hypotheses about the EXPECTED string/chord.
- Pitch = YIN over the full 70–1000 Hz range with octave correction (harmonic support for the expected AND neighbor-string hypotheses). A narrow search window turns wrong-string plucks into false "muted". Report Hz, cents, confidence, RMS.
- Strum verdicts are chord-level (+ distinctive bass-string faults); per-string diagnosis comes from the pluck check.
- New verdict class → synthetic test first (core-tests/Synth). Threshold change → ./scripts/audio-eval.sh before/after in the commit.
- Standard tuning MIDI 40 45 50 55 59 64; f = 440·2^((m−69)/12). Chord table: docs/MASTER_PLAN.md Appendix B.
- macOS capture can apply voice processing/AGC (Mic Mode "Voice Isolation", some USB headset paths); if eval on device recordings looks "pumped", raise it — don't tune around it.
- Lane: Person 2 (Core). Person 1's agents may read these files but never edit them (hook-enforced; docs/WORKSPLIT.md).
