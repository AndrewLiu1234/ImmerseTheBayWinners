# core-tests/ — fast tests for pure Core (no Unity)
- Core compiles as netstandard2.1 via ../core-build; tests run on net8.0 with xUnit: `dotnet test core-tests -v q`.
- Categories ([Trait("Category", ...)]): default = Unit; "Synthetic" = Karplus–Strong audio; "AudioEval" = recorded fixtures
  (slower; run via ./scripts/audio-eval.sh; prints a confusion matrix; fails if a metric drops >3 points vs the ```json block in
  docs/AUDIO_BASELINE.md; with no baseline yet it only reports and passes).
- Fixtures/audio/**.wav + Fixtures/audio/labels.csv are immutable (hook-protected). Humans add recordings via /record-fixtures.
- Synth/KarplusStrong.cs: Pluck(midi, ms), Strum(ChordShape, mistakes, spreadMs, snrDb). Write the failing test with it first.
- Geometry/NeckSolverTests: synthetic head pivot + known neck pose → rays (+ optional angular noise) → solver must recover the pose.
- AllSongsValid loads every StreamingAssets/songs/*.json (linked into the output as Songs/).
- Test names: Method_Scenario_Expected. One behavior per test. No sleeps, no randomness without a seed.
