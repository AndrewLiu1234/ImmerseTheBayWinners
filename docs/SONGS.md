# Songs

*Decided Sat 2026-11-14 ~00:00 PT. Playing matters more than singing: lyrics are kept where we can legally show them, but
they are optional (SHOULD), not a fixed requirement.*

Three popular songs with simple chords. Together they cover the five MUST chords for the ghost hands: **G, C, D, Em, Am**.

| Song | File | Content we ship | Chords | Time | Strum (level 1) | Why |
|---|---|---|---|---|---|---|
| Happy Birthday to You | `happy-birthday.song.json` | chords **and lyrics** (public domain) | G, D, C | 3/4 · 90 bpm | `D - D - D -` | everyone knows it; shows the lyric strip; great live demo |
| Riptide (Vance Joy) | `riptide-loop.song.json` | **chords only** | Am, G, C | 4/4 · 100 bpm | `D - D U - U D U` | the most-requested beginner song; one loop all the way through |
| Stand By Me (Ben E. King) | `stand-by-me-loop.song.json` | **chords only** | G, Em, C, D | 4/4 · 80 bpm | `D - D U - U D U` | the classic I–vi–IV–V; Em practice |

Files live in `unity/Assets/StreamingAssets/songs/`. Tempo presets (50–100%) and wait mode make every song beginner-safe.

## The charts

**Happy Birthday to You** (public domain; G; 3/4; starts on beat 3 with "Hap-py")
```
G                  D
Happy birthday to you
D                  G
Happy birthday to you
G                    C
Happy birthday dear friend
G              D  G
Happy birthday to you
```

**Riptide (chord loop)**: chords only, no lyrics
```
Intro  | Am  G | C   C |  ×2
Loop   | Am  G | C   C |  ×8      (the verse and chorus use this same loop)
```
The recording uses capo 1. We practice without a capo, so you sound a half step lower than the record. The bridge adds an F
chord; it isn't in the app yet (the beginner shape would be Fmaj7, `x x 3 2 1 0`).

**Stand By Me (chord loop)**: chords only, no lyrics, moved from A to G
```
| G | G | Em | Em | C | D | G | G |  ×2
```

## Content rules (enforced by `.claude/rules/songs-content.md` and the song validator)

| `license` | Allowed content | Required fields |
|---|---|---|
| `original` | anything we wrote | — |
| `public-domain` | chords and lyrics | `source` (a URL showing it's public domain) |
| `chords-only` | the chord progression of a copyrighted song: chord names and beat counts **only**. Every `lyric` must be `""`. No melody, tab or audio | `credit` (song, artist, writers, year) |
| `user-provided` | whatever the user imports locally | never committed |

We never paste lyrics or chord sheets from Ultimate Guitar or any other site. A chord progression on its own (common chords in
a common order) is generally not treated as protected, so `chords-only` charts carry just the progression plus credit.
*Not legal advice. If you publish this beyond the hackathon, check with someone qualified.*

## Schema notes (song JSON v1)

- `timeSignature`: `[3, 4]` or `[4, 4]`. `pickupBeats` (optional): beats before the first full bar (Happy Birthday = 1).
- `strum.slots` / `pick.slots`: one entry per subdivision. `D` down, `U` up, `-` rest. In `pick`, `B` = the chord's bass
  string, digits = string numbers (**1 = high E … 6 = low E**).
- Each chord has `beats`. When a line has a lyric, each chord also has `at`: the character index in the lyric where the chord
  name is drawn (Ultimate Guitar style, chords above the words).
- Chord names must exist in `Core/Theory/ChordLibrary` (G, C, D, Em, Am now; E, A, Dm later).

## Adding a song

`/add-song <file>` converts an original or public-domain ChordPro/text file, or builds a `chords-only` chart from a chord
list you type, then runs `./scripts/verify.sh` (AllSongsValid) and asks `music-teacher` to review it.

## Sources

- Happy Birthday is public domain in the US after the 2015 ruling and 2016 settlement:
  [ABC7](https://abc7.com/happy-birthday-song-licensing-agreement-public-domain-copyright/1403454/),
  [NBC Los Angeles](https://www.nbclosangeles.com/news/national-international/happy-birthday-lawsuit-settlement-public-domain/116048/?amp=1)
- Riptide chord loop (Am, G, C; F in the bridge; capo 1 on the record):
  [ukulelego](https://ukulelego.com/?p=3710), [pickuptheguitar](https://pickuptheguitar.com/riptide-chords-no-capo)
