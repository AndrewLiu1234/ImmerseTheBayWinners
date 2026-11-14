using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Core.Tests.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// SL3 Core: strum check — named Appendix B2 faults, robustness, budget, and the feed/session wiring. Synthetic correctness only.
[Trait("Category", "Synthetic")]
public class StrumCheckTests
{
    const int Sr = StrumScenes.Sr;
    static readonly int Onset = StrumScenes.OnsetIndex;
    static readonly StrumVerifier V = new StrumVerifier(AudioConfig.Default, Sr);
    static readonly object VLock = new object();

    static StrumCheckResult Check(float[] x, ChordShape expected, IReadOnlyList<ChordShape> confusers = null)
    {
        lock (VLock) return V.Verify(x, 0, x.Length, Onset, expected, confusers ?? StrumVerifier.DefaultConfusers(expected));
    }

    static StrumCheckResult Strum(ChordShape c, params StrumMistake[] m) =>
        Check(KarplusStrong.Strum(c, m, 25, 20, seed: 5), c);

    // ---- clean and wrong chords ----

    [Fact]
    public void Verify_EveryLibraryChordStrummedClean_Clean()
    {
        foreach (var c in ChordLibrary.All)
        {
            var r = Strum(c);
            Assert.True(r.Clean, r.ToString());
            Assert.Equal(c.Name, r.BestMatch);
            Assert.Equal(StrumIssue.None, r.Issue);
            Assert.Null(r.TipKey);
            Assert.True(r.Margin >= AudioConfig.Default.StrumMinMargin);
        }
    }

    [Theory]
    [InlineData("G", "Em")] [InlineData("Em", "G")] [InlineData("C", "Am")] [InlineData("Am", "C")]
    [InlineData("D", "G")] [InlineData("G", "D")] [InlineData("D", "Am")] [InlineData("Em", "E")] [InlineData("Am", "A")] [InlineData("D", "Dm")]
    public void Verify_ConfusablePairPlayed_WrongChordHeardRight(string target, string played)
    {
        var r = Check(KarplusStrong.Strum(ChordLibrary.Get(played), null, 25, 20, seed: 9), ChordLibrary.Get(target));
        Assert.False(r.Clean, r.ToString());
        Assert.Equal(played, r.BestMatch);
        Assert.Equal(StrumIssue.WrongChord, r.Issue);
        Assert.True(r.Margin < 0);
        Assert.Equal($"Heard {played} — target is {target}", Tips.ForStrum(r, ChordLibrary.Get(target)));
    }

    // ---- Appendix B2 faults that a strum can hear ----

    [Fact]
    public void Verify_CWithLowEStrummed_ExtraStringSkipString6()
    {
        var r = Strum(ChordLibrary.C, StrumMistake.Extra(6));
        Assert.False(r.Clean, r.ToString());
        Assert.Equal(StrumIssue.ExtraString, r.Issue);
        Assert.Equal(6, r.IssueString);
        Assert.Equal(PluckTipKeys.SkipString, r.TipKey);
        Assert.Equal("Skip string 6", Tips.ForStrum(r, ChordLibrary.C));
    }

    [Fact]
    public void Verify_DWithLowEAndAStrummed_StrumOnlyTopFour()
    {
        var r = Strum(ChordLibrary.D, StrumMistake.Extra(5), StrumMistake.Extra(6));
        Assert.Equal(StrumIssue.ExtraString, r.Issue);
        Assert.Equal(StrumTipKeys.StrumTopFour, r.TipKey);
        Assert.Equal("Strum only the top four", Tips.ForStrum(r, ChordLibrary.D));
    }

    [Fact]
    public void Verify_CAStringOpen_OpenStringInsteadFinger3()
    {
        var r = Strum(ChordLibrary.C, StrumMistake.Open(5));      // A2 instead of C3
        Assert.Equal(StrumIssue.OpenStringInstead, r.Issue);
        Assert.Equal(5, r.IssueString); Assert.Equal(48, r.IssueExpectedMidi); Assert.Equal(45, r.IssueHeardMidi);
        Assert.Equal("Finger 3: press string 5, fret 3", Tips.ForStrum(r, ChordLibrary.C));
    }

    [Fact]
    public void Verify_GLowEFingerMissing_OpenStringInsteadString6()
    {
        var r = Strum(ChordLibrary.G, StrumMistake.Open(6));      // E2 instead of G2
        Assert.Equal(StrumIssue.OpenStringInstead, r.Issue);
        Assert.Equal(6, r.IssueString);
        Assert.Equal("Finger 2: press string 6, fret 3", Tips.ForStrum(r, ChordLibrary.G));
    }

    [Fact]
    public void Verify_GHighEOpen_OpenStringInsteadString1()
    {
        var r = Strum(ChordLibrary.G, StrumMistake.Open(1));      // E4 instead of G4
        Assert.Equal(StrumIssue.OpenStringInstead, r.Issue);
        Assert.Equal(1, r.IssueString);
    }

    [Fact]
    public void Verify_EmFinger2Missing_OpenStringInsteadString5()
    {
        var r = Strum(ChordLibrary.Em, StrumMistake.Open(5));     // A2 instead of B2
        Assert.Equal(StrumIssue.OpenStringInstead, r.Issue);
        Assert.Equal(5, r.IssueString);
        Assert.Equal("Finger 2: press string 5, fret 2", Tips.ForStrum(r, ChordLibrary.Em));
    }

    [Fact]
    public void Verify_AmFinger2Missing_OpenStringInsteadString4()
    {
        var r = Strum(ChordLibrary.Am, StrumMistake.Open(4));     // D3 instead of E3
        Assert.Equal(StrumIssue.OpenStringInstead, r.Issue);
        Assert.Equal(4, r.IssueString);
    }

    [Fact]
    public void Verify_DBStringFretTwo_WrongFretLow()
    {
        var r = Strum(ChordLibrary.D, StrumMistake.Fret(2, -1));  // C#4 instead of D4
        Assert.Equal(StrumIssue.WrongFretLow, r.Issue);
        Assert.Equal(2, r.IssueString);
        Assert.Equal("Finger 3: one fret higher", Tips.ForStrum(r, ChordLibrary.D));
    }

    [Fact]
    public void Verify_CGStringMuted_MutedArchFinger2()
    {
        var r = Strum(ChordLibrary.C, StrumMistake.Mute(3));      // no G3 (the only G)
        Assert.Equal(StrumIssue.Muted, r.Issue);
        Assert.Equal(3, r.IssueString);
        Assert.Equal(PluckTipKeys.ArchFinger, r.TipKey);
        Assert.Equal("String 3 muted — arch finger 2", Tips.ForStrum(r, ChordLibrary.C));
    }

    [Fact]
    public void Verify_AmBStringMuted_MutedString2()
    {
        var r = Strum(ChordLibrary.Am, StrumMistake.Mute(2));     // no C4 (the only C)
        Assert.Equal(StrumIssue.Muted, r.Issue);
        Assert.Equal(2, r.IssueString);
        Assert.Equal("Finger 1: press with fingertip", Tips.ForStrum(r, ChordLibrary.Am));
    }

    [Fact]
    public void Verify_GBStringMuted_NotAWrongChord()
    {
        // B3 is B2's 2nd partial: a strum cannot hear it missing (the pluck check does). It must not become a false fault.
        var r = Strum(ChordLibrary.G, StrumMistake.Mute(2));
        Assert.Equal("G", r.BestMatch);
        Assert.NotEqual(StrumIssue.WrongChord, r.Issue);
        Assert.True(r.Clean || r.Issue == StrumIssue.Muted, r.ToString());
    }

    // ---- robustness ----

    [Fact]
    public void Verify_Silence_UnclearNothingHeard()
    {
        var r = Check(new float[Sr], ChordLibrary.G);
        Assert.False(r.Clean); Assert.Equal(StrumIssue.Unclear, r.Issue); Assert.Null(r.BestMatch);
        Assert.Equal(StrumTipKeys.StrumAgain, r.TipKey);
        Assert.Equal(0, r.Confidence); Assert.Equal(0, r.Margin);
    }

    [Fact]
    public void Verify_WhiteNoise_UnclearNeverClean()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var r = Check(StrumScenes.NoiseOnly(seed), ChordLibrary.Em);
            Assert.False(r.Clean, r.ToString());
            Assert.True(double.IsFinite(r.Confidence) && double.IsFinite(r.Margin) && double.IsFinite(r.PeakDb));
        }
    }

    [Fact]
    public void Verify_NaNInfOrTooShort_UnclearNoThrow()
    {
        var x = KarplusStrong.Strum(ChordLibrary.G, null, 20, 20, 3);
        x[Onset + 5000] = float.NaN;
        Assert.Equal(StrumIssue.Unclear, Check(x, ChordLibrary.G).Issue);
        x[Onset + 5000] = float.PositiveInfinity;
        Assert.Equal(StrumIssue.Unclear, Check(x, ChordLibrary.G).Issue);
        var y = KarplusStrong.Strum(ChordLibrary.G, null, 20, 20, 3);
        lock (VLock)
        {
            Assert.Equal(StrumIssue.Unclear, V.Verify(y, 0, Onset + 4000, Onset, ChordLibrary.G, null).Issue);   // window < half full
            Assert.Equal(StrumIssue.Unclear, V.Verify(Array.Empty<float>(), 0, 0, 0, ChordLibrary.G, null).Issue);
            Assert.True(V.Verify(y, 0, y.Length, Onset, ChordLibrary.G, null).Clean);                            // no confusers: still judged
        }
    }

    [Fact]
    public void Verify_FullScaleClipping_NoThrowFinite()
    {
        var x = KarplusStrong.Strum(ChordLibrary.C, null, 20, double.PositiveInfinity, 4);
        for (int i = 0; i < x.Length; i++) x[i] = Math.Max(-1f, Math.Min(1f, x[i] * 20f));
        var r = Check(x, ChordLibrary.C);
        Assert.True(double.IsFinite(r.Confidence) && double.IsFinite(r.Margin));
    }

    [Fact]
    public void Verify_SameInputTwice_IdenticalResult()
    {
        var x = KarplusStrong.Strum(ChordLibrary.Am, new[] { StrumMistake.Open(4) }, 30, 10, 8);
        var a = Check(x, ChordLibrary.Am); var b = Check(x, ChordLibrary.Am);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(a.ExpectedScore, b.ExpectedScore);
    }

    [Fact]
    public void Verify_ContractShapeWrongSampleRate_Throws()
    {
        var x = KarplusStrong.Strum(ChordLibrary.G, null, 20, 20, 3);
        lock (VLock)
        {
            Assert.True(V.Verify(x, Sr, Onset, ChordLibrary.G, StrumVerifier.DefaultConfusers(ChordLibrary.G)).Clean);
            Assert.Throws<ArgumentException>(() => V.Verify(x, 44100, Onset, ChordLibrary.G, null));
        }
        Assert.Throws<ArgumentNullException>(() => V.Verify(x, 0, x.Length, Onset, null, null));
    }

    [Fact]
    public void Verify_At44100_StillJudgesChords()
    {
        var v = new StrumVerifier(AudioConfig.Default, 44100);
        var x = KarplusStrong.Strum(ChordLibrary.D, null, 20, 20, 3, sampleRate: 44100);
        int onset = (int)(StrumScenes.PreSec * 44100);
        Assert.True(v.Verify(x, 0, x.Length, onset, ChordLibrary.D, StrumVerifier.DefaultConfusers(ChordLibrary.D)).Clean);
        var y = KarplusStrong.Strum(ChordLibrary.Em, null, 20, 20, 3, sampleRate: 44100);
        Assert.Equal("Em", v.Verify(y, 0, y.Length, onset, ChordLibrary.G, StrumVerifier.DefaultConfusers(ChordLibrary.G)).BestMatch);
    }

    // ---- budget: CPU < 5 ms per onset, zero allocation after warm-up ----

    [Fact]
    public void Verify_AfterWarmup_AllocatesNothingAndUnder5Ms()
    {
        var v = new StrumVerifier(AudioConfig.Default, Sr);
        var conf = StrumVerifier.DefaultConfusers(ChordLibrary.C);
        var x = KarplusStrong.Strum(ChordLibrary.C, new[] { StrumMistake.Extra(6) }, 20, 10, 2);
        for (int i = 0; i < 5; i++) v.Verify(x, 0, x.Length, Onset, ChordLibrary.C, conf);
        const int n = 50;
        var sw = new Stopwatch();
        long allocated = AllocProbe.MinAllocatedBytes(() =>
        {
            sw.Restart();
            for (int i = 0; i < n; i++) v.Verify(x, 0, x.Length, Onset, ChordLibrary.C, conf);
            sw.Stop();
        });
        Assert.Equal(0, allocated);
        double ms = sw.Elapsed.TotalMilliseconds / n;
        Console.WriteLine($"[Synthetic] StrumVerifier ≈ {ms:F2} ms per onset ({v.HypothesisCount} hypotheses; budget < 5 ms)");
        Assert.True(ms < 5.0, $"{ms:F2} ms per onset");
    }

    // ---- feed: onset → strum check → sessions ----

    static float[] Train(params (double t, ChordShape c, StrumMistake[] m)[] strums)
    {
        double end = strums.Max(s => s.t) + 1.0;
        var x = new float[(int)(end * Sr)];
        int seed = 1;
        foreach (var s in strums)
        {
            var one = KarplusStrong.Strum(s.c, s.m, 25, double.PositiveInfinity, seed++);
            // Each strum restarts every string it plays (the pick stops the old vibration): fade the mix out 5 ms before.
            int at = (int)(s.t * Sr), fade = Sr / 200;
            for (int i = 0; i < fade && at - fade + i >= 0; i++) x[at - fade + i] *= 1f - (float)i / fade;
            for (int i = at; i < x.Length; i++) x[i] = 0f;
            Signals.MixInto(x, one, at - Onset, 1f);
        }
        Signals.AddNoise(x, 25, 77, 0, x.Length);
        return x;
    }

    static List<StrumEvent> Feed(StrumFeed f, float[] x, Func<double, ChordShape> expectedAt, List<double> readyAtSec = null, int chunk = 512)
    {
        var ev = new List<StrumEvent>();
        var confusers = ChordLibrary.All.ToDictionary(c => c.Name, c => StrumVerifier.DefaultConfusers(c));
        for (int i = 0; i < x.Length; i += chunk)
        {
            int n = Math.Min(chunk, x.Length - i);
            var exp = expectedAt((double)i / Sr);
            f.Process(x, i, n, exp, exp == null ? null : confusers[exp.Name]);
            while (f.TryRead(out var e)) { ev.Add(e); readyAtSec?.Add((double)(i + n) / Sr); }
        }
        return ev;
    }

    [Fact]
    public void StrumFeed_StrumTrain_OneEventPerStrumWithinLatencyBudget()
    {
        var times = new[] { 0.5, 1.5, 2.5, 3.5 };
        var chords = new[] { ChordLibrary.G, ChordLibrary.C, ChordLibrary.G, ChordLibrary.C };
        var x = Train(times.Select((t, i) => (t, chords[i], (StrumMistake[])null)).ToArray());
        var f = new StrumFeed(AudioConfig.Default, Sr, clockOriginSec: 100);
        var ready = new List<double>();
        var ev = Feed(f, x, t => t < 1.4 ? ChordLibrary.G : t < 2.4 ? ChordLibrary.C : t < 3.4 ? ChordLibrary.G : ChordLibrary.C, ready);
        Assert.Equal(4, ev.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(ev[i].TimeSec - 100 - times[i], -0.03, 0.03);
            Assert.True(ev[i].Result.Clean, ev[i].Result.ToString());
            Assert.Equal(chords[i].Name, ev[i].HeardChord);
            double latency = ready[i] - times[i];
            Assert.True(latency <= 0.35, $"strum {i}: verdict {latency * 1000:F0} ms after the strum");
        }
        Assert.Equal(0, f.Dropped);
    }

    [Fact]
    public void StrumFeed_NoExpectedChord_DropsOnsets()
    {
        var x = Train((0.5, ChordLibrary.G, null));
        var f = new StrumFeed(AudioConfig.Default, Sr);
        Assert.Empty(Feed(f, x, _ => null));
        Assert.True(f.Dropped >= 1);
    }

    [Fact]
    public void StrumFeed_Process_AllocatesNothingAfterWarmup()
    {
        var x = Train((0.5, ChordLibrary.G, null), (1.5, ChordLibrary.G, null));
        var f = new StrumFeed(AudioConfig.Default, Sr);
        var conf = StrumVerifier.DefaultConfusers(ChordLibrary.G);
        f.Process(x, 0, Sr, ChordLibrary.G, conf);
        while (f.TryRead(out _)) { }
        int events = 0;
        long allocated = AllocProbe.MinAllocatedBytes(() =>
        {
            f.Reset(0);
            f.Process(x, 0, Sr, ChordLibrary.G, conf);
            while (f.TryRead(out _)) { }
            events = 0;
            for (int i = Sr; i + 512 <= x.Length; i += 512) { f.Process(x, i, 512, ChordLibrary.G, conf); while (f.TryRead(out _)) events++; }
        });
        Assert.Equal(0, allocated);
        Assert.Equal(1, events);
    }

    [Fact]
    public void StrumRouting_ChangeDrillGtoC_CountsCleanChanges()
    {
        var times = new[] { 0.5, 2.0, 3.5, 5.0 };
        var chords = new[] { ChordLibrary.G, ChordLibrary.C, ChordLibrary.G, ChordLibrary.C };
        var x = Train(times.Select((t, i) => (t, chords[i], (StrumMistake[])null)).ToArray());
        var drill = new ChangeDrillSession("G", "C");
        var f = new StrumFeed(AudioConfig.Default, Sr);
        ChangeDrillState st = drill.State;
        // The drill alternates; the expectation is the drill's target.
        var target = ChordLibrary.G;
        var confusers = StrumVerifier.DefaultConfusers(ChordLibrary.G);
        for (int i = 0; i < x.Length; i += 512)
        {
            f.Process(x, i, Math.Min(512, x.Length - i), target, StrumVerifier.DefaultConfusers(target));
            while (f.TryRead(out var e))
            {
                st = StrumRouting.Feed(drill, e);
                target = ChordLibrary.Get(st.Target);
            }
        }
        Assert.Equal(3, st.CleanChanges);
        Assert.Equal(0, st.WrongStrums);
    }

    [Fact]
    public void StrumRouting_LearnChordWholeChord_FaultThenCleanMasters()
    {
        var s = new LearnChordSession(ChordLibrary.C, 0);
        double t = 0;
        while (s.Stage != LearnStage.WholeChord) s.Skip(t += 1);
        var x = Train((0.5, ChordLibrary.C, new[] { StrumMistake.Extra(6) }), (2.0, ChordLibrary.C, null));
        var f = new StrumFeed(AudioConfig.Default, Sr, clockOriginSec: 10);
        var states = new List<LearnChordState>();
        foreach (var e in Feed(f, x, _ => ChordLibrary.C)) states.Add(StrumRouting.Feed(s, e));
        Assert.Equal(2, states.Count);
        Assert.Equal(LearnStage.WholeChord, states[0].Stage);
        Assert.Equal("Skip string 6", states[0].LastTip);
        Assert.Equal(PluckVerdict.ExtraString, states[0].LastVerdict);
        Assert.Equal(1, states[0].Attempts);
        Assert.Equal(LearnStage.Mastered, states[1].Stage);
        Assert.True(s.LastStrum.HasValue && s.LastStrum.Value.Clean);
    }

    [Fact]
    public void LearnChord_OnStrumNothingHeard_ShowsTipWithoutAttempt()
    {
        var s = new LearnChordSession(ChordLibrary.D, 0);
        while (s.Stage != LearnStage.WholeChord) s.Skip(1);
        var r = Check(new float[Sr], ChordLibrary.D);
        var st = s.OnStrum(r, 2);
        Assert.Equal(0, st.Attempts);
        Assert.Equal(Tips.StrumAgain, st.LastTip);
        var stale = Check(KarplusStrong.Strum(ChordLibrary.G, null, 20, 20, 1), ChordLibrary.G);
        Assert.False(s.OnStrum(stale, 3).Advanced);    // checked against another chord: ignored
        Assert.Equal(LearnStage.WholeChord, s.Stage);
    }

    [Fact]
    public void StrumRouting_PlayAlong_PassesHeardChordAndTime()
    {
        var r = SongValidator.Load(SongFiles.Minimal);
        Assert.True(r.Ok);
        var pa = new PlayAlongSession(new UntitledGuitarApp.Core.Songs.SongTimeline(r.Song, 1.0));
        pa.Start(0);
        var x = Train((0.5, ChordLibrary.G, null));
        var f = new StrumFeed(AudioConfig.Default, Sr);
        var ev = Feed(f, x, _ => ChordLibrary.G);
        Assert.Single(ev);
        var target = StrumRouting.Feed(pa, ev[0]);
        Assert.False(target.Holding);       // wait mode released by the heard G
    }

    // ---- tips ----

    [Fact]
    public void ForStrum_EveryIssueTip_AtMostSevenWords()
    {
        foreach (var c in ChordLibrary.Must)
            foreach (var m in new[] { StrumMistake.Open(c.BassString), StrumMistake.Mute(3), StrumMistake.Extra(6) })
            {
                var r = Strum(c, m);
                string tip = Tips.ForStrum(r, c);
                if (r.Clean) Assert.Null(tip);
                else Assert.InRange(Tips.WordCount(tip), 1, Tips.MaxWords);
            }
        Assert.InRange(Tips.WordCount(Tips.StrumAgain), 1, Tips.MaxWords);
        Assert.InRange(Tips.WordCount(Tips.StrumTopFour), 1, Tips.MaxWords);
    }
}
