# The harness: how Claude Code agents stay fast, on-task and honest here

*Why this repo has `CLAUDE.md`, `.claude/{rules,agents,skills,hooks}`, `scripts/verify.sh` and living docs. Full files:
`MASTER_PLAN.md` Appendix A (already unpacked into this repo; the repo files are the source of truth and have changed since:
Person 1 / Person 2 lane guard, Sat ~01:00 PT).*

The idea: **models are good at writing code and bad at knowing when they're wrong.** So the harness gives every agent a small,
precise context, makes the important rules mechanical (hooks and tests, not "please don't"), and makes every claim
checkable (verification levels, eval sets, logs on disk).

## 1. Context engineering: what each agent sees, and when

| Layer | Loads | Contents | Size |
|---|---|---|---|
| `CLAUDE.md` | every session, always | what the project is, session ritual, commands, boundaries, 16 hard rules, glossary | ~55 lines (limit 120) |
| SessionStart hook | at start, after `/clear`, after compaction | countdown to freeze, branch, **which person this clone works for and both lanes**, that person's open slices, top of `STATUS.md`, uncommitted files | ~30 lines |
| Path-scoped rules (`.claude/rules/*.md`) | only when the agent touches matching files | Core purity, audio/DSP, Unity + glasses, songs, docs style | 5–15 lines each |
| Nested `CLAUDE.md` | when working in `unity/` or `core-tests/` | Unity specifics (display 2, threading, Unity MCP) or test conventions | ~20 lines each |
| Skills (`.claude/skills/*/SKILL.md`) | only when invoked, or when Claude decides they apply | step-by-step procedures (build a slice, debug, eval, handoff…) | on demand |
| Docs | read by reference, never preloaded | PRD, ARCHITECTURE, TODO (one slice at a time), GHOST_HANDS, HOW_IT_WORKS, SONGS, DATA | on demand |
| `docs/apis/**` | when writing code against an outside API | exact excerpts (Unity multi-display, the glasses' HID protocol, hidapi) | on demand |
| Subagents | their own context window | builders and reviewers return ≤30-line reports, so the main session stays small | isolated |

Rules that keep context clean:
- **One slice per session.** `/build-slice SL4` → verify → `/handoff` → `/clear`. Long sessions rot; files don't.
- **External memory.** `STATUS.md`, `TODO.md` and `DECISIONS.md` are the memory between sessions, people and compactions.
- **No noise in chat.** Logs go to `logs/*.log` (`scripts/logs.sh`); agents grep them. Tests run quiet (`-v q`).
- **Fresh eyes.** Review skills fork a separate agent (`context: fork`), so the reviewer never sees the builder's reasoning.
- **Facts from files, not memory.** APIs and packet layouts must be cited from `docs/apis/**` or package source (hard rule 5).

## 2. Guardrails that don't depend on the model's mood (hooks + permissions)

| Hook | When | What it does |
|---|---|---|
| `protect-files.sh` | before every Edit/Write | blocks secrets, Unity YAML (`.unity .prefab .asset .meta .mat …`), ProjectSettings, fixtures, the audio baseline and the harness itself. Then the **lane guard**: Person 1's agents can't edit Person 2's lane, and the other way round (`WORKSPLIT.md`). **Fails closed** |
| `post-edit-check.sh` | after every Edit/Write | Core must not import UnityEngine; JSON must parse |
| `stop-gate.sh` | when an agent (or a builder subagent) tries to finish | if Core/test/content files changed and `verify.sh` hasn't passed on **this exact diff**, it sends the agent back once. **Fails open** so nobody gets trapped |
| `session-context.sh` | session start / after compaction | injects the countdown, which person this clone works for (a human sets it once with `./scripts/set-person.sh`) and current state |

Permissions: **allow** safe, common commands (verify, dotnet, git status/diff/commit, Unity MCP read/test/scene tools);
**ask** for builds, pushes, merges and package changes; **deny** force-push, hard reset, `rm -rf`, reading `.env`, changing
the person setting, and the
Unity MCP *script-editing* tools, so C# always goes through Edit/Write, where the hooks can see it.

## 3. Verification: every claim has a level

| Level | Proof | Who |
|---|---|---|
| L0 | hooks (purity, JSON) | automatic |
| L1 | `./scripts/verify.sh`: Core builds as netstandard2.1, unit + synthetic-audio + song tests | every agent, every slice |
| L2 | `./scripts/audio-eval.sh` on recorded clips vs a frozen baseline | `dsp-engineer` |
| L3 | Unity compiles, console clean, EditMode tests (Unity MCP) | `xr-engineer` |
| L4 | Editor simulation: mouse-look head + WAV replay | `xr-engineer` |
| L5 | on the glasses with a real guitar (`DEVICE_TEST.md`) | Person 1 (the human) |

Every report ends with "Verified to L_ · pending L_". Accuracy numbers come only from the eval set recorded through the app
and frozen before tuning (`DATA.md`).

## 4. Agents and model routing

| Agent | Model | Job |
|---|---|---|
| `architect` | Opus | contracts, ARCHITECTURE, design forks (Stage 2) |
| `dsp-engineer` | Opus | audio algorithms, thresholds, eval (worktree) |
| `slice-builder` | Sonnet | one Core slice end-to-end (worktree) |
| `xr-engineer` | Sonnet | Unity runtime/editor through the Unity MCP (main checkout only) |
| `music-teacher` | Sonnet | chord shapes, fingerings, tips, licensing checks |
| `ux-critic` | Sonnet | additive-display legibility, beginner clarity (reads through-the-lens photos) |
| `adversarial-qa` | Opus | read-only hostile review before merges and freeze |
| `scribe` | Haiku | keeps STATUS/TODO/ARCHITECTURE short and current |
| `demo-director` | Opus | demo script, Devpost text, judge Q&A |

Expensive judgment (design, DSP, attacks on our own work) gets Opus; volume work gets Sonnet; bookkeeping gets Haiku.

## 5. Prompt engineering

- **Slices are prompts.** Every `TODO.md` block has goal, owned paths, contracts, automated + device acceptance,
  out-of-scope, fallback and a time box. `/build-slice` restates it in ≤8 lines before writing code.
- **Tests first** for Core, with synthetic audio that contains the exact mistake we want to catch.
- **One question, with a recommendation.** Agents ask at most one question at a time and say what they'd pick (grill-me
  discipline), otherwise they proceed and list ASSUMED items.
- **Prompt library** (`MASTER_PLAN.md` §13, P1–P7): Stage 2 spec, slice kickoff, hostile review, debug, demo, handoff.

## 6. Which skill, when

**Project skills (in this repo)**

| Stage | Skill | Use it to |
|---|---|---|
| Decisions | `/grill-plan` | walk the open questions one at a time and write `DECISIONS.md` |
| Build | `/build-slice SLn` | the slice loop: restate → tests first → implement → verify → report |
| Build | `verify` (auto) | run L1–L3 and state the level reached |
| Build | `/debug-loop` | reproduce → isolate → hypotheses → minimal fix → verify |
| Audio | `/record-fixtures`, `/audio-eval` | the recording protocol; the eval table vs baseline |
| Content | `/add-song` | turn an original / public-domain / chords-only chart into validated song JSON |
| Device | `/glasses-run` | Play Mode on the glasses + IMU bridge + logs + a short human checklist |
| Review | `/adversarial-review`, `/ux-review` | fresh-context reviews before merges and after device photos |
| Memory | `/handoff` | compress the session into STATUS/TODO, then `/clear` |
| Demo | `/seed-demo`, `/demo-script` | reset demo state; write `DEMO.md` and the Devpost draft |

**External skills**

| Skill | Status | Why |
|---|---|---|
| `grill-me` (mattpocock/skills) | install tonight | the one-question-with-a-recommendation interview; `/grill-plan` wraps it |
| `pitch-package` (ooiyeefei/ccc) | install tonight | Stage 5: pitch, video plan, Q&A, day-of checklist |
| impeccable | already on Person 1's Mac (`~/.impeccable`) | critique vocabulary for `ux-review` and the Devpost page. Its web-UI defaults lose to our additive-display rules |
| superpowers | probably on Person 1's Mac (`~/.superpowers`) | keep `systematic-debugging` and `verification-before-completion` (they match our rules). In this repo, **our workflow wins**: slices come from `TODO.md`, so don't start its brainstorm → plan → execute flow or create extra plan files |
| skill-creator (Anthropic) | optional | if one of our skills doesn't trigger when it should, use it to test and tighten the description |
| pytheory, hackathon-demo-video | optional | cross-check chord notes; storyboard the video |

Read every community SKILL.md before committing it. Skills are instructions that run with your permissions.

## 7. Built-in Claude Code commands we rely on

`/mcp` (check the Unity MCP server name) · `/agents` · `/hooks` · `/clear` · `/compact` · `/doctor prompt-audit` (finds
contradictions between CLAUDE.md, rules and skills; needs Claude Code v2.1.283+) · `claude -w <name>` (worktree session).
