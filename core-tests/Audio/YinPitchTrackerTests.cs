using System;
using System.Diagnostics;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL1 acceptance (automated, synthetic half): YIN within ±3 cents on E2…E4, octave errors resolved, silence/noise → no pitch.
[Trait("Category", "Synthetic")]
public class YinPitchTrackerTests
{
    const int Sr = 48000;
    readonly ITestOutputHelper _out;
    public YinPitchTrackerTests(ITestOutputHelper output) { _out = output; }

    static YinPitchTracker NewTracker(AudioConfig cfg = null, int sr = Sr) => new YinPitchTracker(cfg ?? AudioConfig.Default, sr);

    // Worst |cents| over several 2048-sample windows of the ring-out (attack skipped: 100 ms … 1.5 s).
    static readonly double[] RingOutWindows = { 0.1, 0.25, 0.5, 1.0, 1.5 };

    static double WorstCents(YinPitchTracker t, float[] x, double expectedHz, int sr, out int misses, double[] windowsSec = null)
    {
        double worst = 0; misses = 0;
        int frame = AudioConfig.Default.FrameSize;
        foreach (double sec in windowsSec ?? RingOutWindows)
        {
            int off = (int)(sec * sr);
            if (off + frame > x.Length) continue;
            var r = t.Process(x, off, frame);
            if (!r.HasPitch) { misses++; continue; }
            worst = Math.Max(worst, Math.Abs(Midi.CentsBetween(expectedHz, r.Hz)));
        }
        return worst;
    }

    [Theory]
    [InlineData(6)] [InlineData(5)] [InlineData(4)] [InlineData(3)] [InlineData(2)] [InlineData(1)]
    public void Process_OpenStringPluck_WithinThreeCents(int stringNumber)
    {
        int midi = Tuning.StandardMidi(stringNumber);
        var x = KarplusStrong.Pluck(midi, 1700, Sr, seed: stringNumber);
        double worst = WorstCents(NewTracker(), x, Midi.ToHz(midi), Sr, out int misses);
        _out.WriteLine($"string {stringNumber} ({Midi.Name(midi)}): max |cents| = {worst:F3}");
        Assert.Equal(0, misses);
        Assert.InRange(worst, 0.0, 3.0);
    }

    [Theory]
    [InlineData(40, 5.0)] [InlineData(40, -5.0)] [InlineData(40, 20.0)] [InlineData(40, -40.0)]
    [InlineData(45, -20.0)] [InlineData(50, 40.0)] [InlineData(55, -5.0)] [InlineData(55, 20.0)]
    [InlineData(59, -40.0)] [InlineData(64, 5.0)] [InlineData(64, -20.0)] [InlineData(64, 40.0)]
    public void Process_DetunedPluck_WithinThreeCents(int midi, double detune)
    {
        var x = KarplusStrong.Pluck(midi, 1700, Sr, seed: midi, detuneCents: detune);
        double hz = Midi.ToHz(midi) * Math.Pow(2.0, detune / 1200.0);
        double worst = WorstCents(NewTracker(), x, hz, Sr, out int misses);
        _out.WriteLine($"{Midi.Name(midi)} {detune:+0;-0} c: max |cents| = {worst:F3}");
        Assert.Equal(0, misses);
        Assert.InRange(worst, 0.0, 3.0);
    }

    [Theory]
    [InlineData(40)] [InlineData(64)]
    public void Process_At44100Hz_WithinThreeCents(int midi)
    {
        var x = KarplusStrong.Pluck(midi, 1700, 44100, seed: 1);
        double worst = WorstCents(NewTracker(sr: 44100), x, Midi.ToHz(midi), 44100, out int misses);
        Assert.Equal(0, misses);
        Assert.InRange(worst, 0.0, 3.0);
    }

    [Theory]
    [InlineData(40)] [InlineData(50)] [InlineData(64)]
    public void Process_PluckWithNoiseAt20dB_WithinThreeCents(int midi)
    {
        // SNR is measured over 0.1–0.45 s, so only windows inside that span are at ≈ 20 dB (the pluck decays ~2 dB/100 ms).
        var x = KarplusStrong.Pluck(midi, 1700, Sr, seed: 5);
        Signals.AddNoise(x, 20.0, seed: 11, from: (int)(0.1 * Sr), to: (int)(0.45 * Sr));
        double worst = WorstCents(NewTracker(), x, Midi.ToHz(midi), Sr, out int misses, new[] { 0.1, 0.2, 0.3, 0.4 });
        Assert.Equal(0, misses);
        Assert.InRange(worst, 0.0, 3.0);
    }

    // Low E whose fundamental and odd partials are weak (−16 dB) next to a dominant 2nd harmonic: plain YIN's first dip
    // under 0.12 is at the E3 period. Harmonic support at the sub-octave must pull it back to E2.
    static readonly double[] WeakFundamentalPartials = { 0.15, 1.0, 0.15, 0.5, 0.08, 0.3 };

    [Fact]
    public void Process_LowEWithDominantSecondHarmonic_ReadsE2NotE3()
    {
        double e2 = Midi.ToHz(40);
        var x = Signals.Harmonic(e2, WeakFundamentalPartials, 500);
        var r = NewTracker().Process(x, 4800, 2048);
        Assert.True(r.HasPitch);
        Assert.InRange(Midi.CentsBetween(e2, r.Hz), -3.0, 3.0);
    }

    [Fact]
    public void Process_LowEWithDominantSecondHarmonic_WithoutOctaveCorrection_ReadsE3()
    {
        // Proves the case above actually exercises the octave correction.
        double e2 = Midi.ToHz(40);
        var x = Signals.Harmonic(e2, WeakFundamentalPartials, 500);
        var r = NewTracker(new AudioConfig(octaveCorrection: false)).Process(x, 4800, 2048);
        Assert.True(r.HasPitch);
        Assert.InRange(Midi.CentsBetween(2 * e2, r.Hz), -3.0, 3.0);
    }

    [Theory]
    [InlineData(52)] [InlineData(57)] [InlineData(62)]
    public void Process_TrueNoteWithRichHarmonics_NotPulledAnOctaveDown(int midi)
    {
        double hz = Midi.ToHz(midi);
        var x = Signals.Harmonic(hz, new[] { 0.6, 1.0, 0.7, 0.5, 0.3, 0.2 }, 500);
        Signals.AddNoise(x, 20.0, seed: 2, from: 0, to: x.Length);
        var r = NewTracker().Process(x, 4800, 2048);
        Assert.True(r.HasPitch);
        Assert.InRange(Midi.CentsBetween(hz, r.Hz), -3.0, 3.0);
    }

    [Fact]
    public void Process_KarplusStrongPlusStrongOctave_ReadsLowE()
    {
        // A low E pluck mixed with a louder E3 pluck sharing its harmonics: the fundamental is still the low E.
        var low = KarplusStrong.Pluck(40, 500, Sr, seed: 1);
        var oct = KarplusStrong.Pluck(52, 500, Sr, seed: 2, amplitude: 1.0f);
        var x = new float[low.Length];
        for (int i = 0; i < x.Length; i++) x[i] = 0.5f * low[i] + oct[i];
        var r = NewTracker().Process(x, 4800, 2048);
        Assert.True(r.HasPitch);
        Assert.InRange(Midi.CentsBetween(Midi.ToHz(40), r.Hz), -3.0, 3.0);
    }

    // The attack: the first frames of a pluck are a decaying noise burst whose spectrum smears into the sub-octave bins.
    // That must not trigger the octave correction (found by probing: B3 at t = 0 read B2 before the d′(2τ) < d′(τ) rule).
    [Theory]
    [InlineData(40)] [InlineData(45)] [InlineData(50)] [InlineData(55)] [InlineData(59)] [InlineData(64)]
    public void Process_AttackWindows_NeverAnOctaveOff(int midi)
    {
        var t = NewTracker();
        foreach (int seed in new[] { 1, 4, 9 })
        {
            var x = KarplusStrong.Pluck(midi, 300, Sr, seed: seed);
            for (int off = 0; off <= 960; off += 240)
            {
                var r = t.Process(x, off, 2048);
                if (!r.HasPitch) continue;  // "no pitch yet" is fine during the attack; a wrong octave is not
                Assert.InRange(Midi.CentsBetween(Midi.ToHz(midi), r.Hz), -50.0, 50.0);
            }
        }
    }

    [Fact]
    public void Process_OctaveCorrected_KeepsUncorrectedCandidate()
    {
        double e2 = Midi.ToHz(40);
        var r = NewTracker().Process(Signals.Harmonic(e2, WeakFundamentalPartials, 500), 4800, 2048);
        Assert.InRange(Midi.CentsBetween(e2, r.Hz), -3.0, 3.0);
        Assert.InRange(Midi.CentsBetween(2 * e2, r.UncorrectedHz), -3.0, 3.0);
    }

    [Fact]
    public void Process_NoCorrectionNeeded_UncorrectedEqualsHz()
    {
        var r = NewTracker().Process(KarplusStrong.Pluck(55, 300), 4800, 2048);
        Assert.Equal(r.Hz, r.UncorrectedHz);
    }

    // Regression sweep: every semitone E2…E5, two seeds, windows from the attack (t = 0) to 0.6 s. Found by probing:
    // before the sub-multiple and dip-ratio rules, low-SNR frames read 1–2 octaves low (−1200/−1902/−2400 cents).
    [Theory]
    [InlineData(999.0)] [InlineData(20.0)] [InlineData(10.0)]
    public void Process_SemitoneSweep_NoOctaveErrors(double snrDb)
    {
        var t = NewTracker();
        int pitched = 0, gross = 0;
        for (int midi = 40; midi <= 76; midi++)
            foreach (int seed in new[] { 1, 2 })
            {
                var x = KarplusStrong.Pluck(midi, 900, Sr, seed: seed);
                if (snrDb < 999) Signals.AddNoise(x, snrDb, seed + 100, 0, (int)(0.3 * Sr));
                for (int off = 0; off <= 28800; off += 2400)
                {
                    var r = t.Process(x, off, 2048);
                    if (!r.HasPitch) continue;
                    pitched++;
                    if (Math.Abs(Midi.CentsBetween(Midi.ToHz(midi), r.Hz)) > 50) gross++;
                }
            }
        _out.WriteLine($"snr {snrDb} dB: pitched {pitched} frames, octave/gross errors {gross}");
        Assert.Equal(0, gross);
    }

    // Fine accuracy holds while the frame's own SNR is ≥ 15 dB (white noise; probe: worst 2.14 cents over 2,759 frames).
    [Theory]
    [InlineData(40)] [InlineData(47)] [InlineData(55)] [InlineData(64)] [InlineData(71)]
    public void Process_LocalSnr15dB_WithinThreeCents(int midi)
    {
        var t = NewTracker();
        foreach (int seed in new[] { 1, 2, 3 })
            foreach (double sec in new[] { 0.1, 0.3, 0.5 })
            {
                var x = KarplusStrong.Pluck(midi, 900, Sr, seed: seed);
                int off = (int)(sec * Sr);
                Signals.AddNoise(x, 15.0, seed + 7, off, off + 2048);  // SNR measured on this very window
                var r = t.Process(x, off, 2048);
                Assert.True(r.HasPitch);
                Assert.InRange(Midi.CentsBetween(Midi.ToHz(midi), r.Hz), -3.0, 3.0);
            }
    }

    [Fact]
    public void Process_Silence_NoPitchFiniteFields()
    {
        var r = NewTracker().Process(new float[2048], 0, 2048);
        Assert.False(r.HasPitch);
        Assert.Equal(PitchStatus.Silent, r.Status);
        AssertFinite(r);
    }

    [Fact]
    public void Process_SineBelowSilenceGate_Silent()
    {
        var x = Signals.Harmonic(110.0, new[] { 0.0005 }, 100);  // ≈ −69 dBFS RMS
        var r = NewTracker().Process(x, 0, 2048);
        Assert.Equal(PitchStatus.Silent, r.Status);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Process_WhiteNoise_NoPitch(int seed)
    {
        var x = Signals.WhiteNoise(100, 0.1, seed);
        var r = NewTracker().Process(x, 0, 2048);
        Assert.False(r.HasPitch);
        Assert.Equal(PitchStatus.Aperiodic, r.Status);
        AssertFinite(r);
    }

    [Fact]
    public void Process_NonFiniteSamples_InvalidInputNoThrow()
    {
        var x = KarplusStrong.Pluck(45, 100);
        x[700] = float.NaN; x[900] = float.PositiveInfinity;
        var r = NewTracker().Process(x, 0, 2048);
        Assert.Equal(PitchStatus.InvalidInput, r.Status);
        AssertFinite(r);
    }

    [Fact]
    public void Process_WindowShorterThanTwoLowestPeriods_TooShort()
    {
        var x = KarplusStrong.Pluck(64, 100);
        var r = NewTracker().Process(x, 0, 1000);
        Assert.Equal(PitchStatus.TooShort, r.Status);
        AssertFinite(r);
    }

    [Fact]
    public void Process_DcOffset_DoesNotChangePitch()
    {
        var x = KarplusStrong.Pluck(45, 300);
        for (int i = 0; i < x.Length; i++) x[i] += 0.2f;
        var r = NewTracker().Process(x, 4800, 2048);
        Assert.InRange(Midi.CentsBetween(110.0, r.Hz), -3.0, 3.0);
    }

    [Fact]
    public void Process_ReportsRmsAndConfidence()
    {
        var x = Signals.Harmonic(110.0, new[] { 0.5 }, 100, t60Sec: 1000);
        var r = NewTracker().Process(x, 0, 2048);
        Assert.InRange(r.Rms, 0.5 / Math.Sqrt(2) - 0.01, 0.5 / Math.Sqrt(2) + 0.01);
        Assert.InRange(r.Confidence, 0.95f, 1.0f);
        Assert.InRange(r.Aperiodicity, 0.0f, 0.05f);
    }

    [Fact]
    public void Process_SameInputTwice_IdenticalResult()
    {
        var x = KarplusStrong.Pluck(50, 300, seed: 9);
        Signals.AddNoise(x, 15.0, seed: 4, from: 0, to: x.Length);
        var t = NewTracker();
        var a = t.Process(x, 4800, 2048);
        t.Process(KarplusStrong.Pluck(64, 100), 0, 2048);  // state from another call must not leak
        var b = t.Process(x, 4800, 2048);
        var c = NewTracker().Process(x, 4800, 2048);
        Assert.Equal(a.Hz, b.Hz); Assert.Equal(a.Hz, c.Hz);
        Assert.Equal(a.Confidence, b.Confidence); Assert.Equal(a.Rms, c.Rms);
    }

    [Fact]
    public void Process_SteadyState_AllocatesNothing()
    {
        var x = KarplusStrong.Pluck(40, 1000);
        var noise = Signals.WhiteNoise(100, 0.1, 1);
        var silence = new float[2048];
        var t = NewTracker();
        for (int i = 0; i < 5; i++) { t.Process(x, 512 * i, 2048); t.Process(noise, 0, 2048); t.Process(silence, 0, 2048); }  // warm-up (JIT)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) { t.Process(x, 4800 + 512 * i, 2048); t.Process(noise, 0, 2048); t.Process(silence, 0, 2048); t.Process(x, 0, 100); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Process_CpuTimePerFrame_FitsTheHopBudget()
    {
        var x = KarplusStrong.Pluck(40, 2000);
        var t = NewTracker();
        for (int i = 0; i < 20; i++) t.Process(x, 512 * i, 2048);
        const int frames = 150;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) t.Process(x, 512 * i, 2048);
        sw.Stop();
        double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
        _out.WriteLine($"YIN 2048 @48k: {msPerFrame:F3} ms per frame (hop budget {512 * 1000.0 / Sr:F1} ms)");
        Assert.True(msPerFrame < 512 * 1000.0 / Sr, $"{msPerFrame:F3} ms per frame exceeds the 10.7 ms hop");
    }

    [Fact]
    public void Process_NullOrOutOfRangeBuffer_Throws()
    {
        var t = NewTracker();
        Assert.Throws<ArgumentNullException>(() => t.Process(null, 0, 2048));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Process(new float[100], 50, 100));
    }

    static void AssertFinite(PitchResult r)
    {
        Assert.True(double.IsFinite(r.Hz)); Assert.True(double.IsFinite(r.UncorrectedHz)); Assert.True(float.IsFinite(r.Confidence));
        Assert.True(float.IsFinite(r.Aperiodicity)); Assert.True(float.IsFinite(r.Rms));
    }
}
