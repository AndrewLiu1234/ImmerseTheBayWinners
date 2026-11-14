# Chord data: open datasets first, then 20 minutes of our own recordings

*Researched Sat 2026-11-14 ~00:00 PT (web search plus Kaggle through Claude in Chrome). Owner: Person 2 (Core/audio).*

## What the data is for

| Purpose | Data | Rule |
|---|---|---|
| Build and unit-test the DSP | synthetic Karplus–Strong audio (`core-tests/Synth`) + open datasets | free to tune on |
| Prove accuracy (the numbers we show judges) | **an eval set recorded through our own app, guitar and mic**, frozen Sat afternoon before tuning | never tune on it |
| Demo fallback | a few labelled WAVs in `StreamingAssets/fixtures-demo/` | the HUD shows SIMULATED INPUT |

## Open datasets worth downloading

| Dataset | What it has | License | Size | Use it for |
|---|---|---|---|---|
| **GuitarSet** (NYU + QMUL) | ~3 h of acoustic guitar, 360 excerpts, 6 players; a studio mic recording **and** a hexaphonic pickup (one channel per string); chords, per-string notes, beats | CC BY 4.0 | `audio_mono-mic.zip` 657 MB + `annotation.zip` 39 MB | chord-level eval on real strumming ("comp" excerpts), onset and pitch tests; per-string truth from the hex pickup |
| **GUITAR CHORDS V3** (Kaggle, Fabiana Vinci) | chords Am, Bb, Bdim, C, Dm, Em, F, G, strummed 4 ways; 20 real guitars + 35 virtual ones | CC BY-SA 4.0 | 765 MB, 1,760 files | strum-check eval for **Am, C, Em, G** across many guitars (no D) |
| **Guitar chords database** (Osmalskyj et al., Univ. of Liège) | 2,000 chords in 10 classes, recorded in an anechoic room and in noisy rooms; 16-bit mono 44.1 kHz WAV | none stated | small | noise robustness; **local eval only, never commit or redistribute** |
| **IDMT-SMT-Guitar** (Fraunhofer) | 4 subsets; subset 4 = 64 short pieces at 2 tempi on 3 guitars with chord and onset labels | CC BY-NC-ND 4.0 (non-commercial) | 1.3 GB | optional extra eval; **don't commit** |
| **NSynth** (Google Magenta) | single notes from 1,006 instruments incl. acoustic guitar, 4 s at 16 kHz | CC BY 4.0 | use the small test split | YIN pitch tests across the guitar range |

**Chord shapes (fingering data):** `chords-db` (MIT, JSON with frets and fingers) and the UCI "Guitar Chords finger
positions" set (2,633 chords). `music-teacher` cross-checks `ChordLibrary` and the ghost-hand fingertip targets against them.

**Reading only:** *Joint Transcription of Acoustic Guitar Strumming Directions and Chords* (2025) detects strum direction
from microphone audio, which informs the COULD item "strum direction from audio".

Everything downloaded goes in `data/` (gitignored). Only our own small fixtures are committed, in `core-tests/Fixtures/audio/`.
`./scripts/get-datasets.sh` fetches GuitarSet and NSynth-test, and GUITAR CHORDS V3 if you have a Kaggle API token. Credit
every dataset we use in `docs/CREDITS.md`.

## Why we still record ~20 minutes ourselves

Datasets can't give us:
1. **Our guitar, our mic, our room**, through **our capture path** (the glasses mic or MacBook mic, macOS processing, latency).
2. **The mistakes beginners make**: a muted B string on G, a missing finger, a strummed low E on C. Datasets are mostly clean
   playing, but these mistakes are exactly what the pluck check must catch.
3. An honest eval set that wasn't used for tuning.

Nobody needs to play well. The shapes just have to be held as labelled; slowly is fine.

## Recording protocol (30–40 min; `/record-fixtures` walks you through it; Person 2 runs it, Person 1 can be the hands)

**Setup:** tune the guitar · quiet corner · MacBook on the table with its mic 40–60 cm from the soundhole (and/or the glasses
mic, whichever M-S3 picked) · record through the app's session recorder, or with QuickTime/Audacity as 48 kHz mono WAV.

**For each chord: G, C, D, Em, Am**
1. 3 clean strums, letting each ring ~3 s.
2. 1 slow pluck pass, string by string from low to high.
3. 3 strums with **one deliberate fault**, and then 1 pluck pass with the same fault:
   - G: B string muted · high E open (no finger 3)
   - C: low E strummed · G string muted
   - D: low E and A strummed · high E open
   - Em: finger 2 missing (A string open)
   - Am: B string muted (finger 1 flat)

**Plus:** open strings 6→1 twice (tuner) · G→C and Am→G changes, 5 each · 30 s room noise · 30 s talking nearby.

**Files and labels:** `core-tests/Fixtures/audio/<group>/<label>__<take>.wav`, e.g. `chords/G_fault-B-muted__02.wav`, plus
one row per file in `labels.csv`: `file,group,chord,string,expected_midi,label,take,source,env`. Fixtures are hook-protected:
humans add them; agents never edit them.

**Two sessions:** Friday night or Sat 08:30 = **dev clips** (tune freely). Saturday afternoon = **eval set**, recorded the same way through
the app and frozen before any tuning. Only eval-set numbers go on slides.

## Sources

- [GuitarSet on Zenodo (CC BY 4.0, file sizes)](https://zenodo.org/record/3371780)
- [GUITAR CHORDS V3 on Kaggle (CC BY-SA 4.0)](https://www.kaggle.com/datasets/fabianavinci/guitar-chords-v3)
- [Osmalskyj et al. chords database](https://people.montefiore.uliege.be/josmalskyj/research.php)
- [IDMT-SMT-Guitar on Zenodo (CC BY-NC-ND 4.0)](https://zenodo.org/records/7544110)
- [NSynth (CC BY 4.0)](https://magenta.tensorflow.org/datasets/nsynth)
- [chords-db (MIT)](https://github.com/tombatossals/chords-db) · [UCI Guitar Chords finger positions](https://archive.ics.uci.edu/dataset/575)
- [Strumming directions and chords from audio (arXiv 2508.07973)](https://arxiv.org/html/2508.07973v1)
