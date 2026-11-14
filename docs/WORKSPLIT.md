# Work split for 2 people (and how not to step on each other)

*Decided Sat 2026-11-14 ~00:00 PT. "P2" = your teammate; replace with their name.*

## The split in one line

**Andrew = everything you see** (Unity, the glasses, ghost hands, Blender, HUD, demo ops).
**P2 = everything you hear and score** (pure C# Core: audio, theory, songs, practice logic, data, eval, Devpost).
They meet only at the **contracts** (`Core/Contracts/*.cs`), which freeze at Sat 09:00.

| | Andrew (XR) | P2 (Core) |
|---|---|---|
| Owns paths | `unity/Assets/UntitledGuitarApp/{Runtime,Editor,Tests,Art,Scenes}/**`, prefabs/materials (via Unity MCP), `StreamingAssets/flags*.json`, `StreamingAssets/poses/**`, `tools/imu-bridge/**`, `art/**` | `unity/Assets/UntitledGuitarApp/Core/**` (except Contracts), `core-build/**`, `core-tests/**`, `StreamingAssets/songs/**`, `tools/flags/**`, `data/` (local), `docs/DATA.md`, `docs/SONGS.md`, `docs/AUDIO_BASELINE.md` (human-only) |
| Slices (`docs/TODO.md`) | SL0, SL4, SL8, SL9 + the UI half of SL1–SL3, SL5, SL6 | SL11 (data) + the Core half of SL0, SL1–SL3, SL5–SL8, SL10 |
| Machine | the MacBook with the glasses and Unity (+ Unity MCP) | own laptop; needs only git, .NET 8+ SDK, python3, Claude Code. No Unity needed |
| Claude Code | **one** session at the repo root on the Mac (`xr-engineer`, `ux-critic`) | one session in their own clone, plus up to 2 worktrees (`claude -w audio`, `claude -w logic`) for `dsp-engineer` / `slice-builder` |
| Hardware | owns the glasses and runs every L5 check | sends L5 requests (≤8-item checklists in `docs/DEVICE_TEST.md`) at sync points |
| Also | Blender poses, rehearsals, demo driver | recordings and eval, tips wording (`music-teacher`), `DEMO.md` + Devpost draft (`demo-director`) |

`Core/Contracts/**` and `docs/ARCHITECTURE.md` belong to the `architect` agent. Change them only through a "contract PR"
that both of you OK. After Sat 09:00, contract changes must be **additive** (new members only), so nothing breaks the other side.

## Git rules

1. `main` is always green: `./scripts/verify.sh` passes, and Andrew's Unity compiles it with no errors (L3).
2. Branches: `andrew/<slice>` and `p2/<slice>`, or Claude Code worktrees (`claude -w audio` → branch `worktree-audio`).
   Small PRs; the owner merges after verify plus `/adversarial-review` for risky diffs.
3. Pull `main` before you start every slice, and at every sync point.
4. Never run two Claude sessions on the same checkout. Never open Unity on a worktree (it re-imports for minutes).
5. Shared docs:
   - `docs/STATUS.md` and `docs/DECISIONS.md` use git's **union merge** (set in `.gitattributes`), so both of you can append
     without conflicts. Newest first, one line per entry, prefixed with your name.
   - `docs/TODO.md`: edit only the status line of **your** slices.
   - `CLAUDE.md`, `.claude/**`, `scripts/verify.sh`: harness files. Change them by PR that both of you OK (hooks stop agents
     from editing them anyway).
6. Unity files: P2 never commits anything under `unity/` except Core `.cs` files and `StreamingAssets/{songs,flags}` JSON.
   Unity generates `.meta` files for new Core scripts on Andrew's machine; **Andrew commits them**. If both sides ever create a
   `.meta` for the same file, keep Andrew's (Core classes are never referenced by GUID, so nothing breaks).
7. Never commit `Library/`, `build/`, `logs/`, `data/` or secrets (all gitignored).

## Sync points (15 min each, both people)

| Time (PT) | What happens |
|---|---|
| Sat 07:00 | P2 starts: clone, verify green, Stage 2 with the `architect` (contract stubs) |
| Sat 08:30 | review the contracts together, commit "spec v1"; record dev clips if not done tonight |
| Sat 09:00 | **contract freeze**; merge Core SL0; Andrew pulls and compiles in Unity |
| Sat 11:00 | **G1**: alignment on the real neck passes, or we switch to headLocked |
| Sat 13:00 | merge → L5 on the glasses → tag `good-1300` (aligned neck + fretting ghost hand + pluck check) |
| Sat 16:00 | merge → L5 → tag `good-1600` (play-along with both ghost hands + wait mode) → **record a rough-cut video** |
| Sat 18:00 | **G2**: cut decision (`MASTER_PLAN.md` §23 cut order) |
| Sat 22:00 | rehearsal #1, adversarial review of `main` |
| Sun 01:00 | **feature freeze**: bug fixes only, each with an adversarial-qa sign-off |

At each sync: pull → `./scripts/verify.sh` → Andrew opens Unity (L3) → L5 checks from both lists → tag → one line each in
`STATUS.md`.

## Hour by hour (Saturday)

| Time | Andrew (XR) | P2 (Core) |
|---|---|---|
| 07:00–09:00 | spikes M-S4–M-S6 · SL0: stereo rig, floating neck, GlassesGameView | Stage 2 contracts (until 08:30) → SL0-core: ChordLibrary (G C D Em Am), FretboardGeometry |
| 09:00–11:00 | SL8: IMU head pose + 4-glance alignment → G1 | SL1-core tuner (YIN) · SL2-core pluck verifier on synthetic audio |
| 11:00–13:00 | SL4a: fretting ghost hand (G, Em first) + pluck-check UI | SL2 done on dev clips · SL3-core strum verifier · song parser + validator |
| 13:00–16:00 | SL4b: C, D, Am poses + strumming ghost hand · SL5 UI (neck HUD, lyric strip) | SL5-core: timeline, wait mode, scoring · record the **eval set** with Andrew (15 min) |
| 16:00–18:00 | SL6 UI: pattern strips synced to the strumming hand · SL9 demo ops | SL6-core timing · eval table · tips with `music-teacher` · `DEMO.md` draft |
| 18:00–01:00 | polish from ux-review photos · rehearsals | Devpost draft · venue-noise eval · bug fixes |

Friday night (Andrew only): repo on GitHub, Unity setup, spikes M-S0–M-S3, Blender rig, guitar measurements; dev clips if
there's time. P2 starts Sat 07:00: verify green, `./scripts/get-datasets.sh` in the background, Stage 2 with the `architect`.

## The interference checklist (read it once, then trust the hooks)

- [ ] I only edit my own paths. Anything else is a request in chat or a contract PR.
- [ ] One Claude session per checkout; Unity only in Andrew's main checkout.
- [ ] I pulled `main` before starting this slice.
- [ ] My slice report ends with "Verified to L_ · pending L_".
- [ ] I put my STATUS line in (union merge), not a rewrite of someone else's lines.
- [ ] Before merging to `main`: verify green; `/adversarial-review` if I touched audio threading, state machines or flags.
