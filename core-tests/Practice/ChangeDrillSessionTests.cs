using System;
using UntitledGuitarApp.Core.Practice;
using Xunit;

public class ChangeDrillSessionTests
{
    [Fact]
    public void FirstStrumOfA_StartsDrillAndTargetsB()
    {
        var s = new ChangeDrillSession("G", "C");
        Assert.Equal("G", s.State.Target);
        var st = s.OnStrum("G", 1);
        Assert.True(st.Started); Assert.Equal("C", st.Target); Assert.Equal(0, st.CleanChanges);
    }

    [Fact]
    public void QuickChanges_AreCleanAndAlternate()
    {
        var s = new ChangeDrillSession("G", "C");
        s.OnStrum("G", 0);
        var st = s.OnStrum("C", 2);
        Assert.Equal(ChangeOutcome.Clean, st.Outcome); Assert.Equal("G", st.Target);
        st = s.OnStrum("G", 4);
        Assert.Equal(2, st.CleanChanges); Assert.Equal(2, st.Streak); Assert.Equal("C", st.Target);
    }

    [Fact]
    public void RestrummingCurrentChord_IsIgnored()
    {
        var s = new ChangeDrillSession("Am", "G");
        s.OnStrum("Am", 0);
        var st = s.OnStrum("Am", 1);
        Assert.Equal(ChangeOutcome.None, st.Outcome); Assert.Equal(0, st.WrongStrums); Assert.Equal("G", st.Target);
    }

    [Fact]
    public void WrongChord_BreaksStreakButKeepsTarget()
    {
        var s = new ChangeDrillSession("G", "C");
        s.OnStrum("G", 0); s.OnStrum("C", 1); s.OnStrum("G", 2);
        var st = s.OnStrum("D", 3);
        Assert.Equal(ChangeOutcome.Wrong, st.Outcome); Assert.Equal(0, st.Streak); Assert.Equal(2, st.BestStreak);
        Assert.Equal("C", st.Target); Assert.Equal(1, st.WrongStrums);
    }

    [Fact]
    public void Silence_IsNotWrong()
    {
        var s = new ChangeDrillSession("G", "C");
        s.OnStrum("G", 0);
        var st = s.OnStrum(null, 1);
        Assert.Equal(0, st.WrongStrums); Assert.Equal(ChangeOutcome.None, st.Outcome);
    }

    [Fact]
    public void SlowChange_NotCleanAndTwoInARowEmitTip()
    {
        var s = new ChangeDrillSession("G", "C");
        s.OnStrum("G", 0);
        var st = s.OnStrum("C", 5);
        Assert.Equal(ChangeOutcome.Slow, st.Outcome); Assert.Equal(0, st.CleanChanges); Assert.Null(st.LateChangeKey);
        st = s.OnStrum("G", 10);
        Assert.Equal("lateChange", st.LateChangeKey); Assert.Equal(2, st.SlowChanges);
        st = s.OnStrum("C", 11);                       // clean again resets the run
        Assert.Null(st.LateChangeKey); Assert.Equal(1, st.CleanChanges);
    }

    [Fact]
    public void BestRate_IsMaxChangesInAny60sWindow_AndCurrentRateDecays()
    {
        var s = new ChangeDrillSession("G", "C");
        s.OnStrum("G", 0);
        ChangeDrillState st = default;
        for (int i = 1; i <= 10; i++) st = s.OnStrum(i % 2 == 1 ? "C" : "G", i * 2.0); // 10 changes in 20 s
        Assert.Equal(10, st.ChangesPerMinute, 6); Assert.Equal(10, st.BestChangesPerMinute, 6);
        st = s.OnStrum("C", 200);                      // target is C after 10 changes; 180 s gap is slow, not clean
        Assert.Equal(ChangeOutcome.Slow, st.Outcome);
        Assert.Equal(0, st.ChangesPerMinute, 6);       // old changes aged out of the 60 s window
        Assert.Equal(10, st.BestChangesPerMinute, 6);
    }

    [Fact]
    public void Garbage_NeverThrowsOrNaN()
    {
        var s = new ChangeDrillSession("G", "C");
        var st = s.OnStrum("G", double.NaN);
        st = s.OnStrum("C", double.PositiveInfinity);
        st = s.OnStrum("C", -5);
        st = s.OnStrum("", 1);
        Assert.False(double.IsNaN(st.ChangesPerMinute) || double.IsNaN(st.BestChangesPerMinute) || double.IsNaN(st.LastChangeSec));
        Assert.Throws<ArgumentException>(() => new ChangeDrillSession("G", "G"));
    }

    [Fact]
    public void BeforeStart_WrongChordCountsButNoStart()
    {
        var s = new ChangeDrillSession("G", "C");
        var st = s.OnStrum("C", 1);
        Assert.False(st.Started); Assert.Equal(1, st.WrongStrums);
    }
}
