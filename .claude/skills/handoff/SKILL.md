---
name: handoff
description: End-of-session context compression — updates STATUS/TODO/ARCHITECTURE so the next session or teammate starts cold, then recommends /clear.
disable-model-invocation: true
---
# Handoff
!`git log --oneline -10`
!`git status --short | head -n 20`
Use the scribe agent with: what changed this session (with verification levels), blockers, contract changes, open questions
(each with a recommended answer). Then say: "Handoff written — run /clear before the next slice."
