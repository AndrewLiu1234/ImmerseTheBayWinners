using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL3 Core: strum check (chord level). Synthetic (Karplus–Strong) correctness only — accuracy claims come from the recorded eval set.
[Trait("Category", "Synthetic")]
public class StrumVerifierTests
{
    const int Sr = StrumScenes.Sr;
    static readonly int Onset = StrumScenes.OnsetIndex;
    readonly ITestOutputHelper _out;
    public StrumVerifierTests(ITestOutputHelper o) { _out = o; }

    void Log(string s) { _out.WriteLine(s); Console.WriteLine("[Synthetic] strum " + s); }

    static StrumVerifier NewVerifier(AudioConfig cfg = null) => new StrumVerifier(cfg ?? AudioConfig.Default, Sr);
    static readonly Dictionary<string, IReadOnlyList<ChordShape>> Confusers =
        ChordLibrary.All.ToDictionary(c => c.Name, c => StrumVerifier.DefaultConfusers(c));

    static StrumCheckResult Check(StrumVerifier v, float[] x, ChordShape expected) =>
        v.Verify(x, 0, x.Length, Onset, expected, Confusers[expected.Name]);

    // ---- the grid ----

    /// <summary>What a scene should produce: Clean, WrongChord(played), or a chord-level fault (not clean). Faults that keep the
    /// expected chord's pitch classes and bass note are per-string (pluck-check) faults: reported, not gated.</summary>
    enum Truth { Clean, WrongChord, Fault, PerString }

    sealed class Scene
    {
        public ChordShape Expected, Played;
        public StrumMistake[] Mistakes;
        public Truth Truth;
        public StrumIssue Issue;      // the issue a fault should report
        public int IssueString;
        public string Label;
    }

    static (int mask, int bass) ChordLevel(int[] notes)
    {
        int mask = 0, bass = int.MaxValue;
        foreach (int m in notes) if (m >= 0) { mask |= 1 << (m % 12); bass = Math.Min(bass, m); }
        return (mask, bass);
    }

    // Physics, independent of the verifier: a note is masked in a mixture when each of its partials 1–4 lies within 30 cents of
    // a partial (1–16) of another note (F#4 under G is B2's 3rd and 6th partial). A fault is audible at chord level only if a
    // removed or an added note is unmasked; otherwise a strum cannot tell it apart and the pluck check must.
    static bool Masked(int note, IEnumerable<int> others)
    {
        var o = others.Where(m => m != note).Distinct().ToList();
        for (int h = 1; h <= 4; h++)
        {
            double f = h * Midi.ToHz(note);
            bool hit = o.Any(m => Enumerable.Range(1, 16).Any(k => Math.Abs(1200 * Math.Log2(f / (k * Midi.ToHz(m)))) <= 30));
            if (!hit) return false;
        }
        return true;
    }

    static bool Audible(int[] before, int[] after)
    {
        var b = before.Where(m => m >= 0).Distinct().ToList();
        var a = after.Where(m => m >= 0).Distinct().ToList();
        return b.Except(a).Any(r => !Masked(r, b)) || a.Except(b).Any(x => !Masked(x, a));
    }

    static IEnumerable<Scene> ScenesFor(ChordShape c)
    {
        yield return new Scene { Expected = c, Played = c, Truth = Truth.Clean, Label = "clean" };
        foreach (var other in ChordLibrary.Must)
            if (other.Name != c.Name)
                yield return new Scene { Expected = c, Played = other, Truth = Truth.WrongChord, Issue = StrumIssue.WrongChord, Label = "is " + other.Name };
        var faults = new List<(StrumMistake[] m, StrumIssue issue, int s)>();
        for (int s = 6; s >= 1; s--)
        {
            int fret = c.FretOn(s);
            if (fret > 0)
            {
                faults.Add((new[] { StrumMistake.Open(s) }, StrumIssue.OpenStringInstead, s));
                if (fret > 1) faults.Add((new[] { StrumMistake.Fret(s, -1) }, StrumIssue.WrongFretLow, s));
                faults.Add((new[] { StrumMistake.Fret(s, +1) }, StrumIssue.WrongFretHigh, s));
            }
            if (fret >= 0) faults.Add((new[] { StrumMistake.Mute(s) }, StrumIssue.Muted, s));
        }
        var extra = new List<StrumMistake>();
        for (int s = c.BassString + 1; s <= 6; s++)
        {
            faults.Add((new[] { StrumMistake.Extra(s) }, StrumIssue.ExtraString, s));
            extra.Add(StrumMistake.Extra(s));
        }
        if (extra.Count > 1) faults.Add((extra.ToArray(), StrumIssue.ExtraString, 6));

        var cl0 = ChordLevel(StrumScenes.SoundedNotes(c, null));
        foreach (var f in faults)
        {
            var cl = ChordLevel(StrumScenes.SoundedNotes(c, f.m));
            bool visible = (cl.mask != cl0.mask || cl.bass != cl0.bass) && Audible(StrumScenes.SoundedNotes(c, null), StrumScenes.SoundedNotes(c, f.m));
            yield return new Scene
            {
                Expected = c, Played = c, Mistakes = f.m, Truth = visible ? Truth.Fault : Truth.PerString,
                Issue = f.issue, IssueString = f.s, Label = string.Join("+", f.m.Select(x => x.ToString())),
            };
        }
    }

    static readonly double[] Spreads = { 10, 20, 30, 40 };
    static readonly double[] Snrs = { double.PositiveInfinity, 20, 10 };
    static readonly int[] Seeds = { 1, 2 };

    sealed class Outcome { public Scene Scene; public double Snr, Spread; public int Seed; public StrumCheckResult R; }

    static float[] Render(Scene sc, double spread, double snr, int seed)
    {
        // Wrong chords are the other chord played cleanly; brightness alternates so templates are not fitted to one timbre.
        double bright = seed % 2 == 1 ? 1.0 : 0.15;
        return KarplusStrong.Strum(sc.Played, sc.Mistakes, spread, snr, seed * 1009 + (int)spread, brightness: bright, detuneCents: 8);
    }

    static List<Outcome> RunGrid()
    {
        var v = NewVerifier();
        var res = new List<Outcome>();
        foreach (var c in ChordLibrary.Must)
            foreach (var sc in ScenesFor(c))
                foreach (double snr in Snrs)
                    foreach (double sp in Spreads)
                        foreach (int seed in Seeds)
                            res.Add(new Outcome { Scene = sc, Snr = snr, Spread = sp, Seed = seed, R = Check(v, Render(sc, sp, snr, seed), sc.Expected) });
        return res;
    }

    static bool Right(Outcome o)
    {
        switch (o.Scene.Truth)
        {
            case Truth.Clean: return o.R.Clean;
            case Truth.WrongChord: return !o.R.Clean && o.R.BestMatch == o.Scene.Played.Name;
            default: return !o.R.Clean;
        }
    }

    static bool IssueRight(Outcome o) =>
        o.R.Issue == o.Scene.Issue && (o.Scene.Issue == StrumIssue.WrongChord || o.R.IssueString == o.Scene.IssueString);

    static string SnrName(double snr) => double.IsPositiveInfinity(snr) ? "clean" : $"{snr:F0} dB";

    static List<Outcome> _grid;
    static readonly object GridLock = new object();
    static List<Outcome> Grid() { lock (GridLock) return _grid ??= RunGrid(); }

    [Fact]
    public void Verify_SyntheticGrid_AccuracyAtLeast95AndFalseCleanAtMost3()
    {
        var g = Grid();
        Log($"{"SNR",-7} {"n",5} {"acc%",6} {"clean→✓%",9} {"wrong→X%",9} {"fault→✕%",9} {"falseClean%",11} {"issue%",7} | per-string (pluck-check) faults heard clean");
        foreach (double snr in Snrs.Concat(new[] { double.NaN }))
        {
            var rows = g.Where(o => double.IsNaN(snr) || o.Snr.Equals(snr)).ToList();
            var gated = rows.Where(o => o.Scene.Truth != Truth.PerString).ToList();
            var faulty = gated.Where(o => o.Scene.Truth != Truth.Clean).ToList();
            var faults = gated.Where(o => o.Scene.Truth == Truth.Fault).ToList();
            var per = rows.Where(o => o.Scene.Truth == Truth.PerString).ToList();
            double acc = 100.0 * gated.Count(Right) / gated.Count;
            double fc = 100.0 * faulty.Count(o => o.R.Clean) / faulty.Count;
            Log($"{(double.IsNaN(snr) ? "all" : SnrName(snr)),-7} {gated.Count,5} {acc,6:F1} " +
                $"{Pct(gated.Where(o => o.Scene.Truth == Truth.Clean), Right),9:F1} {Pct(gated.Where(o => o.Scene.Truth == Truth.WrongChord), Right),9:F1} " +
                $"{Pct(faults, Right),9:F1} {fc,11:F1} {Pct(faulty, IssueRight),7:F1} | {per.Count(o => o.R.Clean)}/{per.Count}");
        }
        var all = g.Where(o => o.Scene.Truth != Truth.PerString).ToList();
        var bad = all.Where(o => !Right(o)).ToList();
        foreach (var o in bad.Take(40)) Log($"  ✗ {o.Scene.Expected,-3} {o.Scene.Label,-16} {SnrName(o.Snr),-6} sp{o.Spread} seed{o.Seed}: {o.R}");
        double accAll = 100.0 * all.Count(Right) / all.Count;
        var faultyAll = all.Where(o => o.Scene.Truth != Truth.Clean).ToList();
        double fcAll = 100.0 * faultyAll.Count(o => o.R.Clean) / faultyAll.Count;
        foreach (double snr in Snrs)
        {
            var rows = all.Where(o => o.Snr.Equals(snr)).ToList();
            var fr = rows.Where(o => o.Scene.Truth != Truth.Clean).ToList();
            Assert.True(100.0 * rows.Count(Right) / rows.Count >= 95.0, $"{SnrName(snr)}: accuracy {100.0 * rows.Count(Right) / rows.Count:F1}%");
            Assert.True(100.0 * fr.Count(o => o.R.Clean) / fr.Count <= 3.0, $"{SnrName(snr)}: false clean {100.0 * fr.Count(o => o.R.Clean) / fr.Count:F1}%");
        }
        Assert.True(accAll >= 95.0, $"accuracy {accAll:F1}%");
        Assert.True(fcAll <= 3.0, $"false clean {fcAll:F1}%");
    }

    static double Pct(IEnumerable<Outcome> rows, Func<Outcome, bool> f)
    {
        var l = rows.ToList();
        return l.Count == 0 ? double.NaN : 100.0 * l.Count(f) / l.Count;
    }

    [Fact]
    public void Verify_SyntheticGrid_PrintsConfusionMatrix()
    {
        var g = Grid().Where(o => o.Scene.Truth == Truth.Clean || o.Scene.Truth == Truth.WrongChord).ToList();
        var names = ChordLibrary.All.Select(c => c.Name).ToList();
        var sb = new StringBuilder();
        Log("confusion (played chord → best match; clean + wrong-chord scenes, all SNRs, expected = target)");
        Log($"{"played",-7}" + string.Concat(names.Select(n => $"{n,5}")) + "   null");
        foreach (var p in ChordLibrary.Must)
        {
            var rows = g.Where(o => o.Scene.Played.Name == p.Name).ToList();
            Log($"{p.Name,-7}" + string.Concat(names.Select(n => $"{rows.Count(o => o.R.BestMatch == n),5}")) + $"   {rows.Count(o => o.R.BestMatch == null),4}");
        }
        // The pairs beginners confuse: margin distributions when the target is one and the other is played.
        foreach (var (a, b) in new[] { ("G", "Em"), ("Em", "G"), ("C", "Am"), ("Am", "C"), ("D", "G"), ("G", "D"), ("D", "Am"), ("Em", "Am") })
        {
            var rows = g.Where(o => o.Scene.Expected.Name == a && o.Scene.Played.Name == b).ToList();
            int right = rows.Count(o => o.R.BestMatch == b && !o.R.Clean);
            Log($"target {a,-2} played {b,-2}: {right}/{rows.Count} heard {b}, margin min/max {rows.Min(o => o.R.Margin):+0.00;-0.00}/{rows.Max(o => o.R.Margin):+0.00;-0.00}");
        }
        foreach (var c in ChordLibrary.Must)
        {
            var rows = Grid().Where(o => o.Scene.Expected.Name == c.Name && o.Scene.Truth == Truth.Clean).ToList();
            Log($"clean {c.Name,-2}: margin min {rows.Min(o => o.R.Margin):F2} · score min {rows.Min(o => o.R.ExpectedScore):F2} · fault-vs-expected max {rows.Max(o => o.R.FaultScore - o.R.ExpectedScore):+0.00;-0.00} · peak min {rows.Min(o => o.R.PeakDb):F0} dB");
        }
        // Issue confusion over the chord-level faults.
        var faults = Grid().Where(o => o.Scene.Truth == Truth.Fault).ToList();
        Log("fault issue confusion (injected → reported)");
        foreach (var grp in faults.GroupBy(o => o.Scene.Issue))
            Log($"  {grp.Key,-17} n={grp.Count(),3}: " + string.Join(", ", grp.GroupBy(o => o.R.Clean ? "CLEAN" : o.R.Issue + (o.R.Issue == grp.Key && o.R.IssueString != o.Scene.IssueString ? "(other string)" : ""))
                .OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}")));
        Log("per-scene: fault label → right/total");
        foreach (var grp in Grid().Where(o => o.Scene.Truth == Truth.Fault || o.Scene.Truth == Truth.PerString).GroupBy(o => o.Scene.Expected.Name + " " + o.Scene.Label))
            Log($"  {grp.Key,-22} {(grp.First().Scene.Truth == Truth.PerString ? "(per-string)" : ""),-12} not-clean {grp.Count(o => !o.R.Clean)}/{grp.Count()} issue {grp.Count(IssueRight)}/{grp.Count()}");
    }
}
