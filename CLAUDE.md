# Untitled Guitar App — project brain (keep ≤120 lines)
AR guitar coach for absolute beginners on XREAL Air 2 Ultra, driven by a MacBook over USB-C (no Android, no webcam).
The glasses = side-by-side 3D display + IMU (via tools/imu-bridge) + mics/speakers. Unity runs on the Mac (Unity MCP).
Immerse the Bay 2026 · feature freeze Sun 2026-11-15 01:00 PT · Devpost deadline 09:00 PT.

## Start of every session
1. The SessionStart hook prints the countdown, branch, which person this clone works for, that person's next slice and the
   head of docs/STATUS.md (newest first). Read it.
2. Work only on the slice you were given (docs/TODO.md). Open other docs on demand; don't preload.
3. Product truth: docs/PRD.md + docs/DECISIONS.md. Contracts: unity/Assets/UntitledGuitarApp/Core/Contracts/*.cs.
4. Specs on demand: docs/GHOST_HANDS.md (the core feature) · docs/HOW_IT_WORKS.md (glasses + Mac) · docs/WORKSPLIT.md (who owns what) · docs/SONGS.md · docs/DATA.md.

## Commands
- Verify before finishing: `./scripts/verify.sh` (add `--eval` if you touched Core/Audio)
- Core tests only: `dotnet test core-tests -v q`
- Audio eval vs baseline: `./scripts/audio-eval.sh`
- Unity compile check (L3): Unity MCP `refresh_unity` → `read_console` → `run_tests` (EditMode)
- Run on the glasses (ask before building; a human wears them): `/glasses-run`
- Logs to a file: `./scripts/logs.sh` (UGA lines from Unity's Editor/Player log → logs/*.log; never paste raw logs into chat)
- IMU bridge: `./scripts/imu-bridge.sh` (UDP 127.0.0.1:47800; without it Unity runs headLocked)

## Architecture boundaries
- Core = unity/Assets/UntitledGuitarApp/Core/** — pure C# (netstandard2.1, C# 9). No UnityEngine/UnityEditor, no threads, no hidden statics.
  Theory, songs, DSP, practice logic, scoring. Built and tested outside Unity via core-build/ + core-tests/.
- Runtime = unity/Assets/UntitledGuitarApp/Runtime/** — thin Unity layer (XR rig, mic, anchors, ghost hand, HUD, spectator, flags, logging).
  Talks to Core only through Core/Contracts.
- Songs: unity/Assets/StreamingAssets/songs/*.song.json (schema v1). Original, public-domain, or chords-only (progression + credit, no lyrics; docs/SONGS.md).
- Fixtures core-tests/Fixtures/** and docs/AUDIO_BASELINE.md are human-owned ground truth.
- Flags: StreamingAssets/flags.default.json, overridden by <persistentDataPath>/flags.json (scripts/use-flags.sh).

## Two people, two lanes (hook-enforced; full table in docs/WORKSPLIT.md)
The SessionStart line "you work for: PERSON N" says whose agent you are. Only a human changes it (./scripts/set-person.sh).
- **Person 1 = XR** (Unity, the glasses, ghost hands, HUD, Blender, demo ops). Lane: unity/** except Core/** and
  StreamingAssets/songs/** · tools/imu-bridge/** · art/**. Only Person 1's session opens Unity, uses the Unity MCP or runs L3–L5.
- **Person 2 = Core** (audio/DSP, theory, songs, practice logic, data, eval, Devpost). Lane: unity/Assets/UntitledGuitarApp/Core/**
  (not Contracts) · core-build/** · core-tests/** · StreamingAssets/songs/** · tools/flags/** · data/ · docs/DATA.md · docs/SONGS.md.
- The other person's lane is READ-ONLY for you: read it, never edit it, never route around the hook (no Bash writes).
  Need a change there? Add "Person N → Person M: <request>" under Blockers in docs/STATUS.md, then keep working in your lane.
- Seam: Core/Contracts/** and docs/ARCHITECTURE.md change only by a contract PR both people OK (additive after Sat 09:00).
- Shared: docs/STATUS.md and docs/DECISIONS.md (add your own lines, labelled Person 1 / Person 2; never rewrite the other
  person's) · docs/TODO.md (only your slices; in a mixed slice only your half).
- Mixed slices (owner: Person 1 · Person 2): build ONLY your half, and never delegate the other half to a subagent.

## Hard rules
1. One slice per session; stay inside its owned paths and your person's lane (above). Need something outside? Stop and say why.
2. Tests first for Core. Done = verify green + acceptance criteria ticked with evidence + "Verified to L_ · pending L_".
3. Never weaken a test, threshold, fixture or baseline to get green. Threshold changes need audio-eval numbers in the commit.
4. Never hand-edit .unity/.prefab/.asset/.meta or ProjectSettings (hook-enforced). Use Unity MCP tools or an Editor script.
5. Never invent APIs or packet layouts (Unity multi-display, the glasses' HID/IMU protocol). Find them in docs/apis/** or package source and cite the path.
6. No package changes (Packages/manifest.json) without asking a human.
7. No copyrighted lyrics, melodies, tabs or chord sheets. Ever. Chord-only progressions follow docs/SONGS.md.
8. Every network call has a timeout and an offline fallback. No secrets in the repo.
9. Additive display: black = invisible. Bright emissive colors, bold text, min ≈2.5 cm cap height at 1.2 m.
10. On this setup the glasses give us ONLY an SBS stereo display, IMU rotation and mics/speakers. No cameras, 6DoF, hand or image
    tracking (those need XREAL's Android SDK). Don't design features that need them.
11. Log with UGALog (tag UGA, one JSON object per line). No Debug.Log in Update().
12. Core-only work happens in worktrees; Unity only in the main checkout. Never open Unity on a worktree.
13. Ambiguous? Ask ONE question with your recommended answer (grill-me style). Never ask what you can look up.
14. Finish with `/handoff`. Worktree workers instead put their STATUS/TODO lines in their report.
15. macOS ships BSD tools: edit files with the Edit tool, never `sed -i`; avoid GNU-only flags in scripts.
16. Process skills (e.g. superpowers) may help with debugging and verification, but this repo's workflow wins: work comes
    from docs/TODO.md via /build-slice. Don't start a separate brainstorm/plan flow or create extra plan files.

## Glossary
Ghost hands (fretting + strumming, glowing translucent blue) · fingertip dots/rings (source of truth for positions) ·
approach rings (timing) · neck mode (Aligned | headLocked) ·
pluck check (string-by-string pitch) · strum check (whole chord) · wait mode (playhead holds until the right chord) ·
L0–L5 verification levels (docs/MASTER_PLAN.md §9).

## Agents (delegate inside your own lane; keep this context small)
Person 1's: xr-engineer (sonnet, main checkout) · ux-critic
Person 2's: dsp-engineer (opus, worktree) · slice-builder (sonnet, worktree) · music-teacher · demo-director (opus)
Either: architect (opus; contracts, both people OK) · adversarial-qa (read-only) · scribe (haiku; your own lines only)
