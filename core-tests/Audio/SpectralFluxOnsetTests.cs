using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL6 Core: spectral-flux onsets. Synthetic (Karplus–Strong) correctness only — not an accuracy claim.
[Trait("Category", "Synthetic")]
public class SpectralFluxOnsetTests
{
    const int Sr = 48000;
    const double TolSec = 0.030;          // SL6 acceptance: ±30 ms
    const double BaseSlotSec = 0.3;       // eighths at 100 bpm (Riptide / Stand By Me tempo) at 100 %
    static readonly ChordShape[] Prog = { ChordLibrary.G, ChordLibrary.C, ChordLibrary.D, ChordLibrary.Em, ChordLibrary.Am };
    static readonly string StrumPattern = "D-DU-UDU";                 // Appendix B5 level 2
    static readonly string PickPattern = "B3231323";                  // Appendix B5 picking, 4/4 eighths

    readonly ITestOutputHelper _out;
    public SpectralFluxOnsetTests(ITestOutputHelper o) { _out = o; }

    static List<double> Detect(float[] x, int chunk = 512, AudioConfig cfg = null)
    {
        var d = new SpectralFluxOnset(cfg ?? AudioConfig.Default, Sr);
        var on = new List<double>();
        for (int i = 0; i < x.Length; i += chunk)
        {
            d.Process(x, i, Math.Min(chunk, x.Length - i));
            while (d.TryReadOnset(out double t)) on.Add(t);
        }
        return on;
    }

    // Greedy one-to-one matching within ±tol. Returns (tp, fp, fn, mean signed error of matches, s).
    static (int tp, int fp, int fn, double bias) Score(IReadOnlyList<double> truth, IReadOnlyList<double> det, double tol = TolSec)
    {
        var used = new bool[det.Count];
        int tp = 0; double err = 0;
        foreach (double t in truth)
        {
            int best = -1; double bd = tol + 1e-12;
            for (int j = 0; j < det.Count; j++)
                if (!used[j] && Math.Abs(det[j] - t) <= bd) { bd = Math.Abs(det[j] - t); best = j; }
            if (best >= 0) { used[best] = true; tp++; err += det[best] - t; }
        }
        return (tp, det.Count - tp, truth.Count - tp, tp > 0 ? err / tp : 0);
    }

    static double F1((int tp, int fp, int fn, double bias) s) => s.tp == 0 ? 0 : 2.0 * s.tp / (2.0 * s.tp + s.fp + s.fn);

    static OnsetTrain StrumTrain(double tempo, int bars, int seed)
    {
        var tr = new OnsetTrain(Sr, seed);
        double slot = BaseSlotSec / tempo, t = 0.5;
        for (int b = 0; b < bars; b++)
            for (int i = 0; i < 8; i++, t += slot)
            {
                char c = StrumPattern[i];
                if (c != '-') tr.Strum(t, Prog[b % Prog.Length], up: c == 'U');
            }
        return tr;
    }

    static OnsetTrain PickTrain(double tempo, int bars, int seed)
    {
        var tr = new OnsetTrain(Sr, seed);
        double slot = BaseSlotSec / tempo, t = 0.5;
        for (int b = 0; b < bars; b++)
            for (int i = 0; i < 8; i++, t += slot)
            {
                var ch = Prog[b % Prog.Length];
                char c = PickPattern[i];
                tr.Pluck(t, ch, c == 'B' ? ch.BassString : c - '0');
            }
        return tr;
    }

    [Fact]
    public void Detect_StrumAndPickTrains_F1AtLeast090AtEveryTempoAndNoise()
    {
        var lines = new List<string>();
        var fails = new List<string>();
        foreach (var kind in new[] { "strum", "pick" })
            foreach (double tempo in new[] { 0.5, 0.75, 1.0 })
                foreach (double snr in new[] { double.PositiveInfinity, 20.0, 10.0 })
                {
                    var tr = kind == "strum" ? StrumTrain(tempo, 8, 11) : PickTrain(tempo, 8, 12);
                    var x = tr.Render(snrDb: snr, noiseSeed: 5);
                    var s = Score(tr.OnsetsSec, Detect(x));
                    double f1 = F1(s);
                    string snrName = double.IsPositiveInfinity(snr) ? "clean" : $"{snr:F0} dB";
                    string line = $"{kind,-5} {tempo * 100,3:F0}% {snrName,-6} F1 {f1:F3}  tp {s.tp} fp {s.fp} fn {s.fn}  bias {s.bias * 1000:+0.0;-0.0} ms";
                    lines.Add(line);
                    bool gated = snr >= 20;                      // 10 dB is reported, not gated
                    if (gated && f1 < 0.9) fails.Add(line);
                }
        foreach (var l in lines) { _out.WriteLine(l); Console.WriteLine("[Synthetic] onset " + l); }
        Assert.True(fails.Count == 0, "onset F1 < 0.9: " + string.Join(" | ", fails));
    }

    [Fact]
    public void Detect_Silence_NoOnsetsNoThrow()
    {
        Assert.Empty(Detect(new float[Sr * 3]));
    }

    [Fact]
    public void Detect_SteadyNoise_AtMostOneFalseOnsetIn10s()
    {
        var x = Signals.WhiteNoise(10000, 0.01, seed: 3, sampleRate: Sr);
        Assert.True(Detect(x).Count <= 1);
    }

    [Fact]
    public void Detect_NaNAndInfSamples_Ignored()
    {
        var tr = PickTrain(1.0, 2, 4);
        var x = tr.Render();
        for (int i = 1000; i < x.Length; i += 7919) x[i] = i % 2 == 0 ? float.NaN : float.PositiveInfinity;
        var s = Score(tr.OnsetsSec, Detect(x));
        Assert.True(F1(s) >= 0.9, $"F1 {F1(s):F3}");
    }

    [Fact]
    public void Detect_SingleStrum_OneOnsetWithin30ms()
    {
        var tr = new OnsetTrain(Sr, 1);
        tr.Strum(0.5, ChordLibrary.G, up: false, spreadMs: 40);
        var on = Detect(tr.Render());
        Assert.Single(on);
        Assert.InRange(on[0], 0.5 - TolSec, 0.5 + TolSec);
    }

    [Theory]
    [InlineData(1)] [InlineData(100)] [InlineData(441)] [InlineData(4096)]
    public void Process_AnyChunkSize_SameOnsets(int chunk)
    {
        var x = StrumTrain(1.0, 2, 9).Render(snrDb: 20);
        Assert.Equal(Detect(x, 512), Detect(x, chunk));
    }

    [Fact]
    public void Process_MinGap_NeverTwoOnsetsCloserThan60ms()
    {
        var on = Detect(StrumTrain(1.0, 4, 2).Render(snrDb: 20));
        for (int i = 1; i < on.Count; i++) Assert.True(on[i] - on[i - 1] >= 0.06 - 1e-9);
    }

    [Fact]
    public void Process_AfterWarmup_AllocatesNothingAndIsCheap()
    {
        var x = StrumTrain(1.0, 2, 3).Render(snrDb: 20);
        var d = new SpectralFluxOnset(AudioConfig.Default, Sr);
        d.Process(x, 0, 48000);
        while (d.TryReadOnset(out _)) { }
        var sw = new Stopwatch();
        int hops = 0;
        long allocated = UntitledGuitarApp.Core.Tests.Audio.AllocProbe.MinAllocatedBytes(() =>
        {
            hops = 0;
            sw.Restart();
            for (int i = 48000; i + 512 <= x.Length; i += 512, hops++) { d.Process(x, i, 512); while (d.TryReadOnset(out _)) { } }
            sw.Stop();
        });
        Assert.Equal(0, allocated);
        double us = sw.Elapsed.TotalMilliseconds * 1000.0 / hops;
        Console.WriteLine($"[Synthetic] SpectralFluxOnset ≈ {us:F1} µs per 512-sample hop (budget: DSP < 1 ms per hop)");
        Assert.True(us < 1000, $"{us:F0} µs per hop");
    }

    [Fact]
    public void Reset_RestartsClock()
    {
        var tr = new OnsetTrain(Sr, 1);
        tr.Pluck(0.3, ChordLibrary.G, 3);
        var x = tr.Render();
        var d = new SpectralFluxOnset(AudioConfig.Default, Sr);
        d.Process(x, 0, x.Length); d.TryReadOnset(out double a);
        d.Reset();
        d.Process(x, 0, x.Length); d.TryReadOnset(out double b);
        Assert.Equal(a, b);
        Assert.Equal(x.Length, d.SamplesProcessed);
    }

    [Fact]
    public void Fft_MatchesNaiveDft()
    {
        var fft = new Fft(64);
        var rng = new XorShift32(5);
        var re = new float[64]; var im = new float[64];
        for (int i = 0; i < 64; i++) re[i] = (float)rng.NextBipolar();
        var x = (float[])re.Clone();
        fft.Forward(re, im);
        for (int k = 0; k < 64; k++)
        {
            double r = 0, m = 0;
            for (int n = 0; n < 64; n++) { r += x[n] * Math.Cos(-2 * Math.PI * k * n / 64); m += x[n] * Math.Sin(-2 * Math.PI * k * n / 64); }
            Assert.Equal(r, re[k], 3); Assert.Equal(m, im[k], 3);
        }
    }

    // ---- integration: onsets → PatternJudge ----

    [Fact]
    public void Integration_StrumAlongRiptideAt75Percent_PatternJudgeSeesSteadyOnTimeStrums()
    {
        var song = SongFiles.Load("riptide-loop.song.json");
        var timeline = new SongTimeline(song, 0.75);
        var clock = new PatternClock(song, timeline, pick: false);
        var tr = new OnsetTrain(Sr, 21);
        double until = 0;
        foreach (var slot in clock.Slots)
        {
            if (slot.IsRest || slot.Bar > 4) continue;
            var chord = ChordLibrary.Get(timeline.Spans[slot.ChordIndex].Chord);
            tr.Strum(slot.TimeSeconds, chord, up: slot.Symbol == "U");
            until = slot.TimeSeconds;
        }
        var x = tr.Render(snrDb: 20);
        var onsets = Detect(x);
        var rep = new PatternJudge(clock).Judge(onsets, 0, until);
        Console.WriteLine($"[Synthetic] PatternJudge on detected onsets: {rep.Hits.Count}/{rep.ExpectedSlots} hit, extra {rep.ExtraOnsets}, " +
                          $"mean {rep.MeanOffsetMs:+0.0;-0.0} ms, on-time {rep.OnTimePercent:F0}%, feedback {rep.FeedbackKey}");
        Assert.True(rep.ExpectedSlots >= 16);
        Assert.True(rep.MissedSlots.Count <= 1, $"missed {rep.MissedSlots.Count}");
        Assert.True(rep.ExtraOnsets <= 1, $"extra {rep.ExtraOnsets}");
        Assert.InRange(rep.MeanOffsetMs, -20, 20);
        Assert.True(rep.OnTimePercent >= 90);
        Assert.Equal("steady", rep.FeedbackKey);
    }
}
