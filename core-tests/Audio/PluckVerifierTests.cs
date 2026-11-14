using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;
using Xunit.Abstractions;

// SL2 Core: pluck check. Synthetic (Karplus–Strong) correctness only — accuracy claims come from the recorded eval set.
[Trait("Category", "Synthetic")]
public class PluckVerifierTests
{
    const int Sr = PluckScenes.Sr;
    readonly ITestOutputHelper _out;
    public PluckVerifierTests(ITestOutputHelper output) { _out = output; }

    static PluckVerifier NewVerifier() => new PluckVerifier(AudioConfig.Default, Sr);

    static PluckResult Check(float[] x, int s, int expected, ChordShape chord = null, PluckVerifier v = null) =>
        (v ?? NewVerifier()).Verify(x, 0, x.Length, PluckScenes.OnsetIndex, s, expected, chord);

    static PluckResult Play(int s, int expected, int played, double t60 = PluckScenes.FreeT60, ChordShape chord = null,
                            double snr = double.PositiveInfinity, int seed = 1) =>
        Check(PluckScenes.Scene(played, t60, seed, snr), s, expected, chord);

    // ---- every MUST chord's strings, clean ----

    [Fact]
    public void Verify_EveryMustChordString_Correct()
    {
        var v = NewVerifier();
        foreach (var chord in ChordLibrary.Must)
            for (int s = 6; s >= 1; s--)
            {
                if (!chord.IsPlayed(s)) continue;
                int m = chord.MidiOn(s);
                var r = Check(PluckScenes.Scene(m, PluckScenes.FreeT60, seed: s), s, m, chord, v);
                Assert.True(r.Verdict == PluckVerdict.Correct, $"{chord} s{s}: {r}");
                Assert.InRange(r.Cents, -5, 5);
                Assert.True(r.RingJudged);
                Assert.Null(r.TipKey);
            }
    }

    // ---- each fault class from Appendix B2 ----

    [Fact]
    public void Verify_EmFinger2Missing_OpenStringInstead()
    {
        var r = Play(5, 47, 45, chord: ChordLibrary.Em);   // A2 instead of B2
        Assert.Equal(PluckVerdict.OpenStringInstead, r.Verdict);
        Assert.Equal(45, r.HeardMidi);
        Assert.InRange(r.Cents, -205, -195);
        Assert.Equal(PluckTipKeys.PressFret, r.TipKey);
    }

    [Fact]
    public void Verify_FretOneTarget_OpenBeatsWrongFretLow()
    {
        // C chord string 2 = C4 at fret 1: the open B3 is also "−1 semitone"; §19 precedence says OpenStringInstead.
        var r = Play(2, 60, 59, chord: ChordLibrary.C);
        Assert.Equal(PluckVerdict.OpenStringInstead, r.Verdict);
    }

    [Fact]
    public void Verify_DChordBAtFret2_WrongFretLow()
    {
        var r = Play(2, 62, 61, chord: ChordLibrary.D);   // C#4 instead of D4 (B2 table: "B at fret 2")
        Assert.Equal(PluckVerdict.WrongFretLow, r.Verdict);
        Assert.Equal(PluckTipKeys.FretHigher, r.TipKey);
    }

    [Fact]
    public void Verify_OneFretTooHigh_WrongFretHigh()
    {
        var r = Play(4, 52, 53, chord: ChordLibrary.Em);  // F3 instead of E3
        Assert.Equal(PluckVerdict.WrongFretHigh, r.Verdict);
        Assert.Equal(PluckTipKeys.FretLower, r.TipKey);
    }

    [Fact]
    public void Verify_NeighbourStringPlucked_WrongString()
    {
        var r = Play(3, 55, 50, chord: ChordLibrary.G);   // expected G3 on string 3, the D3 of string 4 rang
        Assert.Equal(PluckVerdict.WrongString, r.Verdict);
        Assert.Equal(4, r.HeardString);
    }

    [Fact]
    public void Verify_GChordBStringMuted_MutedArchFinger()
    {
        var r = Play(2, 59, 59, PluckScenes.MutedT60, ChordLibrary.G);
        Assert.Equal(PluckVerdict.Muted, r.Verdict);
        Assert.Equal(PluckTipKeys.ArchFinger, r.TipKey);   // B is open in G → a neighbour finger is leaning on it
        Assert.Equal(0, r.DetectedHz);
    }

    [Fact]
    public void Verify_AmFrettedBMuted_MutedPressFingertip()
    {
        var r = Play(2, 60, 60, PluckScenes.MutedT60, ChordLibrary.Am);
        Assert.Equal(PluckVerdict.Muted, r.Verdict);
        Assert.Equal(PluckTipKeys.PressFingertip, r.TipKey);
    }

    [Fact]
    public void Verify_FingerLeaningOnString_ShortRing()
    {
        var r = Play(1, 67, 67, PluckScenes.ShortT60, ChordLibrary.G);
        Assert.Equal(PluckVerdict.ShortRing, r.Verdict);
        Assert.True(r.RingJudged);
        Assert.True(r.RingSec < 0.8, r.ToString());
        Assert.Equal(PluckTipKeys.LetRing, r.TipKey);
    }

    [Fact]
    public void Verify_ShouldBeSilentStringRings_ExtraString()
    {
        var r = Play(6, ChordShape.Muted, 40, chord: ChordLibrary.C);   // low E strummed on C
        Assert.Equal(PluckVerdict.ExtraString, r.Verdict);
        Assert.Equal(PluckTipKeys.SkipString, r.TipKey);
    }

    [Fact]
    public void Verify_ShouldBeSilentStringSilent_Correct()
    {
        var r = Check(new float[Sr], 6, ChordShape.Muted, ChordLibrary.C);
        Assert.Equal(PluckVerdict.Correct, r.Verdict);
    }

    // ---- robustness ----

    [Fact]
    public void Verify_Silence_UnclearNoNaN()
    {
        var r = Check(new float[Sr * 2], 3, 55);
        Assert.Equal(PluckVerdict.Unclear, r.Verdict);
        Assert.Equal(0, r.DetectedHz); Assert.Equal(0, r.Cents);
    }

    [Fact]
    public void Verify_SteadyNoiseNoPluck_Unclear()
    {
        var r = Check(PluckScenes.Scene(-1, 1, seed: 4, snrDb: 10), 3, 55);
        Assert.Equal(PluckVerdict.Unclear, r.Verdict);
    }

    [Fact]
    public void Verify_NaNAndTinyWindows_UnclearNeverThrows()
    {
        var v = NewVerifier();
        var x = PluckScenes.Scene(55, 4, 1); x[20000] = float.NaN;
        Assert.Equal(PluckVerdict.Unclear, v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 3, 55).Verdict);
        x[20000] = float.PositiveInfinity;
        Assert.Equal(PluckVerdict.Unclear, v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 3, 55).Verdict);
        Assert.Equal(PluckVerdict.Unclear, v.Verify(new float[0], 0, 0, 0, 3, 55).Verdict);
        Assert.Equal(PluckVerdict.Unclear, v.Verify(new float[100], 0, 100, 50, 3, 55).Verdict);
        var y = PluckScenes.Scene(55, 4, 1);
        Assert.Equal(PluckVerdict.Unclear, v.Verify(y, 0, y.Length, y.Length + 10, 3, 55).Verdict);   // onset past the end
    }

    [Fact]
    public void Verify_PreviousStringStillRingingThenMutedPluck_MutedNotWrongString()
    {
        // Finger by finger: string 3 still rings when the player plucks a deadened string 2. A pitch-only check would see
        // G3 = the neighbour's note and say WrongString; the onset-relative rise says nothing new rang.
        var x = PluckScenes.Scene(59, PluckScenes.MutedT60, seed: 3, ringingMidi: 55, ringingAgoSec: 0.5);
        var r = Check(x, 2, 59, ChordLibrary.G);
        Assert.Equal(PluckVerdict.Muted, r.Verdict);
    }

    [Fact]
    public void Verify_PreviousStringStillRingingThenGoodPluck_Correct()
    {
        var x = PluckScenes.Scene(59, PluckScenes.FreeT60, seed: 3, ringingMidi: 55, ringingAgoSec: 0.5);
        var r = Check(x, 2, 59, ChordLibrary.G);
        Assert.True(r.Verdict == PluckVerdict.Correct, r.ToString());
    }

    [Fact]
    public void Verify_WeakFundamentalLowE_CorrectNotOctaveFault()
    {
        // Laptop mics barely pass 82 Hz: fundamental at −26 dB vs the 2nd partial. Must not read E3 (= "wrong string" bait).
        var tone = Signals.Harmonic(Midi.ToHz(40), new[] { 0.05, 1.0, 0.6, 0.4, 0.3, 0.2 }, 1000, Sr, t60Sec: 4);
        var x = new float[PluckScenes.OnsetIndex + tone.Length];
        Signals.MixInto(x, tone, PluckScenes.OnsetIndex, 0.3f);
        var r = Check(x, 6, 40, ChordLibrary.Em);
        Assert.Equal(PluckVerdict.Correct, r.Verdict);
    }

    [Fact]
    public void Verify_FreeNoteDampedAfter300ms_ShortRing()
    {
        // A free string that a finger stops after 0.3 s: the decay fit alone would say "rings 2 s"; the cut check catches it.
        var x = PluckScenes.Scene(55, PluckScenes.FreeT60, seed: 6);
        int cut = PluckScenes.OnsetIndex + (int)(0.3 * Sr);
        for (int i = cut; i < x.Length; i++) x[i] *= (float)Math.Exp(-(i - cut) / (0.005 * Sr));
        Signals.AddNoise(x, 40, 77, PluckScenes.OnsetIndex, PluckScenes.OnsetIndex + Sr / 4);
        var r = Check(x, 3, 55, ChordLibrary.G);
        Assert.True(r.Verdict == PluckVerdict.ShortRing, r.ToString());
        Assert.InRange(r.RingSec, 0.2, 0.4);
    }

    [Theory]
    [InlineData(25.0)] [InlineData(-25.0)] [InlineData(40.0)] [InlineData(-40.0)]
    public void Verify_SlightlyOutOfTuneString_StillCorrect(double detune)
    {
        var p = KarplusStrong.Pluck(50, 1000, Sr, seed: 8, detuneCents: detune);
        var x = new float[PluckScenes.OnsetIndex + p.Length];
        Signals.MixInto(x, p, PluckScenes.OnsetIndex, 1f);
        var r = Check(x, 4, 50, ChordLibrary.G);
        Assert.Equal(PluckVerdict.Correct, r.Verdict);
        Assert.InRange(r.Cents, detune - 3, detune + 3);
    }

    [Fact]
    public void Verify_ShouldBeSilentStringRingsInNoise_ExtraString()
    {
        var r = Play(5, ChordShape.Muted, 45, chord: ChordLibrary.D, snr: 10);   // A string strummed on D
        Assert.Equal(PluckVerdict.ExtraString, r.Verdict);
    }

    [Fact]
    public void Verify_WindowTooShortForRing_PitchVerdictRingNotJudged()
    {
        var x = PluckScenes.Scene(55, PluckScenes.ShortT60, seed: 2, postSec: 0.4);
        var r = Check(x, 3, 55, ChordLibrary.G);
        Assert.Equal(PluckVerdict.Correct, r.Verdict);
        Assert.False(r.RingJudged);
    }

    [Fact]
    public void Verify_DraftContractOverload_MatchesOffsetOverload()
    {
        var x = PluckScenes.Scene(47, PluckScenes.FreeT60, seed: 9);
        var a = NewVerifier().Verify(x, Sr, PluckScenes.OnsetIndex, 5, 47);
        var b = NewVerifier().Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 5, 47);
        Assert.Equal(a.Verdict, b.Verdict); Assert.Equal(a.DetectedHz, b.DetectedHz);
        Assert.Throws<ArgumentException>(() => NewVerifier().Verify(x, 44100, PluckScenes.OnsetIndex, 5, 47));
    }

    [Fact]
    public void Verify_OffsetWindow_SameAsCopiedWindow()
    {
        var x = PluckScenes.Scene(50, PluckScenes.FreeT60, seed: 5, snrDb: 20);
        var padded = new float[x.Length + 3000];
        Array.Copy(x, 0, padded, 1234, x.Length);
        var a = Check(x, 4, 50);
        var b = NewVerifier().Verify(padded, 1234, x.Length, PluckScenes.OnsetIndex, 4, 50);
        Assert.Equal(a.Verdict, b.Verdict); Assert.Equal(a.DetectedHz, b.DetectedHz, 9);
    }

    [Fact]
    public void Verify_SameInput_Deterministic()
    {
        var x = PluckScenes.Scene(57, PluckScenes.FreeT60, seed: 5, snrDb: 10);
        var v = NewVerifier();
        var a = v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 3, 57, ChordLibrary.Am);
        var b = v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 3, 57, ChordLibrary.Am);
        Assert.Equal(a.Verdict, b.Verdict); Assert.Equal(a.DetectedHz, b.DetectedHz); Assert.Equal(a.RingSec, b.RingSec);
    }

    [Fact]
    public void Verify_AfterWarmup_AllocatesNothing()
    {
        var x = PluckScenes.Scene(55, PluckScenes.FreeT60, seed: 5, snrDb: 20, ringingMidi: 50);
        var v = NewVerifier();
        v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 3, 55, ChordLibrary.G);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++) v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 3, 55, ChordLibrary.G);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Verify_Cost_ReportedPerCall()
    {
        var x = PluckScenes.Scene(43, PluckScenes.FreeT60, seed: 5, snrDb: 20);
        var v = NewVerifier();
        v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 6, 43);
        var sw = Stopwatch.StartNew();
        const int n = 20;
        for (int i = 0; i < n; i++) v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, 6, 43);
        double ms = sw.Elapsed.TotalMilliseconds / n;
        Console.WriteLine($"[Synthetic] PluckVerifier.Verify ≈ {ms:F2} ms per pluck (once per onset, not per hop)");
        Assert.True(ms < 50, $"{ms:F1} ms per Verify");
    }

    // ---- the ≥ 95 % grid ----

    static PluckVerdict ExpectedVerdict(PluckFault f, int s, int expected, int playedMidi)
    {
        int open = Tuning.StandardMidi(s);
        switch (f)
        {
            case PluckFault.Correct: return PluckVerdict.Correct;
            case PluckFault.Muted: return PluckVerdict.Muted;
            case PluckFault.ShortRing: return PluckVerdict.ShortRing;
            case PluckFault.Silence: return PluckVerdict.Unclear;
        }
        // §19 precedence by the note actually sounded: open > ±1/±2 semitones on the string > neighbour's note.
        if (playedMidi == expected) return PluckVerdict.Correct;
        if (playedMidi == open && expected > open) return PluckVerdict.OpenStringInstead;
        int d = playedMidi - expected;
        if (d >= -2 && d <= 2 && playedMidi >= open) return d < 0 ? PluckVerdict.WrongFretLow : PluckVerdict.WrongFretHigh;
        return PluckVerdict.WrongString;
    }

    [Fact]
    public void Verify_SyntheticGrid_AtLeast95PercentPerSnr()
    {
        var v = NewVerifier();
        // 5 dB is reported, not gated (§19 lists it for strums; a pluck check in a room that loud is out of scope).
        var snrs = new[] { double.PositiveInfinity, 20.0, 10.0, 5.0 };
        var names = new[] { "clean", "20 dB", "10 dB", "5 dB" };
        const int gated = 3;
        var faults = (PluckFault[])Enum.GetValues(typeof(PluckFault));
        var summary = new List<string>();
        var accs = new List<double>();
        for (int si = 0; si < snrs.Length; si++)
        {
            int ok = 0, total = 0;
            var perClass = new Dictionary<PluckVerdict, (int ok, int n)>();
            var misses = new List<string>();
            foreach (var chord in ChordLibrary.Must)
                for (int s = 6; s >= 1; s--)
                {
                    if (!chord.IsPlayed(s)) continue;
                    int m = chord.MidiOn(s), open = Tuning.StandardMidi(s);
                    foreach (var fault in faults)
                    {
                        if (fault == PluckFault.OpenString && m == open) continue;
                        if (fault == PluckFault.FretLow && m - 1 <= open) continue;
                        for (int nb = 0; nb < 2; nb++)
                        {
                            int neighbour = nb == 0 ? s + 1 : s - 1;
                            if (fault != PluckFault.NeighbourString && nb == 1) break;
                            if (fault == PluckFault.NeighbourString && (neighbour < 1 || neighbour > 6)) continue;
                            int played = PluckScenes.FaultMidi(fault, s, m, chord, neighbour);
                            if (fault == PluckFault.NeighbourString && played == m) continue;
                            var want = ExpectedVerdict(fault, s, m, played);
                            for (int seed = 1; seed <= 2; seed++)
                            {
                                var x = PluckScenes.Scene(played, PluckScenes.T60For(fault), seed * 100 + s * 10 + (int)fault, snrs[si]);
                                var r = v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, s, m, chord);
                                bool hit = r.Verdict == want;
                                total++; if (hit) ok++;
                                var pc = perClass.TryGetValue(want, out var c) ? c : (ok: 0, n: 0);
                                perClass[want] = (pc.ok + (hit ? 1 : 0), pc.n + 1);
                                if (!hit) misses.Add($"{chord} s{s} {fault} seed{seed}: want {want}, got {r}");
                            }
                        }
                    }
                }
            double acc = 100.0 * ok / total;
            string cls = string.Join(" · ", perClass.OrderBy(k => (int)k.Key).Select(k => $"{k.Key} {k.Value.ok}/{k.Value.n}"));
            summary.Add($"{names[si],-6} {acc,5:F1}% ({ok}/{total})  {cls}");
            accs.Add(acc);
            foreach (var mline in misses.Take(12)) _out.WriteLine("  miss " + mline);
            Console.WriteLine($"[Synthetic] pluck grid {names[si]}: {acc:F1}% ({ok}/{total})");
            foreach (var mline in misses.Take(12)) Console.WriteLine("[Synthetic]   miss " + mline);
        }
        foreach (var line in summary) { _out.WriteLine(line); Console.WriteLine("[Synthetic] " + line); }
        for (int i = 0; i < gated; i++)
            Assert.True(accs[i] >= 95.0, "pluck verdict accuracy below 95%: " + summary[i]);
    }
}
