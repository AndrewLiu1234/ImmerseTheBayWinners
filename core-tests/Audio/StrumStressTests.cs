using System;
using System.Collections.Generic;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL3 Core: a held-out synthetic set for the strum check (seeds the main grid never uses; ±15-cent string detune; darker
// excitations; half of it through a simulated laptop-mic high-pass that rolls off the bass fundamentals). The strum
// parameters were chosen on this set's sibling sweep; it guards against regressions. Synthetic correctness only.
[Trait("Category", "Synthetic")]
public class StrumStressTests
{
    readonly ITestOutputHelper _out;
    public StrumStressTests(ITestOutputHelper o) { _out = o; }

    /// <summary>Two cascaded one-pole high-passes at <paramref name="fc"/> Hz: a MacBook mic's weak low end (−10 dB at G2).</summary>
    internal static float[] HighPass(float[] x, double fc, int sr = StrumScenes.Sr)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            double a = 1.0 / (1.0 + 2 * Math.PI * fc / sr), prevX = 0, prevY = 0;
            for (int i = 0; i < y.Length; i++) { double v = a * (prevY + y[i] - prevX); prevX = y[i]; prevY = v; y[i] = (float)v; }
        }
        return y;
    }

    [Fact]
    public void Verify_HeldOutDetunedLaptopMicSet_AccuracyAtLeast95AndFalseCleanAtMost3()
    {
        var scenes = new List<(ChordShape exp, ChordShape played, StrumMistake[] m, int truth)>();   // 0 clean, 1 wrong chord, 2 fault
        foreach (var c in ChordLibrary.Must)
        {
            scenes.Add((c, c, null, 0));
            foreach (var o in ChordLibrary.Must) if (o != c) scenes.Add((c, o, null, 1));
            for (int s = 6; s >= 1; s--)
                if (c.FretOn(s) > 0) { scenes.Add((c, c, new[] { StrumMistake.Open(s) }, 2)); scenes.Add((c, c, new[] { StrumMistake.Fret(s, 1) }, 2)); }
            for (int s = c.BassString + 1; s <= 6; s++) scenes.Add((c, c, new[] { StrumMistake.Extra(s) }, 2));
        }
        // Mutes a strum can hear: a pitch class only that string gives, or the bass.
        scenes.Add((ChordLibrary.C, ChordLibrary.C, new[] { StrumMistake.Mute(3) }, 2));
        scenes.Add((ChordLibrary.G, ChordLibrary.G, new[] { StrumMistake.Mute(4) }, 2));
        scenes.Add((ChordLibrary.Am, ChordLibrary.Am, new[] { StrumMistake.Mute(2) }, 2));
        scenes.Add((ChordLibrary.D, ChordLibrary.D, new[] { StrumMistake.Mute(3) }, 2));
        scenes.Add((ChordLibrary.Em, ChordLibrary.Em, new[] { StrumMistake.Mute(6) }, 2));

        var v = new StrumVerifier(AudioConfig.Default, StrumScenes.Sr);
        var confs = ChordLibrary.All.ToDictionary(c => c.Name, c => StrumVerifier.DefaultConfusers(c));
        int[] n = new int[3], ok = new int[3];
        int faulty = 0, falseClean = 0, hpN = 0, hpOk = 0, k = 0;
        foreach (var sc in scenes)
        {
            foreach (int seed in new[] { 11, 12, 13, 14 })
                foreach (double snr in new[] { 20.0, 10.0 })
                {
                    var x = KarplusStrong.Strum(sc.played, sc.m, 10 + 10 * (seed % 4), snr, seed * 37 + k,
                                                brightness: seed % 2 == 0 ? 0.15 : 0.4, detuneCents: 15);
                    bool hp = seed >= 13;
                    if (hp) x = HighPass(x, 150);
                    var r = v.Verify(x, 0, x.Length, StrumScenes.OnsetIndex, sc.exp, confs[sc.exp.Name]);
                    bool right = sc.truth == 0 ? r.Clean : sc.truth == 1 ? !r.Clean && r.BestMatch == sc.played.Name : !r.Clean;
                    n[sc.truth]++; if (right) ok[sc.truth]++;
                    if (sc.truth != 0) { faulty++; if (r.Clean) falseClean++; }
                    if (hp) { hpN++; if (right) hpOk++; }
                    if (!right) _out.WriteLine($"✗ {sc.exp} played {sc.played} {(sc.m == null ? "" : string.Join("+", sc.m))} seed{seed} {snr} dB hp={hp}: {r}");
                }
            k++;
        }
        double acc = 100.0 * ok.Sum() / n.Sum(), fc = 100.0 * falseClean / faulty;
        string line = $"held-out: n={n.Sum()} acc {acc:F1}% · clean {100.0 * ok[0] / n[0]:F0}% · wrong chord {100.0 * ok[1] / n[1]:F0}% · " +
                      $"faults {100.0 * ok[2] / n[2]:F0}% · false clean {fc:F1}% · laptop-mic half {100.0 * hpOk / hpN:F1}%";
        _out.WriteLine(line);
        Console.WriteLine("[Synthetic] strum " + line);
        Assert.True(acc >= 95.0, line);
        Assert.True(fc <= 3.0, line);
    }

    [Fact]
    public void Verify_WhiteNoise_PeakBelowStrumGate()
    {
        var v = new StrumVerifier(AudioConfig.Default, StrumScenes.Sr);
        double maxPeak = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            var x = StrumScenes.NoiseOnly(seed);
            var r = v.Verify(x, 0, x.Length, StrumScenes.OnsetIndex, ChordLibrary.G, null);
            Assert.Equal(StrumIssue.Unclear, r.Issue);
            maxPeak = Math.Max(maxPeak, v.LastPeakDb);
        }
        Console.WriteLine($"[Synthetic] strum white-noise peak ≤ {maxPeak:F1} dB over the median (gate {AudioConfig.Default.StrumMinPeakDb} dB)");
        Assert.True(maxPeak < AudioConfig.Default.StrumMinPeakDb);
    }
}
