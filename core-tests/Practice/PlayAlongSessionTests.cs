using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using Xunit;

public class PlayAlongSessionTests
{
    // Minimal song: G 4 beats, C 4 beats at 60 bpm ×1 → 1 s per beat; G 0-4 s, C 4-8 s.
    static SongTimeline Tl() => new SongTimeline(SongFiles.Load("riptide-loop.song.json"), 1.0);
    static SongTimeline Small()
    {
        var r = SongValidator.Load(SongFiles.Minimal);
        Assert.True(r.Ok);
        return new SongTimeline(r.Song, 1.0);
    }

    [Fact]
    public void WaitMode_HoldsAtStartUntilFirstChord()
    {
        var s = new PlayAlongSession(Small());
        var t = s.Start(0);
        Assert.True(t.Holding); Assert.Equal("G", t.Current); Assert.Equal("C", t.Next);
        t = s.Update(5);
        Assert.True(t.Holding); Assert.Equal(0, t.PositionSeconds);
        t = s.OnStrum("G", 5);
        Assert.False(t.Holding);
        t = s.Update(6);
        Assert.Equal(1.0, t.PositionSeconds, 9);
        Assert.Equal(3.0, t.BeatsToChange, 9);
    }

    [Fact]
    public void WaitMode_HoldsAtChordChangeUntilRightChord()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0); s.OnStrum("G", 0);
        var t = s.Update(10);                    // reaches 4 s of song at t=4, then holds
        Assert.True(t.Holding); Assert.Equal("C", t.Current); Assert.Equal(4.0, t.PositionSeconds, 9);
        t = s.OnStrum("G", 11);                  // wrong chord: still holding
        Assert.True(t.Holding);
        t = s.OnStrum("C", 12);
        Assert.False(t.Holding);
        Assert.Equal(4.0, t.PositionSeconds, 9);
        t = s.Update(14);
        Assert.Equal(6.0, t.PositionSeconds, 9);
    }

    [Fact]
    public void WaitMode_EscapeHatch_TimeoutAutoSkips()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { HoldTimeoutSec = 3 });
        s.Start(0);
        var t = s.Update(3.5);                   // held on G 3 s → skipped at 3, then runs 0.5 s
        Assert.False(t.Holding);
        Assert.Equal(0.5, t.PositionSeconds, 9);
        Assert.Equal(ChordOutcome.Pending, s.Results.Count == 0 ? ChordOutcome.Pending : s.Results[0].Outcome);
        s.Update(100);                           // holds at C, times out, finishes
        Assert.True(s.Finished);
        Assert.Equal(2, s.Skipped);
    }

    [Fact]
    public void WaitMode_EscapeHatch_TooManyWrongStrumsSkips()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { MaxWrongStrums = 2 });
        s.Start(0);
        s.OnStrum("D", 1);
        var t = s.OnStrum("D", 2);
        Assert.False(t.Holding);
        s.Update(100);
        Assert.Equal(ChordOutcome.Skipped, s.Results[0].Outcome);
    }

    [Fact]
    public void WaitMode_ManualSkip_Continues()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0);
        var t = s.Skip(0);
        Assert.False(t.Holding);
    }

    [Fact]
    public void NonWait_PlayheadRunsAndMissesAreCounted()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        var t = s.Start(0);
        Assert.False(t.Holding); Assert.Equal("G", t.Current);
        t = s.Update(4.5);
        Assert.Equal("C", t.Current); Assert.Equal(3.5, t.BeatsToChange, 9);
        s.Update(9);
        Assert.True(s.Finished);
        Assert.Equal(2, s.Missed);
        Assert.Equal(0, s.Hits);
    }

    [Fact]
    public void NonWait_BestOnsetWins_AndEarlyStrumCounts()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        s.Start(0);
        s.OnStrum("G", 0.6);                     // late 0.6
        s.OnStrum("G", 2.0);                     // worse
        s.OnStrum("C", 3.8);                     // 0.2 early for C: inside the early window
        s.Update(9);
        Assert.Equal(ChordOutcome.Hit, s.Results[0].Outcome);
        Assert.Equal(0.6, s.Results[0].ErrorSec, 9);
        Assert.Equal(ChordOutcome.Hit, s.Results[1].Outcome);
        Assert.Equal(-0.2, s.Results[1].ErrorSec, 9);
        Assert.Equal(0.4, s.MeanAbsErrorSec, 9);
    }

    [Fact]
    public void NonWait_StrumOutsideWindow_DoesNotCount()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        s.Start(0);
        s.OnStrum("C", 1.0);                     // C is 3 s away; early window is 0.3
        s.Update(9);
        Assert.Equal(ChordOutcome.Missed, s.Results[1].Outcome);
    }

    [Fact]
    public void WaitMode_EarlyStrum_PreSatisfiesTheNextChord()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0); s.OnStrum("G", 0);
        s.OnStrum("C", 3.9);                     // 0.1 s before the change
        var t = s.Update(4.5);
        Assert.False(t.Holding);
        Assert.Equal(4.5, t.PositionSeconds, 9);
        s.Update(100);
        Assert.Equal(-0.1, s.Results[1].ErrorSec, 9);
    }

    [Fact]
    public void WaitMode_HoldLateness_IsTheTimingError()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0); s.OnStrum("G", 0);
        s.Update(4);                             // hold on C starts at clock 4
        s.OnStrum("C", 5.5);
        s.Update(100);
        Assert.Equal(1.5, s.Results[1].ErrorSec, 9);
    }

    [Fact]
    public void FullRunAtEveryPreset_Completes()
    {
        foreach (var scale in SongFiles.Presets())
        {
            var song = SongFiles.Load("happy-birthday.song.json");
            var tl = new SongTimeline(song, scale);
            var s = new PlayAlongSession(tl);
            double now = 0;
            var t = s.Start(now);
            for (int step = 0; step < 100000 && !s.Finished; step++)
            {
                now += 0.05;
                t = s.Update(now);
                // a simulated player strums each chord 0.1 s into it (or as soon as the playhead holds on it)
                if (!s.Finished && (t.Holding || t.PositionSeconds >= tl.Spans[t.CurrentIndex].StartSeconds + 0.1) && s.Results.Count <= t.CurrentIndex)
                    s.OnStrum(t.Current, now);
            }
            Assert.True(s.Finished);
            Assert.Equal(tl.Spans.Count, s.Results.Count);
            Assert.Equal(tl.Spans.Count, s.Hits);
        }
    }

    [Fact]
    public void Garbage_NeverThrows()
    {
        var s = new PlayAlongSession(Small());
        s.OnStrum(null, 1); s.OnStrum("", 0.5); s.Update(double.MaxValue / 4); s.OnStrum("G", -3);
        Assert.True(true);
    }
}
