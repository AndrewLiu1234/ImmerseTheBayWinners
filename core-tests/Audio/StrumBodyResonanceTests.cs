using System;
using System.Collections.Generic;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL3 Core, real-guitar failure modes reproduced synthetically (correctness only; accuracy claims come from the recorded set).
// 1) Body air resonance: on the dev recordings every attack rings a 98.1 Hz peak (acoustic guitar A0 mode) that is absent in
//    silence, decays ≈ 15 dB over the strum window and sits 6–16 dB below the loudest partial. It is no chord note: it cut the
//    precision of every hypothesis and sat on G2, so clean D and Em read as G / Unclear (D clean 0/5, Em 0/5).
// 2) Slow beginner strums (strings spread over ~100+ ms) re-triggered the onset detector 106–128 ms after the first onset.
[Trait("Category", "Synthetic")]
public class StrumBodyResonanceTests
{
    const int Sr = StrumScenes.Sr;
    static readonly int Onset = StrumScenes.OnsetIndex;
    const double BodyHz = 98.1, BodyT60 = 0.9;
    readonly ITestOutputHelper _out;
    public StrumBodyResonanceTests(ITestOutputHelper o) { _out = o; }
    void Log(string s) { _out.WriteLine(s); Console.WriteLine("[Synthetic] body " + s); }

    /// <summary>The verifier before this fix (window 30–250 ms, precision floor 0.02, no body band, no rival-note rules). Logged
    /// for comparison only — it shows the synthetic scene reproduces the recorded failure.</summary>
    static readonly AudioConfig Before = new AudioConfig(strumWindowStartSec: 0.03, strumWindowEndSec: 0.25, strumPrecisionFloor: 0.02,
        strumBodyMinMidi: 41, strumBodyMaxMidi: 40, strumDistinctPresenceRatio: 0, strumFaultMinPresence: 0, onsetMinGapSec: 0.06);

    /// <summary>Adds the body's air mode: a decaying 98.1 Hz sinusoid excited at the onset, <paramref name="relAmp"/> × the
    /// strum's per-string amplitude (0.3), seeded phase.</summary>
    static float[] WithBody(float[] x, double relAmp, int seed)
    {
        var y = (float[])x.Clone();
        double a = 0.3 * relAmp, k = Math.Log(1000.0) / BodyT60, ph = (seed * 0.618034) % 1.0 * 2 * Math.PI;
        for (int i = Onset; i < y.Length; i++)
        {
            double t = (double)(i - Onset) / Sr;
            y[i] += (float)(a * Math.Exp(-k * t) * Math.Sin(2 * Math.PI * BodyHz * t + ph));
        }
        return y;
    }

    static StrumCheckResult Check(AudioConfig cfg, float[] x, ChordShape expected) =>
        new StrumVerifier(cfg, Sr).Verify(x, 0, x.Length, Onset, expected, StrumVerifier.DefaultConfusers(expected));

    static readonly ChordShape[] Chords = { ChordLibrary.G, ChordLibrary.C, ChordLibrary.D, ChordLibrary.Em, ChordLibrary.Am };

    [Fact]
    public void Verify_CleanStrumWithBodyAirResonance_StaysClean()
    {
        int n = 0, ok = 0, okBefore = 0;
        var fails = new List<string>();
        foreach (var c in Chords)
            for (int seed = 1; seed <= 8; seed++)
                foreach (double rel in new[] { 0.5, 1.0 })
                {
                    var x = WithBody(KarplusStrong.Strum(c, new StrumMistake[0], 30, 30, seed), rel, seed);
                    var r = Check(AudioConfig.Default, x, c);
                    n++;
                    if (r.Clean) ok++; else fails.Add($"{c.Name} seed {seed} body×{rel}: {r}");
                    if (Check(Before, x, c).Clean) okBefore++;
                }
        Log($"clean strum + 98 Hz body mode: clean {ok}/{n} (before the fix {okBefore}/{n})");
        foreach (var f in fails.Take(8)) Log("  " + f);
        Assert.True(ok >= n * 0.95, $"clean {ok}/{n}: " + string.Join(" | ", fails.Take(5)));
    }

    [Theory]
    [InlineData("D", 6)]     // D + low E strummed (D m1)
    [InlineData("C", 6)]     // C + low E strummed (C m2)
    [InlineData("Am", 6)]    // Am + low E strummed (Am m2)
    public void Verify_ExtraBassStringWithBodyAirResonance_NotClean(string chord, int extra)
    {
        var c = ChordLibrary.Get(chord);
        int clean = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            var x = WithBody(KarplusStrong.Strum(c, new[] { StrumMistake.Extra(extra) }, 30, 30, seed), 1.0, seed);
            if (Check(AudioConfig.Default, x, c).Clean) clean++;
        }
        Assert.True(clean == 0, $"{chord} + string {extra}: {clean}/8 judged clean");
    }

    [Theory]
    [InlineData("G", 6)]     // G with the low-E finger missing (G m1): E2 instead of G2, under the body band
    [InlineData("C", 5)]     // C m1: A2 instead of C3
    [InlineData("Em", 5)]    // Em m1: A2 instead of B2
    public void Verify_OpenStringWithBodyAirResonance_NotClean(string chord, int s)
    {
        var c = ChordLibrary.Get(chord);
        int clean = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            var x = WithBody(KarplusStrong.Strum(c, new[] { StrumMistake.Open(s) }, 30, 30, seed), 1.0, seed);
            if (Check(AudioConfig.Default, x, c).Clean) clean++;
        }
        Assert.True(clean == 0, $"{chord} with string {s} open: {clean}/8 judged clean");
    }

    [Fact]
    public void Detect_SlowBeginnerStrum_OneOnsetPerStrum()
    {
        // Strings spread over 120 ms (±25 %), strums 0.6 s apart: one onset each, inside the strum (first string −30 ms … +60 ms;
        // where in a 120 ms strum "the" strum time is is ambiguous, the latency calibration absorbs a constant bias).
        int extraNow = 0, extraBefore = 0, missed = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            var tr = new OnsetTrain(Sr, seed);
            for (int i = 0; i < 6; i++) tr.Strum(0.5 + 0.6 * i, Chords[i % Chords.Length], up: false, spreadMs: 120);
            var x = tr.Render(snrDb: 30, noiseSeed: seed);
            var now = DetectAll(AudioConfig.Default, x);
            extraNow += Math.Max(0, now.Count - tr.OnsetsSec.Count);
            extraBefore += Math.Max(0, DetectAll(Before, x).Count - tr.OnsetsSec.Count);
            foreach (double t in tr.OnsetsSec) if (!now.Any(d => d >= t - 0.03 && d <= t + 0.06)) missed++;
        }
        Log($"slow 120 ms strums: extra onsets {extraNow} (before the fix {extraBefore}), missed {missed} of 36");
        Assert.Equal(0, extraNow);
        Assert.Equal(0, missed);
    }

    static List<double> DetectAll(AudioConfig cfg, float[] x)
    {
        var d = new SpectralFluxOnset(cfg, Sr);
        var on = new List<double>();
        for (int i = 0; i < x.Length; i += 512) { d.Process(x, i, Math.Min(512, x.Length - i)); while (d.TryReadOnset(out double t)) on.Add(t); }
        return on;
    }
}
