---
name: ux-review
description: Fresh-context legibility and beginner-UX review of the AR HUD by the ux-critic agent, optionally with photos taken through the glasses.
context: fork
agent: ux-critic
background: false
argument-hint: "[optional image paths]"
disable-model-invocation: true
---
Review Runtime/Hud/**, Runtime/GhostHand/** and the flags for an absolute beginner wearing XREAL Air 2 Ultra while holding a
guitar. Images (if any): $ARGUMENTS. Rank ≤15 issues by "chance a beginner misreads or misses this within 10 seconds", with file
paths and concrete changes.
