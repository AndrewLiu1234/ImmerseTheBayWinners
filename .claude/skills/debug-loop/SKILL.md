---
name: debug-loop
description: Systematic debugging for Untitled Guitar App — reproduce, isolate, hypothesize, minimal fix, verify. Use when a test fails, the device misbehaves, or detection accuracy drops.
argument-hint: "[symptom]"
---
# Debug: $ARGUMENTS
1. Reproduce deterministically: failing test, fixture/session WAV, or a logs/*.log excerpt. No repro → ask for the smallest one (one question).
2. Isolate the layer: capture → DSP → practice logic → UI → anchor. Bisect with logs and flags before reading lots of code.
3. ≤3 hypotheses, each with the evidence that would confirm or kill it. Test the cheapest first.
4. Minimal fix + regression test (synthetic or fixture). No threshold changes without audio-eval numbers.
5. ./scripts/verify.sh (+ --eval). Report root cause (1 line), fix, test added, level verified.
Device-only bugs: ask the human to run ./scripts/logs.sh or ./scripts/pull-session.sh, then analyze the files.
