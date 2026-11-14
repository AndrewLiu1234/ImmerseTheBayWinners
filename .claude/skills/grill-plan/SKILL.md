---
name: grill-plan
description: Interview the team (grill-me discipline) through the open questions in docs/MASTER_PLAN.md Part III and record answers in docs/DECISIONS.md. Use for the 🔴 set tonight and whenever a slice is blocked on a decision.
argument-hint: "[optional: category or question id, e.g. AUD or Q-REG-01]"
disable-model-invocation: true
---
# Grill the plan ($ARGUMENTS)
Already decided (one per line; empty = none yet):
!`grep -oE 'D-Q-[A-Z0-9]+-[0-9]+' docs/DECISIONS.md 2>/dev/null | head -n 200`
1. Take the next undecided question in docs/MASTER_PLAN.md §29 order, then §30 (🔴 → 🟠 → 🟢), or the one named above.
2. First try to answer it from the repo (ProjectVersion.txt, Packages/, docs/, STATUS.md). If you can, record it as
   decided-by-evidence and move on.
3. Ask ONE question at a time with your recommended answer (multiple choice when available) and ≤1 sentence on why it matters.
4. Append to docs/DECISIONS.md (newest first): `D-<QID> · <answer> · <who> · <HH:MM PT> · reversible: yes|no · affects: <…>`.
5. If an answer contradicts an earlier decision or the plan, say so and ask whether to reopen it.
6. Stop when asked or when the set is done; summarize what changes in PRD/TODO terms.
