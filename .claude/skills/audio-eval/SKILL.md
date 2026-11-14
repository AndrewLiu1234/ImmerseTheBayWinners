---
name: audio-eval
description: Run the recorded-fixture audio evaluation, compare with docs/AUDIO_BASELINE.md and explain regressions. Use after any Core/Audio change or when tuning thresholds.
---
# Audio eval
Lane: Person 2 acts on these numbers (DSP, thresholds, flag profiles). On Person 1's clone you may run it and report, but
change nothing in Core, core-tests or tools/flags.
1. Run `./scripts/audio-eval.sh` as a normal tool call (it can take a minute). Baseline format: a ```json block in
   docs/AUDIO_BASELINE.md; with no baseline yet the eval only reports. Then summarize: pluck verdict accuracy, strum chord accuracy, false-clean rate, onset F1 — quiet vs venue.
2. Compare with docs/AUDIO_BASELINE.md. Any metric down >3 points = regression → name the labels that got worse and the likely cause.
3. Never edit fixtures or the baseline. If results improved, give the human the exact baseline lines to paste.
