# DEMO — live script, judge protocol, 30 s video, Devpost, hostile questions

*Owner: Person 2 (text) · Person 1 drives the glasses. Drafted Sat 2026-11-14 15:45 PT. Freeze Sun 01:00 PT · Devpost due Sun 09:00 PT.*

> **Status banner (Sat 15:45).** Nothing here is verified to L5 yet. Core is at L1 (408 tests, synthetic) plus L2 on real tuner clips.
> Runtime (stereo, IMU, ghost hands, HUD, spectator) has no device check yet. **Every live beat depends on a gate (§0).** If its gate
> is unticked at freeze, use that beat's fallback. `[VERIFY ON DEVICE]` = a human must check it on the glasses before we say it.
> `[PLACEHOLDER]` = fill from the frozen venue eval set (`docs/AUDIO_BASELINE.md`, still empty). Don't quote a placeholder you haven't filled.

## 0. Go / no-go gates (tick at the Sun 00:30 rehearsal)

| # | Claim the script makes | Check that proves it (L5 = on the glasses, a human watching) | Owner | Status |
|---|---|---|---|---|
| G-1 | Stereo picture fuses on the glasses, ≥60 fps | SL0 device box; fps = `[FPS]` | Person 1 | ☐ |
| G-2 | 4 glances put the neck on the real neck, and it holds | SL8 device box: nut within 2 cm for 60 s, ±30° head turns; measured `[NUT_ERR_CM]` | Person 1 | ☐ → else headLocked all demo |
| G-3 | Fretting ghost hand for G, Em, C, D is copyable without a diagram | SL4 device box | Person 1 | ☐ → else capsule hand |
| G-4 | Strumming ghost hand keeps time at 75% | SL4 device box | Person 1 | ☐ |
| G-5 | Pluck check flags a muted B string on G | SL2 device box: 4 of 5 tries | Person 1 · Person 2 | ☐ |
| G-6 | Strum check judges chords right | SL3 device box: ≥18 of 20 | Person 1 · Person 2 | ☐ |
| G-7 | Happy Birthday at 75% with wait mode completes | SL5 device box | Person 1 · Person 2 | ☐ |
| G-8 | Spectator view on the Mac screen, labelled "glasses view (virtual layer)" | SL9 | Person 1 | ☐ |
| G-9 | SIMULATED INPUT works and the badge shows on the HUD **and** spectator | SL9 + a recorded `fixtures-demo/learn-g.wav` (**the file doesn't exist yet**) | Person 1 · recorder | ☐ |
| G-10 | Flags hot-reload in ≤1 s (`./scripts/use-flags.sh …`) | SL9 | Person 1 | ☐ |

**If time** (say nothing about these unless they pass L5): lyric strip (SHOULD) · picking judging · strum timing feedback ·
change drill counter · AI coach card (COULD, `coach: "off"` in every demo profile) · E/A shapes · left-handed mirror.

## 1. Live script: 2:00 core + 1:00 extension (3:00 total)

Roles: **Wearer = Person 1** (plays and drives) · **Narrator = Person 2**. The spectator view (Mac screen) shows **only the virtual
layer** (neck, ghost hands, HUD on black). It never shows the real guitar, since we have no camera. Judges look at the real guitar directly.

| Time | Wearer does | Mirror (spectator) shows | We say | Fallback |
|---|---|---|---|---|
| 0:00–0:15 | Sits, guitar on knee, glasses on | Title + "glasses view (virtual layer)" label; tuner needle steady | "Most beginners quit in the first weeks. They look away to a chart, and they can't tell *which* string is wrong. Your hands are busy and your eyes are on the neck, so this has to be glasses." | Unity down → relaunch the build (≤30 s) while we talk; else play the 30 s video |
| 0:15–0:25 | Looks at 4 points: nut low-E, nut high-E, fret 5, fret 12 (≈1 s each) | Reticle; 4 dwell ticks; virtual neck snaps into place `[VERIFY ON DEVICE]` | "No cameras on this setup. Four glances, and the glasses' motion sensor holds the neck in place." | **IMU bridge down** → app goes headLocked by itself after 250 ms of no packets. Say: "The head sensor dropped, so it's in floating-neck mode. Same lesson, the neck floats in front of you." Skip the glances |
| 0:25–0:45 | Learn **G**: copies the glowing blue fretting hand, finger by finger | Ghost hand forms G; active finger bright with its number, others dim; each plucked note confirmed `[VERIFY ON DEVICE]` | "The blue hand shows *how* the hand gets there: thumb, wrist, curled fingertips. The dots are the exact spots." | Blender poses fail → capsule hand from the dots (still a hand) |
| 0:45–1:05 | **Deliberate mistake:** lets finger 3 lean on the open B string, plucks string by string → fixes → plucks again → strums | String 2 ✕ and the tip **"String 2 muted — arch finger 3"** → ✓ on all six → strum ✓; hand fades to 40% `[VERIFY ON DEVICE]` | "It knows the chord you're aiming for, so it checks each string against it. That's how it can name the finger." | **Mic fails** → `./scripts/use-flags.sh demo-wav` (≤10 s): replays our recorded G clip, neck stays aligned (use `safe` if the IMU is also down). **SIMULATED INPUT** must show on the HUD and spectator. Say: "The mic dropped, so this is a recorded clip of the same mistake, and the badge says so." |
| 1:05–1:40 | Play-along **Happy Birthday** at 75% (G, D, C, 3/4): count-in, plays; misses one change on purpose | Strumming hand + pattern strip in time; fretting hand morphs a beat early; approach rings shrink onto the next spots; **wait mode** holds on the missed change, then moves on when it hears it `[VERIFY ON DEVICE]` · lyric strip **if time** | "Your own speed. Miss a change and it waits for you. It gives up gracefully, so you're never stuck." | Strum check flaky in the noise → keep going at 50% and narrate. Mic gone → `demo-wav` profile with the badge. Freeze → space bar to advance |
| 1:40–2:00 | Takes the glasses off, hands them over | Results screen `[VERIFY ON DEVICE]` | "It verifies what you play against the chord you're aiming for. Tuner: 18 of 18 real open-string clips within 10 cents. Pluck check: `[PLUCK_ACC_VENUE]` in this room. Want your first chord in 60 seconds?" | Placeholders not filled → say only the tuner number and "the real-world eval set isn't frozen yet" |
| **Extension** 2:00–2:20 | Stand By Me loop (G, Em, C, D) at 75%: G→Em change | Morph G→Em; approach rings; per-change ✓ `[VERIFY ON DEVICE]` | "Three songs: Happy Birthday is public domain, the other two are chord loops only. No lyrics, no tabs." | Skip; go straight to results |
| 2:20–2:40 | Plays to the end | Results: changes on time / late / missed `[VERIFY ON DEVICE]` | "Every change gets judged. You see exactly which one to drill." | Describe the results from the log |
| 2:40–3:00 | — | Terminal: `./scripts/verify.sh` green; TODO slices with "Verified to L_" lines | "Claude wrote most of the code inside a harness we built: two lanes, hooks, 408 Core tests, and an eval on our own recordings. Nothing counts as done until it's checked on the glasses." | — |

**Fallback switches, each rehearsed in under 10 s:**

| Failure | Detect | Switch | HUD must show |
|---|---|---|---|
| IMU bridge down | neck swims / bridge terminal stops | automatic headLocked (no action needed); optional restart of `./scripts/imu-bridge.sh` between judges | floating neck |
| Mic dead / room too loud, IMU fine | no meter movement, or 2 bogus verdicts in a row | `./scripts/use-flags.sh demo-wav` (keeps the aligned neck, replays the WAV) | **SIMULATED INPUT** badge (HUD + spectator) |
| Mic dead AND IMU down | both of the above | `./scripts/use-flags.sh safe` (headLocked + WAV) | **SIMULATED INPUT** badge + floating neck |
| Ghost poses broken | hand mangled | capsule-hand fallback (Person 1) | still a hand |
| Unity hang | frozen frame | quit → relaunch `build/UntitledGuitarApp.app` on display 2 | — |
| Everything | — | play the 30 s video on the Mac | — |

**5 riskiest moments:**
1. **0:45 deliberate mistake.** The pluck check has no real fault clips yet, and G-5 is unchecked. Fallback: `safe` profile with the badge.
2. **0:15 alignment.** IMU on the Ultra is unproven (M-S2), and the solver understates nut error about 5×. Fallback: headLocked, and say so.
3. **1:05 play-along in venue noise** (R3). Fallback: 50% tempo; narrate; the pluck check carries the accuracy story.
4. **1:40 numbers.** It's easy to quote a synthetic number as a real one. Fallback: say only the tuner number.
5. **Judge hand-off.** Fit, prescription glasses, or a judge who can't fret yet. Fallback: the wearer plays while the judge watches the spectator.

**Pre-show (2 min, before every judge group):** tune all 6 strings (needle) · `./scripts/use-flags.sh demo` · bridge running ·
`./scripts/logs.sh` in a terminal · glasses brightness/dimming set for the room · spectator label visible · strum once and watch the meter.

## 2. "First chord in 60 s" (judge try-it, Em)

**Reset (≤30 s):** wipe the nose pad · `./scripts/use-flags.sh judge` (Em only, headLocked floating neck, wait mode, large text,
tempo 50/75%) · strum open strings once to check tuning.

| T | Judge | We do / say |
|---|---|---|
| −10 s | — | "Do you wear glasses? Ever played?" If the glasses don't fit, the wearer plays and the judge watches the spectator |
| 0–10 s | Puts the glasses on, takes the guitar | We steady the guitar body. "See the blue hand? Copy it." |
| 10–35 s | Places finger 2 on string 5, fret 2, then finger 3 on string 4, fret 2 | Ghost hand finger by finger; each note confirmed `[VERIFY ON DEVICE]`. Coach only from the tip on the HUD |
| 35–50 s | Strums all six strings | Strum check ✓, or one tip ("Finger 3: press with fingertip"); one retry |
| 50–60 s | ✓ | "That's your first chord. Em." Note pass or fail on a tally (`[JUDGE_FIRST_CHORD_RATE]`) |

Rules: **never run a judge on SIMULATED INPUT.** If the mic is down, say so and show only the ghost hand. Never correct the
app's verdict out loud. If it's wrong, say "it got that one wrong."

## 3. Demo video: 30 s cut (one moment: ghost hands → strum → verdict)

Format: side by side. **Left** = phone over the shoulder (real guitar, real hands, live audio). **Right** = QuickTime recording
of the spectator view, labelled. Optional inset: a phone filming **through the lens**, labelled "through the lens (phone, approximate)".
**No faked overlays:** never composite the virtual layer onto the phone footage. Only real audio. If a take used SIMULATED INPUT,
the badge must be visible and the caption must say so (better: re-shoot live).

| Time | Left (phone) | Right (spectator) | Caption |
|---|---|---|---|
| 0:00–0:03 | Wearer with glasses + guitar; USB-C cable to the MacBook in frame | Title card | "XREAL Air 2 Ultra + MacBook. One cable. No cameras." |
| 0:03–0:11 | Real fretting hand settles into G | Blue fretting ghost hand forms G; dots bright | "Ghost hand: how to hold the chord, on your neck" |
| 0:11–0:16 | Finger 3 leans on the B string; plucks | String 2 ✕, "String 2 muted — arch finger 3" | "It hears which string is wrong" |
| 0:16–0:23 | Arches finger 3, strums | Strumming ghost hand sweeps; all six ✓, strum ✓ | "Fix it. Strum. ✓" |
| 0:23–0:27 | Close-up: chord rings out | Hand fades to 40%, next chord (C) appears | "Then play real songs at your own speed" |
| 0:27–0:30 | — | End card: name, tagline, repo URL | "Untitled Guitar App · `[REPO_URL]`" |

Schedule: rough cut after G-3 and G-5 pass · final by Sun 06:00 · upload and link `[VIDEO_URL]`.

## 4. Devpost draft

**Title:** Untitled Guitar App
**Tagline:** A glowing ghost hand on your real guitar neck, and an ear that tells you which string is wrong.
**Tracks:** `[TRACKS]` (pending Q-AI-01/02; candidates: AI Horizons, Game Changer, ManageXR smart-glasses if XREAL qualifies).

*(Story sections below ≈290 words.)*

**Inspiration.** Most beginners quit guitar in the first weeks. Chord charts make you look away from your hands, and nobody
tells you *which* string is buzzing.

**What it does.** Put on XREAL Air 2 Ultra glasses and pick up a real acoustic. Four glances line up a virtual neck with your real
one, without cameras. Glowing translucent blue ghost hands show the fretting shape and the strumming pattern. The app listens:
a pluck check goes string by string, a strum check covers the whole chord, and you get a tip like "String 2 muted — arch finger 3".
Then you play Happy Birthday or the chord loops of Riptide and Stand By Me at 50–100% speed. Wait mode holds until you play the change.

**How we built it.** A MacBook drives the glasses over one USB-C cable. It runs a Unity side-by-side stereo renderer, a
Python IMU bridge, and a solver that fits the measured fretboard to four gaze rays. A pure-C# core handles theory, a YIN tuner,
chord verification against the expected chord, and the practice engine, with 408 tests outside Unity. Claude Code agents wrote
most of the code inside our harness: two lanes, hooks, gates, and an audio eval on our own recordings.

**Challenges.** No camera feed, hand tracking or 6DoF on this setup. An additive display, where black is invisible. Venue noise.

**Accomplishments.** Ghost hands on a real neck with no camera `[VERIFY ON DEVICE]`. The tuner got 18/18 real open-string clips within
±10 cents. Pluck check `[PLUCK_ACC_VENUE]` and strum check `[STRUM_ACC_VENUE]` on our frozen venue set.

**What we learned.** Verification beats transcription. Knowing the target chord makes listening explainable, string by string.

**What's next.** Bring-your-own chord-sheet import, more chords, latency calibration, and a port of the audio engine to camera-capable headsets.

**Built with:** Unity 2021.3 · C# / .NET · Python · hidapi · macOS · XREAL Air 2 Ultra · Blender `[if used]` · Claude Code · Unity MCP.
**Links:** repo `[REPO_URL]` · video (30 s) `[VIDEO_URL]` · credits and licenses: `docs/CREDITS.md`.

**Image captions (3):**
1. "Glasses view (virtual layer): the blue fretting ghost hand forms G on the aligned neck." *(spectator screenshot)*
2. "String 2 muted — arch finger 3: the pluck check names the string and the finger." *(spectator screenshot with ✕ / ✓)*
3. "One cable, no cameras: XREAL Air 2 Ultra on a MacBook, real guitar, real hands." *(phone photo, over the shoulder)*

## 5. Hostile questions: honest answers (only numbers in the repo)

**Numbers we may quote:**

| Number | Source | Real or synthetic |
|---|---|---|
| Tuner 18/18 open strings within ±10 c, worst 6.3 c | 18 dev clips, quiet room, MacBook mic (STATUS Sat 15:38) | **real**, dev set, not frozen |
| Pluck `[PLUCK_ACC_QUIET]` / `[PLUCK_ACC_VENUE]` · strum `[STRUM_ACC_QUIET]` / `[STRUM_ACC_VENUE]` · false clean `[FALSE_CLEAN_QUIET]` / `[FALSE_CLEAN_VENUE]` · n = `[N_EVAL_CLIPS]` | `docs/AUDIO_BASELINE.md` (empty) | real, once frozen |
| Strum 99.6% / 0.3% false clean · pluck 100% (96.5% at 5 dB SNR) · onset F1 ≥ 0.979 · tuner 0.069 c | unit tests (STATUS Sat 15:10) | **synthetic only, never say "accuracy"** |
| 408 Core tests | STATUS Sat 15:10 | — |

1. **"Isn't this Yousician in glasses?"** Those apps live on a screen you look away to. We put the hand shape on your own neck,
   check each string against the chord you're aiming for, and you never take your hands off the guitar.
2. **"How accurate is it?"** Tuner: 18 of 18 real open-string clips within 10 cents (dev set, quiet room). On our frozen venue set,
   the pluck check gets `[PLUCK_ACC_VENUE]` and the strum check `[STRUM_ACC_VENUE]`, with `[FALSE_CLEAN_VENUE]` mistakes judged clean.
   The pluck check is the definitive test. Muted-string detection from a strum is best effort. *(Placeholders unfilled → "we haven't
   frozen a real-world set yet. The tuner number is the only real one we claim.")*
3. **"Does it really line up with my neck?"** Our target is the nut within 2 cm for 60 s. We measured `[NUT_ERR_CM]` `[VERIFY ON DEVICE]`.
   That's enough for the shape and place of the hand. The exact fret comes from the dots, the diagram and the audio. We don't claim
   millimetres. If the IMU is down, we say so: floating-neck mode.
4. **"Why no camera?"** This glasses-plus-Mac setup gives apps no camera feed, no hand tracking and no 6DoF (those need XREAL's
   Android SDK). So we built alignment from four glances plus the IMU, and we listen instead of looking.
5. **"Where's the AI?"** Real-time machine listening. We test what we hear against the expected chord, string by string, so
   every verdict is explainable. The coach card is optional and off in the demo.
6. **"Latency?"** Target ≤350 ms from strum to verdict. Measured: `[LATENCY_MS]` `[VERIFY ON DEVICE]`. Unfilled → "not measured yet."
7. **"Were the thresholds tuned on the clips you report?"** The tuner number comes from our dev clips, so it's a dev-set number. On the
   first pass both E strings read about 12 cents flat. We retuned the guitar and re-recorded; we didn't loosen the threshold. Pluck and
   strum numbers come only from a set frozen before reporting. If that isn't true at demo time, we call them tuning-set numbers.
8. **"How much did Claude write?"** Most of the code, inside a harness we designed: two lanes, hooks, 408 Core tests and an eval on
   our own recordings. We made the hardware and architecture calls, recorded and labelled the audio, and run every check on the glasses.
9. **"What breaks it?"** Loud rooms (fallback: closer mic, slower tempo, pluck check). A detuned guitar (tuner first). Moving the
   guitar after alignment (one-glance re-anchor). Singing along: we haven't measured it, so it isn't in the demo.
10. **"How can a ghost hand be right without hand tracking?"** It shows shape and place. The fingertip dots give the exact spots,
    and the audio tells us what you actually played, which a camera couldn't hear anyway.
