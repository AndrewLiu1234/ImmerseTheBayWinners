# PRD — Untitled Guitar App

*Product truth for agents and humans. Short on purpose; details live in `MASTER_PLAN.md`. Updated Sat 2026-11-14 00:15 PT.*

## Problem and promise

Most beginners quit guitar in the first weeks: chord shapes feel impossible, they can't tell *what* is wrong, and looking
back and forth between a chart and their hands breaks the flow.

**Promise:** put on the glasses, pick up a real guitar, and a glowing blue ghost hand shows exactly how to hold each chord on
your own neck, while the app listens and tells you which string is wrong. Then play a real song at your own speed.

**User:** an absolute beginner (has never played a chord) with a steel-string acoustic guitar.

## Platform

XREAL Air 2 Ultra + an Apple silicon MacBook over **one USB-C cable**. No Android, no webcam, no extra hardware. The glasses
are a 3D display, a head-motion sensor and mics/speakers; everything else runs on the Mac (`HOW_IT_WORKS.md`).
Unity 2021.3.3f1 (installed), driven by Claude Code through the Unity MCP.

## Fixed requirements (Andrew)

1. **Ghost hands are the core:** a glowing, translucent blue **fretting hand** and **strumming hand** on the **real** neck
   (`GHOST_HANDS.md`). Never cut, never reduced to dots.
2. The chord to play is always in front of your eyes.
3. Strumming **and** picking patterns are shown (and the strumming hand plays them).
4. Self-paced practice of every chord, with listening that says whether it's **correct** and **good** (rings out).
5. Play the song at normal **and slower** speeds.
6. Ultimate Guitar–style display (chords above lyrics) where lyrics are allowed. Lyrics themselves are **optional**: playing
   matters more than singing.

## Core experience

- **Setup:** tune (tuner), check the room noise, align the neck with 4 glances.
- **Learn a chord:** ghost hand shows the shape → finger by finger (each plucked note confirmed) → pluck check (✓/✕ per
  string + a tip like "Arch finger 3, B string muted") → strum check → mastered → next chord.
- **Play along:** pick a tempo (50–100%), count-in, the strumming hand and pattern strip keep time, the fretting hand morphs
  one beat before each change, approach rings shrink onto the next fingertip spots, **wait mode** holds until you play the
  change (and gives up gracefully after 3 tries or 8 s). Results at the end.
- **Hands-free:** auto-advance when the audio check passes; gaze-dwell buttons; the MacBook space bar as backup.

## Songs (`SONGS.md`)

Happy Birthday (public domain, with lyrics) · Riptide (chord loop) · Stand By Me (chord loop). Chords: G, C, D, Em, Am.

## Scope

- **MUST:** chord library + fretboard geometry (G C D Em Am) · aligned neck (IMU + 4 glances + re-anchor) with headLocked
  fallback · **both ghost hands** with morph · tuner · Learn mode (finger by finger, pluck, strum, ring-out quality, mastery,
  tips) · play-along (neck HUD, strum + picking strips, tempo presets, count-in, wait mode, per-change judging, results) ·
  approach rings · busy-hands input · spectator view on the Mac + demo flags + SIMULATED INPUT fallback · logging and session
  recorder · the 3 songs.
- **SHOULD:** lyric strip (chords above words) · alignment helpers · strum timing feedback + latency calibration · picking
  judging · change drill · E and A shapes.
- **COULD:** LLM coach card · ChordPro import · full IK hand for any chord · strum direction from audio · metronome clicks.
- **WON'T (this weekend):** transcribing songs, copyrighted lyrics or chord sheets, barre chords, Android/XREAL SDK features,
  webcams, voice commands.

## Success metrics (shown to judges only from the frozen eval set)

| Metric | Target |
|---|---|
| Pluck check: correct verdict per string | ≥ 90% in a quiet room |
| Strum check: correct chord-level verdict | ≥ 90% quiet; false "clean" ≤ 5% |
| Strum → verdict latency | ≤ 350 ms |
| Neck alignment | nut within 2 cm for 60 s with ±30° head turns |
| Stereo render | ≥ 60 fps at 3840×1080 |
| Demo | a judge plays a clean Em within 60 s |

## Open questions

See `DECISIONS.md` and `MASTER_PLAN.md` Part III. Blocking now: Q-TEAM-07 (P2's name, laptop, .NET SDK), Q-TEAM-02 (who records),
Q-SKILL-01 (superpowers precedence).
