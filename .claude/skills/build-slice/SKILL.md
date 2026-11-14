---
name: build-slice
description: Implement exactly one vertical slice from docs/TODO.md end-to-end (tests first, verify, report). Use when the user says "build slice SLn".
argument-hint: "[slice id, e.g. SL3]"
disable-model-invocation: true
---
# Build slice $ARGUMENTS
!`cat docs/STATUS.md 2>/dev/null | head -n 15`
!`git status --short | head -n 20`

1. Find the $ARGUMENTS block in docs/TODO.md and read its `owner:`. You work for the person in the SessionStart line
   ("you work for: PERSON N"). Your person isn't an owner → stop: it's the other person's slice. Mixed slice
   (`Person 1 · Person 2`) → build ONLY your half (Person 1 = Unity/UI, Person 2 = Core).
   Restate goal, acceptance, owned paths and out-of-scope in ≤8 lines.
2. Ambiguity that would change code → ask ONE question with your recommended answer, then wait.
3. Delegate with self-contained briefs (workers don't see this conversation), inside your own lane only. Person 2: Core
   audio → dsp-engineer; other Core → slice-builder (both in worktrees; run in parallel when independent). Person 1: Unity →
   xr-engineer (main checkout). Never brief a worker to build the other person's half: write what you need from them as a
   `Person N → Person M:` line under Blockers in docs/STATUS.md.
4. Merge worker branches only when their verify is green; then L3 via Unity MCP for Runtime changes; L4 in EditorSimRig.
5. If the slice has device acceptance: give the human a numbered checklist (≤8) and wait for pass/fail (L5).
6. Use the scribe to apply STATUS/TODO/ARCHITECTURE lines. Propose: `feat(<slice>): <summary> [L_]`.
