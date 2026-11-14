---
name: adversarial-review
description: Fresh-context hostile review of the current diff or demo path by the adversarial-qa agent. Use before risky merges, before freeze, and before rehearsals.
context: fork
agent: adversarial-qa
background: false
disable-model-invocation: true
---
Review Untitled Guitar App for demo-breaking problems. Scope: `git diff main...HEAD` if on a branch, otherwise the last 10 commits, plus
docs/PRD.md and docs/DEVICE_TEST.md. Assume a loud expo hall, flaky Wi-Fi, a nervous presenter, a judge who never held a guitar,
MacBook on battery, and the IMU bridge dropping every few seconds.
Output P0/P1/P2 (max 25): file:line → failure scenario → one-line fix. End with the single riskiest thing in the demo.
