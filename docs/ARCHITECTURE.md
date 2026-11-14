# ARCHITECTURE (seed — the `architect` agent finalizes it in Stage 2; changes go under "Deltas")

## Data flow

```
 MacBook mic or glasses mic                     glasses IMU ─USB HID─▶ tools/imu-bridge (Python, hidapi)
        │ 48 kHz float (Unity Microphone)                                     │ UDP 127.0.0.1:47800 {t, q}
 Runtime/Audio/MicCapture ─ ring buffer ─▶ AudioWorker (thread)        Runtime/Head/ImuHeadPose ─▶ Runtime/Neck/NeckAligner
                                              │ 512-sample hops / onset windows       (4 glances → Core/Geometry/NeckSolver)
                          Core/Audio: Onset · YIN · PluckVerifier · StrumVerifier       │ neck pose (world, 3DoF)
                                              │ AudioEvents (ConcurrentQueue)          │
 Runtime/Flags ─────────▶ Core/Practice: LearnChord · PlayAlong(wait) · PatternJudge · Scoring · Tips
                                              │ PracticeEvents            ▲ Core/Songs: Song · Timeline · SongValidator
                                              ▼                           │
 Runtime/Render/StereoRig (SBS 3840×1080 → display 2 = glasses) · Hud · GhostHand (fretting + strumming) · ApproachRings
 Runtime/Spectator (display 1 = Mac screen) · UGALog / SessionRecorder
```

## Modules

- **Core** (pure C#, netstandard2.1, C# 9, deterministic; built and tested outside Unity via `core-build/` + `core-tests/`)
  - `Contracts/` (architect-owned)
  - `Theory/`: `Midi`, `Tuning`, `ChordShape`, `ChordLibrary` (G C D Em Am; E A Dm later), `FretboardGeometry` (neck-local mm)
  - `Geometry/`: `NeckSolver` (4 gaze rays + known fret distances → neck pose + residual mm), `System.Numerics`
  - `Songs/`: `Song`, `SongParser` (JSON v1), `Timeline` (time signatures, `pickupBeats`, tempo presets), `SongValidator`
  - `Audio/`: `AudioConfig`, `Fft`, `YinPitchTracker`, `SpectralFluxOnset`, `HarmonicSalience`, `PluckVerifier`, `StrumVerifier`, `NoiseFloor`, `Tuner`
  - `Practice/`: `LearnChordSession`, `PlayAlongSession`, `ChangeDrillSession`, `PatternJudge`, `Scoring`, `Tips`
- **Runtime** (Unity 2021.3.3f1, macOS standalone, Built-in render pipeline)
  - `Bootstrap` (composes the scene from code) · `Render/StereoRig` (two eye cameras, IPD flag, 3840×1080 SBS to display 2; 2D fallback)
  - `Head/ImuHeadPose` (UDP receiver, short prediction, 250 ms stale → headLocked)
  - `Neck/NeckAligner` (4 glances, re-anchor) · `Neck/FloatingNeck` (fallback)
  - `GhostHand/`: `FrettingGhost`, `StrummingGhost`, `GhostPoseLibrary` (pose JSON in `StreamingAssets/poses/`), `FingertipSnap` (last-joint IK)
  - `ApproachRings`, `Hud` (neck HUD, lyric strip, pattern strip, toasts, mic meter)
  - `Audio/`: `MicCapture` (device by flag), `WavReplaySource`, `AudioWorker` · `Spectator` · `Flags` (hot reload) · `Logging`

## Threading and budgets

Capture → ring buffer → worker thread (512-sample hops, onset windows) → `ConcurrentQueue` → main thread. Core practice
sessions and all Unity APIs run on the main thread. IMU UDP on its own thread → latest-value slot.
Budgets: ≥60 fps stereo at 3840×1080; DSP < 1 ms per hop; strum verification < 5 ms per onset; no per-frame allocations.

## Contract sketch

```csharp
namespace UntitledGuitarApp.Core.Contracts {
  public interface IAudioSource { int SampleRate { get; } int Read(float[] dst, int offset, int count); }
  public enum Verdict { Correct, OpenStringInstead, WrongFretLow, WrongFretHigh, WrongString, Muted, ExtraString, Unclear }
  public sealed class StringResult { public int String; public int ExpectedMidi; public double DetectedHz; public double Cents; public Verdict Verdict; public string TipKey; }
  public sealed class StrumResult  { public string ExpectedChord; public string BestMatch; public double Confidence; public bool Clean; public StringResult[] Strings; public double OnsetSec; }
  public interface IPluckVerifier { StringResult Verify(float[] window, int sampleRate, int stringIndex, int expectedMidi); }
  public interface IStrumVerifier { StrumResult Verify(float[] window, int sampleRate, ChordShape expected, ChordShape[] confusers); }
  public interface IPracticeSession { string Mode { get; } void OnAudio(AudioEvent e); void Tick(double nowSec); event System.Action<PracticeEvent> Emitted; }
}
```
PracticeEvents the ghost hands consume: `ChordTarget(current, next, beatsToChange)`, `ActiveFinger(n)`, `PatternSlot(index, D|U|string)`, `StringVerdict(...)`.

## Song JSON v1

See `SONGS.md` for the rules. Fields: `schema`, `id`, `title`, `artist?`, `license` (`original` | `public-domain` + `source` |
`chords-only` + `credit`, all lyrics empty | `user-provided`, never committed), `key`, `tempoBpm`, `timeSignature`,
`pickupBeats?`, `notes?`, `strum{subdivision, slots}`, `pick{subdivision, slots}`, `sections[{name, lines[{lyric,
chords[{chord, beats, at?}]}]}]`. String numbers: 1 = high E … 6 = low E; `B` = the chord's bass string.

## Flags (excerpt)

```json
{ "anchorMode": "aligned", "audioSource": "mic", "audio": { "device": "MacBook", "noiseGateDb": 12, "pluckCentsTol": 45, "strumMinMargin": 0.15, "latencyOffsetMs": 0 },
  "ghost": { "color": "#4FC3F7", "fill": 0.2, "fingerColors": false, "fadeAfterSuccess": 0.4 },
  "waitModeDefault": true, "tempoPresets": [0.5, 0.6, 0.75, 0.9, 1.0], "leftHanded": false, "debugOverlay": false, "coach": "off" }
```
`audioSource: "wav"` shows a permanent **SIMULATED INPUT** badge.

## Deltas
- Sat 12:30 · architect for Person 2 (proposal, branch contracts/v1, needs Person 1 + Person 2 OK) · **Contracts v1** = compilable `Core/Contracts/*.cs` from the sketch, with these changes: results are immutable (readonly fields + ctor); `IPluckVerifier`/`IStrumVerifier` take `onsetIndex` (window includes pre-roll) and a 1-based `stringNumber`, confusers as `IReadOnlyList<ChordShape>`; `StrumResult` gains `Margin` and loses `OnsetSec` (time rides on `AudioEvent.TimeSec`); `Verdict` gains `ShortRing` (appended); new `IAudioAnalyzer` (whole Core audio pipeline behind `Reset(startSec)` + `Process(samples, offset, count, AudioExpectation, Action<AudioEvent>)`, so Runtime's AudioWorker only moves samples); new `AudioExpectation`/`ListenMode` (what to listen for, swapped by reference to the worker); `AudioEvent` is a readonly struct (`Onset|Tuner|Pluck|Strum|NoiseFloor`) with a `TunerReading` struct payload; `IPracticeSession` uses `PracticeMode` enum (not string) and gains `OnCommand(Start|Pause|Resume|Skip|Restart, nowSec)`; PracticeEvents = `ChordTarget(current, next, beatsToChange, secondsPerBeat, holding)`, `ActiveFinger(n)`, `PatternSlot(kind, index, Stroke D|U|Pick|Rest, stringNumber, durationSec)`, `ListenFor(expectation)`, `StringVerdict(result, tipText)`, `StrumVerdict(result, tipText, timing, offsetMs)`, `SessionEnded(attempts, clean, onTimeFraction)`. One session clock (seconds, owned by Runtime) for `Tick`, `AudioEvent.TimeSec` and all event times. Bootstrap may `new` Core concrete types (composition root only) and then uses them only through these interfaces.
