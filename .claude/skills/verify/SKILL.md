---
name: verify
description: How to verify Untitled Guitar App changes and report the verification level (L0–L5). Use before declaring any task done or when asked whether something works.
---
# Verify
- L1 (always): ./scripts/verify.sh — Core purity, JSON, netstandard2.1 build, unit + synthetic + song tests; writes the Stop-gate stamp.
- L2 (Core/Audio changed): ./scripts/audio-eval.sh — confusion matrix vs docs/AUDIO_BASELINE.md; any metric −3 points = fail.
- L3 (Runtime/Editor changed): Unity MCP refresh_unity → read_console (zero errors from our assemblies) → run_tests (EditMode).
- L4: Play Mode in EditorSimRig with audioSource=wav; describe what you observed.
- L5: only a human on the glasses. Give a numbered checklist from docs/DEVICE_TEST.md for the changed feature; ask for results.
L3–L5 exist only on Person 1's Mac. On Person 2's clone stop at L1/L2 and end with `pending L3–L5 (Person 1)`.
Last line of every task: `Verified to L_ · pending L_ · evidence: <tests / numbers / human check>`.
