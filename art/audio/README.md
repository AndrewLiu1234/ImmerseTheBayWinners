# art/audio: the demo-fallback recording

`unity/Assets/StreamingAssets/fixtures-demo/learn-g.wav` is the file every flag profile names as `wavReplayFile`
(`audioSource: "wav"`, e.g. `tools/flags/safe.json`). The app replays it through the same ring buffer as the microphone and
the HUD shows SIMULATED INPUT.

**It is SYNTHETIC, not a recording of a guitar.** `make_learn_g.py` builds it from six Karplus-Strong plucked strings tuned
to an open G chord (frets 3 2 0 0 0 3; strings 6..1 = MIDI 43 47 50 55 59 67 = G2 B2 D3 G3 B3 G4, A4 = 440 Hz), five
down-strums 2 s apart starting at 0.3 s (string 6 first, 14 ms between strings), 48 kHz mono 16-bit PCM, 10.0 s, peak
-10 dBFS (RMS about -32 dBFS over the file, -26 dBFS during a strum), the last 0.25 s faded out so the loop has no click.
It is seeded and uses only the Python standard library, so a rerun writes the same bytes.

Regenerate (from the repo root):

```bash
python3 art/audio/make_learn_g.py           # rewrites the WAV in StreamingAssets
python3 art/audio/make_learn_g.py --check   # prints the numbers only
```

Checked on 2026-11-14 with numpy (not needed by the script): every string's partial is within 0.5 cent of its target
(98.01, 123.46, 146.85, 196.01, 246.92, 391.89 Hz), the five strums have RMS 0.049-0.054, the first 0.29 s is silent.
`ReplayFixtureTests` (Tests/EditMode/HardeningTests.cs) checks the header, length and level of the shipped file and that
every `wavReplayFile` named in `StreamingAssets/flags.default.json` and `tools/flags/*.json` exists.

Not for judging the audio checks: a synthetic pluck is cleaner than a real guitar. Record a real take (same name, 48 kHz
mono 16-bit, labelled in this file) when there is time.
