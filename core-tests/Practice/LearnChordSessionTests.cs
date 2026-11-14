using System.Linq;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

public class LearnChordSessionTests
{
    static PluckResult R(int s, int expected, PluckVerdict v) =>
        new PluckResult(s, expected, 0, 0, v, PluckTipKeys.For(v, expected > Tuning.StandardMidi(s)), -1, 0, 0.9f, 0.1f, 0.8, true, 10, 15);

    static LearnChordState PluckActive(LearnChordSession s, PluckVerdict v, double t) =>
        s.OnPluck(R(s.ActiveString, s.ExpectedMidi, v), t);

    [Fact]
    public void Steps_G_FingersLowToHighThenAllStringsLowToHigh()
    {
        var s = new LearnChordSession(ChordLibrary.G, 0);
        Assert.Equal(new[] { 6, 5, 1 }, s.FingerSteps.Select(x => x.String));
        Assert.Equal(new[] { 2, 1, 3 }, s.FingerSteps.Select(x => x.Finger));
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, s.PluckSteps.Select(x => x.String));
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, new LearnChordSession(ChordLibrary.C, 0).PluckSteps.Select(x => x.String));
    }

    [Fact]
    public void Start_FingerStage_ActiveFingerAndString()
    {
        var st = new LearnChordSession(ChordLibrary.G, 0).State;
        Assert.Equal(LearnStage.FingerByFinger, st.Stage);
        Assert.Equal(2, st.ActiveFinger);
        Assert.Equal(6, st.ActiveString);
        Assert.Equal(43, st.ExpectedMidi);
        Assert.Equal(3, st.StepCount);
    }

    [Fact]
    public void OnPluck_Correct_AdvancesThroughAllStagesToWholeChord()
    {
        var s = new LearnChordSession(ChordLibrary.Em, 0);
        double t = 0;
        for (int i = 0; i < 2; i++) Assert.True(PluckActive(s, PluckVerdict.Correct, t += 1).Advanced);
        Assert.Equal(LearnStage.PluckCheck, s.Stage);
        Assert.Equal(6, s.ActiveString);
        Assert.Equal(0, s.ActiveFinger);   // open low E
        for (int i = 0; i < 6; i++) PluckActive(s, PluckVerdict.Correct, t += 1);
        Assert.Equal(LearnStage.WholeChord, s.Stage);
        Assert.False(s.ExpectsPluck);
        for (int str = 1; str <= 6; str++) Assert.Equal(PluckVerdict.Correct, s.VerdictFor(str));
    }

    [Fact]
    public void OnPluck_Fault_StaysWithTipAndCountsAttempt()
    {
        var s = new LearnChordSession(ChordLibrary.Em, 0);
        var st = PluckActive(s, PluckVerdict.OpenStringInstead, 1);
        Assert.False(st.Advanced);
        Assert.Equal(5, st.ActiveString);
        Assert.Equal(1, st.Attempts);
        Assert.Equal("Finger 2: press string 5, fret 2", st.LastTip);
        Assert.Equal(PluckTipKeys.PressFret, st.LastTipKey);
    }

    [Fact]
    public void OnPluck_PluckCheckFault_RecordsPerStringVerdictAndTip()
    {
        var s = new LearnChordSession(ChordLibrary.G, 0);
        for (int i = 0; i < 3; i++) PluckActive(s, PluckVerdict.Correct, i);
        for (int i = 0; i < 4; i++) PluckActive(s, PluckVerdict.Correct, 10 + i);   // strings 6..3
        Assert.Equal(2, s.ActiveString);
        PluckActive(s, PluckVerdict.Muted, 20);
        Assert.Equal(PluckVerdict.Muted, s.VerdictFor(2));
        Assert.Equal("String 2 muted — arch finger 3", s.TipFor(2));
        Assert.Null(s.VerdictFor(1));
        PluckActive(s, PluckVerdict.Correct, 21);
        Assert.Equal(PluckVerdict.Correct, s.VerdictFor(2));
        Assert.Null(s.TipFor(2));
    }

    [Fact]
    public void OnPluck_UnclearIsNotAnAttempt()
    {
        var s = new LearnChordSession(ChordLibrary.C, 0);
        var st = PluckActive(s, PluckVerdict.Unclear, 1);
        Assert.Equal(0, st.Attempts);
        Assert.Equal("Pluck string 5 again, slowly", st.LastTip);
    }

    [Fact]
    public void OnPluck_ShortRing_AdvancesFingerStageButNotPluckCheck()
    {
        var s = new LearnChordSession(ChordLibrary.Am, 0);
        Assert.True(PluckActive(s, PluckVerdict.ShortRing, 1).Advanced);
        PluckActive(s, PluckVerdict.Correct, 2); PluckActive(s, PluckVerdict.Correct, 3);
        Assert.Equal(LearnStage.PluckCheck, s.Stage);
        var st = PluckActive(s, PluckVerdict.ShortRing, 4);
        Assert.False(st.Advanced);
        Assert.Equal("Let string 5 ring", st.LastTip);
    }

    [Fact]
    public void OnPluck_OtherString_Ignored()
    {
        var s = new LearnChordSession(ChordLibrary.G, 0);
        var st = s.OnPluck(R(3, 55, PluckVerdict.Correct), 1);
        Assert.False(st.Advanced); Assert.False(st.HasLastVerdict);
        Assert.Equal(6, st.ActiveString);
    }

    [Fact]
    public void Stuck_AfterThreeFaultsOrTwentySeconds()
    {
        var s = new LearnChordSession(ChordLibrary.D, 100);
        for (int i = 0; i < 2; i++) Assert.False(PluckActive(s, PluckVerdict.WrongFretLow, 101 + i).Stuck);
        Assert.True(PluckActive(s, PluckVerdict.WrongFretLow, 103).Stuck);
        var t = new LearnChordSession(ChordLibrary.D, 100);
        Assert.False(t.Tick(119).Stuck);
        Assert.True(t.Tick(120.5).Stuck);
        Assert.Equal(20.5, t.State.ElapsedSec, 9);
    }

    [Fact]
    public void Skip_MovesOnAndResetsStuck()
    {
        var s = new LearnChordSession(ChordLibrary.D, 0);
        s.Tick(30);
        var st = s.Skip(30);
        Assert.True(st.Advanced); Assert.False(st.Stuck);
        Assert.Equal(1, st.StepIndex);
    }

    [Fact]
    public void OnStrumVerdict_CleanInWholeChord_Mastered()
    {
        var s = new LearnChordSession(ChordLibrary.Em, 0);
        Assert.False(s.OnStrumVerdict(true, 1).Advanced);   // not in WholeChord yet: ignored
        for (int i = 0; i < 8; i++) PluckActive(s, PluckVerdict.Correct, 2 + i);
        var bad = s.OnStrumVerdict(false, 11, "Heard G — target is Em", "wrongChord");
        Assert.Equal(LearnStage.WholeChord, bad.Stage); Assert.Equal("Heard G — target is Em", bad.LastTip);
        var ok = s.OnStrumVerdict(true, 12);
        Assert.Equal(LearnStage.Mastered, ok.Stage);
        Assert.Equal(0, ok.ActiveString);
    }

    [Fact]
    public void Clock_GoingBackwardsOrNaN_Clamped()
    {
        var s = new LearnChordSession(ChordLibrary.G, 10);
        s.Tick(15);
        Assert.Equal(5, s.Tick(12).ElapsedSec, 9);
        Assert.Equal(5, s.Tick(double.NaN).ElapsedSec, 9);
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public void Session_WithPluckVerifierOnSyntheticAudio_ReachesWholeChordAndCatchesMutedB()
    {
        // Integration: verifier → session. G chord; the B string is first plucked deadened (fault), then correctly.
        var v = new PluckVerifier(AudioConfig.Default, PluckScenes.Sr);
        var s = new LearnChordSession(ChordLibrary.G, 0);
        double t = 0; int seed = 1;
        bool mutedOnce = false;
        while (s.ExpectsPluck && t < 100)
        {
            int str = s.ActiveString, m = s.ExpectedMidi;
            bool fault = s.Stage == LearnStage.PluckCheck && str == 2 && !mutedOnce;
            var x = PluckScenes.Scene(m, fault ? PluckScenes.MutedT60 : PluckScenes.FreeT60, seed++, snrDb: 20);
            var r = v.Verify(x, 0, x.Length, PluckScenes.OnsetIndex, str, m, ChordLibrary.G);
            var st = s.OnPluck(r, t += 2);
            if (fault)
            {
                mutedOnce = true;
                Assert.Equal(PluckVerdict.Muted, st.LastVerdict);
                Assert.Equal("String 2 muted — arch finger 3", st.LastTip);
            }
        }
        Assert.True(mutedOnce);
        Assert.Equal(LearnStage.WholeChord, s.Stage);
    }
}
