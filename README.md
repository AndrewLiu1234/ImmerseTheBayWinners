# Untitled Guitar App

AR guitar coach for absolute beginners on **XREAL Air 2 Ultra** glasses. Put on the glasses, pick up a real guitar, and a
glowing blue **ghost hand** shows how to hold each chord on your own neck, while the app listens and tells you which string
is wrong. Then play a real song at your own speed. Built at **Immerse the Bay 2026** (Stanford XR).

**Status:** planning and harness complete; no app code yet. Feature freeze Sun 2026-11-15 01:00 PT · Devpost 09:00 PT.

## Hardware and software

- XREAL Air 2 Ultra + an Apple silicon MacBook, **one USB-C cable** (no Android, no webcam). How it works: `docs/HOW_IT_WORKS.md`.
- Unity **2021.3.3f1** (macOS standalone, Built-in render pipeline) + [MCP for Unity](https://github.com/CoplayDev/unity-mcp) so Claude Code can drive the editor.
- Claude Code (v2.1.283+), .NET SDK 8 or newer, Python 3 with `hidapi`, `jq` (optional; hooks fall back to python3).

## Setup

**Andrew (the Mac with the glasses and Unity)**
1. `claude update`. Then from this folder: `git init -b main && chmod +x scripts/*.sh .claude/hooks/*.sh`.
2. Unity: `./scripts/init-unity.sh` turns `unity/` into a Unity 2021.3.3f1 project (set `UNITY_PATH` if Unity lives elsewhere).
   Then Unity Hub → Add → `unity/` → open.
3. In Unity's Package Manager, add `com.unity.nuget.newtonsoft-json` and MCP for Unity (Add package from git URL, pinned to
   the same version as its server). In Unity: Window › MCP for Unity → configure Claude Code, or copy `.mcp.json.example` to
   `.mcp.json`. In Claude Code, `/mcp` must list the server. If it isn't called `unityMCP`, rename the
   `mcp__unityMCP__…` entries in `.claude/settings.json`.
4. Unity YAML merging: `git config merge.unityyamlmerge.driver "'/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Tools/UnityYAMLMerge' merge -p %O %B %A %A"`
5. `python3 -m pip install --user hidapi`, then `./scripts/imu-bridge.sh --list` with the glasses plugged in.
6. `./scripts/verify.sh` (one smoke test passes) → `git add -A && git commit -m "harness v1"` → push to GitHub.
7. Skills: `npx skills add mattpocock/skills --skill grill-me --agent claude-code` and
   `npx skills add ooiyeefei/ccc --skill pitch-package --agent claude-code` (read each SKILL.md before committing it).

**P2 (Core laptop, no Unity needed)**
1. Clone, install the .NET SDK, run `./scripts/verify.sh`.
2. `./scripts/get-datasets.sh` (GuitarSet + NSynth test; Kaggle needs an API token).
3. `claude` in your clone → `/build-slice SL11`, then your Core slices in `docs/TODO.md`.

## Your first Claude Code sessions

Don't paste the plan into Claude. `CLAUDE.md`, the session-start hook and the docs give every session its context, so
you only name the task. One slice per session, then `/handoff` and `/clear`. Details: `docs/HARNESS.md`.

**Andrew, tonight (after Setup)**
1. Type `/mcp` (the Unity MCP must be listed) and `/doctor prompt-audit` (skim the report).
2. Spike M-S1: plug in the glasses, set them to extended + 3D (`docs/HOW_IT_WORKS.md` §4), then ask:
   > Make a 3840×1080 side-by-side test image for spike M-S1 (red "L" on the left half, blue "R" on the right) and tell me how to show it fullscreen on the glasses.
3. Spike M-S2:
   > Spike M-S2 from docs/TODO.md: read the XREAL Air 2 Ultra's IMU on macOS. First write docs/apis/glasses/imu-protocol.md from the Monado xreal_air driver source (HID interfaces, enable command, packet layout; cite the file and commit; never guess bytes). Then fill in tools/imu-bridge/bridge.py and help me run ./scripts/imu-bridge.sh. Stop and report the rate and drift.
4. Put the spike results in `docs/STATUS.md`, run `/handoff`, sleep.

**Andrew, from 07:00:** `/build-slice SL0`, then SL8 (alignment) and SL4 (ghost hands), in the order in `docs/WORKSPLIT.md`.

**P2, from 07:00 (after Setup):**
> Use the architect agent to run prompt P1 from docs/MASTER_PLAN.md §13: contract stubs first, then refine docs/ARCHITECTURE.md and docs/TODO.md. Stop for review.

Review together at 08:30 and commit; contracts freeze at 09:00. Then `/build-slice SL0` (the Core half), and on through your
slices in `docs/WORKSPLIT.md`.

## Docs

| Doc | What |
|---|---|
| `docs/PRD.md` | product truth: requirements, scope, metrics |
| `docs/GHOST_HANDS.md` | the core feature: the glowing blue fretting + strumming hands (with Andrew's sketch) |
| `docs/HOW_IT_WORKS.md` | the one-cable Mac setup, alignment without cameras, and why an Android emulator doesn't help |
| `docs/WORKSPLIT.md` | who owns what, git rules, sync points, the hour-by-hour plan |
| `docs/TODO.md` | vertical slices with owners and acceptance criteria |
| `docs/SONGS.md` | Happy Birthday, Riptide and Stand By Me charts, and the content rules |
| `docs/DATA.md` | open chord datasets and our 20-minute recording protocol |
| `docs/HARNESS.md` | how the agents' context, hooks, verification and skills fit together |
| `docs/ARCHITECTURE.md` | modules, data flow, contracts, schemas |
| `docs/MASTER_PLAN.md` | everything, including the full grill (175 questions) and the adversarial review |
| `docs/STATUS.md` · `docs/DECISIONS.md` | the live log (newest first) |

## Repo map

```
CLAUDE.md  .claude/{settings.json, rules/, agents/, skills/, hooks/}   ← the agent harness
docs/                                   ← plans, specs, living docs
unity/                                  ← Unity project (Core lives in unity/Assets/UntitledGuitarApp/Core)
core-build/  core-tests/                ← Core compiled and tested with dotnet, no Unity
tools/imu-bridge/  tools/flags/         ← IMU → UDP bridge; runtime flag profiles
scripts/                                ← verify, eval, build, logs, flags, datasets, Unity init
```

## Credits and licenses

Datasets and assets we use are listed in `docs/CREDITS.md`. Songs: Happy Birthday (public domain); Riptide and Stand By Me
appear as chord progressions only, with no lyrics, melody or recordings.
