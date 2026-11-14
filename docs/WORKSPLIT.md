# Work split for 2 people (and how not to step on each other)

*Decided Sat 2026-11-14 ~00:00 PT. Relabelled Sat ~01:00 PT: the two people are **Person 1** and **Person 2** in every doc,
hook and agent prompt, and the lanes below are now enforced by a hook.*

## Who is who

| Label | Who | In one line |
|---|---|---|
| **Person 1** | Andrew, the Unity + glasses person | **XR** = everything you see |
| **Person 2** | the teammate (put your name here) | **Core** = everything you hear and score |

Each clone tells its agents which person it works for. A **human** runs this once after cloning (worktrees share it):

```bash
./scripts/set-person.sh 1    # on the Mac with Unity and the glasses
./scripts/set-person.sh 2    # on the Core laptop
```

## The split in one line

**Person 1 = everything you see** (Unity, the glasses, ghost hands, Blender, HUD, demo ops).
**Person 2 = everything you hear and score** (pure C# Core: audio, theory, songs, practice logic, data, eval, Devpost).
They meet only at the **contracts** (`Core/Contracts/*.cs`), which freeze at Sat 09:00.

| | Person 1 (XR) | Person 2 (Core) |
|---|---|---|
| Owns paths | `unity/Assets/UntitledGuitarApp/{Runtime,Editor,Tests,Art,Scenes}/**`, prefabs/materials (via Unity MCP), `StreamingAssets/flags*.json`, `StreamingAssets/poses/**`, `tools/imu-bridge/**`, `art/**`, and everything else under `unity/` that isn't in the next column | `unity/Assets/UntitledGuitarApp/Core/**` (except Contracts), `core-build/**`, `core-tests/**`, `StreamingAssets/songs/**`, `tools/flags/**`, `data/` (local), `docs/DATA.md`, `docs/SONGS.md`, `docs/AUDIO_BASELINE.md` (human-only) |
| Slices (`docs/TODO.md`) | SL0, SL4, SL8, SL9 + the UI half of SL1–SL3, SL5, SL6 | SL11 (data) + the Core half of SL0, SL1–SL3, SL5–SL8, SL10 |
| Machine | the MacBook with the glasses and Unity (+ Unity MCP) | own laptop; needs only git, .NET 8+ SDK, python3, Claude Code. No Unity needed |
| Claude Code | **one** session at the repo root on the Mac (`xr-engineer`, `ux-critic`) | one session in their own clone, plus up to 2 worktrees (`claude -w audio`, `claude -w logic`) for `dsp-engineer` / `slice-builder` |
| Hardware | owns the glasses and runs every L5 check | sends L5 requests (≤8-item checklists in `docs/DEVICE_TEST.md`) at sync points |
| Also | Blender poses, rehearsals, demo driver | recordings and eval, tips wording (`music-teacher`), `DEMO.md` + Devpost draft (`demo-director`) |

`Core/Contracts/**` and `docs/ARCHITECTURE.md` belong to the `architect` agent. Change them only through a "contract PR"
that both of you OK. After Sat 09:00, contract changes must be **additive** (new members only), so nothing breaks the other side.

## What each person's agents do, and never do

| | Person 1's agents (XR) | Person 2's agents (Core) |
|---|---|---|
| **Do** | Runtime, Editor and EditMode-test C#; scenes, prefabs and materials through the Unity MCP; the ghost-hand shader and poses; the IMU bridge; HUD, spectator, flags plumbing, build script. Verify to L3 (Unity compiles, EditMode tests) and L4 (EditorSimRig), then hand the human an L5 checklist | Core C#, tests first: theory, geometry, songs, audio/DSP, practice logic; song JSON; flag profiles in `tools/flags/`; datasets and the eval harness; tips wording; `DEMO.md` and the Devpost draft. Verify to L1 (`./scripts/verify.sh`) and L2 (`./scripts/audio-eval.sh`) |
| **Never** | edit `Core/**`, `core-build/**`, `core-tests/**`, `StreamingAssets/songs/**`, `tools/flags/**`, `docs/DATA.md` or `docs/SONGS.md`. Never "just fix" a Core bug or a failing Core test, and never change a threshold: report it | edit anything else under `unity/` (Runtime, Editor, Tests, Art, Scenes, prefabs, materials, `StreamingAssets/flags*.json`, `StreamingAssets/poses/**`), `tools/imu-bridge/**` or `art/**`. Never open Unity, use the Unity MCP, build the app, commit a `.meta` file or claim L3–L5 |
| **Their subagents** | `xr-engineer`, `ux-critic` | `dsp-engineer`, `slice-builder`, `music-teacher`, `demo-director` |
| **Their skills** | `/glasses-run`, `/seed-demo`, `/ux-review` | `/audio-eval`, `/record-fixtures`, `/add-song`, `/demo-script` |
| **Mixed slices** (`owner: Person 1 · Person 2`) | build only the Unity/UI half | build only the Core half |

Both people use `/build-slice` (their own slices, their own half), `/verify`, `/debug-loop`, `/handoff`, `/grill-plan` and
`/adversarial-review`, plus the `architect` (contracts only; both must OK), `adversarial-qa` (read-only) and `scribe` (your own
lines only).

The other lane is **read-only**. An agent may read anything, but it edits only its own person's lane.

## How the lanes are enforced

1. `./scripts/set-person.sh 1|2` stores the person in `.git/config` (`uga.person`). It is never committed, and worktrees share it.
2. The SessionStart hook opens every session with "you work for: PERSON N", your lane, the other lane and your open slices.
3. `protect-files.sh` runs before every Edit/Write. It blocks edits in the other person's lane and tells the agent what to do
   instead. Until the person is set, it blocks both lanes.
4. `.claude/settings.json` denies agents `set-person.sh` and any `git config … uga.person …`. Only a human changes the person.
5. Hooks see Edit/Write only, not Bash writes or MCP tools. So the same rule is also written in `CLAUDE.md`, the path rules and
   every builder agent's prompt.

**Emergency only** (the other person can't be reached and `main` is red): a human starts Claude with `UGA_CROSS_LANE=1 claude`,
makes the smallest possible fix, and says so in a `STATUS.md` line. Agents never set it.

## Asking the other person for something

- Add one line under **Blockers** in `docs/STATUS.md`: `Person 1 → Person 2 · HH:MM · <what you need, by when>` (union merge,
  so no conflicts). Commit, push, and carry on with other work in your lane.
- A contract change goes through the `architect` and needs both people's OK (additive after Sat 09:00).
- A device check (Person 2 → Person 1): a ≤8-item checklist in `docs/DEVICE_TEST.md`, run at the next sync point.

## Git rules

1. `main` is always green: `./scripts/verify.sh` passes, and Person 1's Unity compiles it with no errors (L3).
2. Branches: `person1/<slice>` and `person2/<slice>`, or Claude Code worktrees (`claude -w audio` → branch `worktree-audio`).
   Small PRs; the owner merges after verify plus `/adversarial-review` for risky diffs.
3. Pull `main` before you start every slice, and at every sync point.
4. Never run two Claude sessions on the same checkout. Never open Unity on a worktree (it re-imports for minutes).
5. Shared docs:
   - `docs/STATUS.md` and `docs/DECISIONS.md` use git's **union merge** (set in `.gitattributes`), so both of you can append
     without conflicts. Newest first, one line per entry, carrying your label (`Person 1` or `Person 2`): after the time in
     STATUS, in the `who` field in DECISIONS. Never rewrite the other person's lines.
   - `docs/TODO.md`: edit only the status line of **your** slices. In a mixed slice, tick only the boxes your half proves;
     Person 1 sets `status: done` after the device check.
   - `CLAUDE.md`, `.claude/**`, `scripts/verify.sh`, `scripts/set-person.sh`: harness files. Change them by PR that both of
     you OK (hooks stop agents from editing them anyway).
6. Unity files: Person 2 never commits anything under `unity/` except Core `.cs` files and `StreamingAssets/songs` JSON.
   `StreamingAssets/flags*.json` is Person 1's file: Person 2 tunes the profiles in `tools/flags/` and asks Person 1 to
   mirror a change into the defaults.
   Unity generates `.meta` files for new Core scripts on Person 1's machine; **Person 1 commits them**. If both sides ever
   create a `.meta` for the same file, keep Person 1's (Core classes are never referenced by GUID, so nothing breaks).
7. Never commit `Library/`, `build/`, `logs/`, `data/` or secrets (all gitignored).

## Sync points (15 min each, both people)

| Time (PT) | What happens |
|---|---|
| Sat 07:00 | Person 2 starts: clone, `./scripts/set-person.sh 2`, verify green, Stage 2 with the `architect` (contract stubs) |
| Sat 08:30 | review the contracts together, commit "spec v1"; record dev clips if not done tonight |
| Sat 09:00 | **contract freeze**; merge Core SL0; Person 1 pulls and compiles in Unity |
| Sat 11:00 | **G1**: alignment on the real neck passes, or we switch to headLocked |
| Sat 13:00 | merge → L5 on the glasses → tag `good-1300` (aligned neck + fretting ghost hand + pluck check) |
| Sat 16:00 | merge → L5 → tag `good-1600` (play-along with both ghost hands + wait mode) → **record a rough-cut video** |
| Sat 18:00 | **G2**: cut decision (`MASTER_PLAN.md` §23 cut order) |
| Sat 22:00 | rehearsal #1, adversarial review of `main` |
| Sun 01:00 | **feature freeze**: bug fixes only, each with an adversarial-qa sign-off |

At each sync: pull → `./scripts/verify.sh` → Person 1 opens Unity (L3) → L5 checks from both lists → tag → one line each in
`STATUS.md`.

## Hour by hour (Saturday)

| Time | Person 1 (XR) | Person 2 (Core) |
|---|---|---|
| 07:00–09:00 | spikes M-S4–M-S6 · SL0: stereo rig, floating neck, GlassesGameView | Stage 2 contracts (until 08:30) → SL0-core: ChordLibrary (G C D Em Am), FretboardGeometry |
| 09:00–11:00 | SL8: IMU head pose + 4-glance alignment → G1 | SL1-core tuner (YIN) · SL2-core pluck verifier on synthetic audio |
| 11:00–13:00 | SL4a: fretting ghost hand (G, Em first) + pluck-check UI | SL2 done on dev clips · SL3-core strum verifier · song parser + validator |
| 13:00–16:00 | SL4b: C, D, Am poses + strumming ghost hand · SL5 UI (neck HUD, lyric strip) | SL5-core: timeline, wait mode, scoring · record the **eval set** with Person 1 (15 min) |
| 16:00–18:00 | SL6 UI: pattern strips synced to the strumming hand · SL9 demo ops | SL6-core timing · eval table · tips with `music-teacher` · `DEMO.md` draft |
| 18:00–01:00 | polish from ux-review photos · rehearsals | Devpost draft · venue-noise eval · bug fixes |

Friday night (Person 1 only): repo on GitHub, Unity setup, spikes M-S0–M-S3, Blender rig, guitar measurements; dev clips if
there's time. Person 2 starts Sat 07:00: verify green, `./scripts/get-datasets.sh` in the background, Stage 2 with the `architect`.

## The interference checklist (read it once, then trust the hooks)

- [ ] My clone knows who I am: `./scripts/set-person.sh` prints the right person.
- [ ] I only edit my own lane. Anything else is a request in `STATUS.md` or a contract PR.
- [ ] In a mixed slice I built only my half.
- [ ] One Claude session per checkout; Unity only in Person 1's main checkout.
- [ ] I pulled `main` before starting this slice.
- [ ] My slice report ends with "Verified to L_ · pending L_".
- [ ] I put my STATUS line in (union merge), not a rewrite of someone else's lines.
- [ ] Before merging to `main`: verify green; `/adversarial-review` if I touched audio threading, state machines or flags.
