using System;
using System.Collections.Generic;
using System.Text;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using Xunit;

// Hardening: timing at onset (not at last frame), wait-mode false skips, NaN/clock jumps, pause/resume, restart.
public class PlayAlongHardeningTests
{
    static SongTimeline Small()
    {
        var r = SongValidator.Load(SongFiles.Minimal);
        Assert.True(r.Ok);
        return new SongTimeline(r.Song, 1.0);
    }

    // 30 chords G/C, 2 s each at 60 bpm.
    static SongTimeline Thirty()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 30; i++) sb.Append(i > 0 ? "," : "").Append("{\"chord\":\"").Append(i % 2 == 0 ? "G" : "C").Append("\",\"beats\":2}");
        string json = "{\"schema\":1,\"id\":\"x\",\"title\":\"X\",\"license\":\"original\",\"tempoBpm\":60,\"timeSignature\":[4,4]," +
            "\"strum\":{\"subdivision\":2,\"slots\":[\"D\",\"-\"]},\"pick\":{\"subdivision\":2,\"slots\":[\"B\",\"1\"]}," +
            "\"sections\":[{\"name\":\"A\",\"lines\":[{\"lyric\":\"\",\"chords\":[" + sb + "]}]}]}";
        var r = SongValidator.Load(json);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        return new SongTimeline(r.Song, 1.0);
    }

    // Plays every chord exactly at its start; the strum reaches the session `delay` s later (60 fps frames).
    static PlayAlongSession PerfectRun(PlayAlongConfig cfg, double delay, bool stampOnset)
    {
        var tl = Thirty();
        var s = new PlayAlongSession(tl, cfg);
        s.Start(0);
        int next = 0;
        for (int f = 1; f < 60 * 70 && !s.Finished; f++)
        {
            double t = f / 60.0;
            s.Update(t);
            while (next < tl.Spans.Count && tl.Spans[next].StartSeconds + delay <= t + 1e-9)
            {
                double onset = tl.Spans[next].StartSeconds;
                s.OnStrum(tl.Spans[next].Chord, stampOnset ? onset : onset + delay);
                next++;
            }
        }
        return s;
    }

    [Fact]
    public void OnStrum_NonWaitPerfectRunDelivered300msLate_MeanErrorNearZero()
    {
        var s = PerfectRun(new PlayAlongConfig { WaitMode = false, LatencyOffsetMs = 300 }, 0.3, stampOnset: false);
        Assert.True(s.Finished);
        Assert.Equal(30, s.Hits);
        Assert.True(s.MeanAbsErrorSec < 0.03, $"mean {s.MeanAbsErrorSec}");
    }

    [Fact]
    public void OnStrum_NonWaitOnsetStampedButDeliveredLate_MeanErrorNearZero()
    {
        var s = PerfectRun(new PlayAlongConfig { WaitMode = false }, 0.3, stampOnset: true);
        Assert.Equal(30, s.Hits);
        Assert.True(s.MeanAbsErrorSec < 0.03, $"mean {s.MeanAbsErrorSec}");
    }

    [Fact]
    public void OnStrum_WaitModeHoldStrumDelivered300msLate_ErrorIsZero()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { LatencyOffsetMs = 300 });
        s.Start(0);
        s.OnStrum("G", 0.3);                 // played at 0.0, delivered at 0.3
        s.Update(100);
        Assert.Equal(0.0, s.Results[0].ErrorSec, 9);
    }

    [Fact]
    public void OnStrum_LateStrumJustBeforeNextChord_StillCountsForPreviousChord()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        s.Start(0);
        s.Update(4.1);                       // playhead is in C already (4 s)
        s.OnStrum("G", 3.95);                // G strum from 0.05 s before the change, delivered late
        s.Update(9);
        Assert.Equal(ChordOutcome.Hit, s.Results[0].Outcome);
    }

    [Fact]
    public void OnStrum_WaitModeSixEmptyOnsets_DoNotReleaseHold()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0);
        PlayAlongTarget t = default;
        for (int i = 0; i < 6; i++) t = s.OnStrum("", 0.2 * (i + 1));
        Assert.True(t.Holding);
        s.Update(7);
        Assert.Equal(0, s.Skipped);
    }

    [Fact]
    public void OnStrum_WaitModeNullChord_DoesNotCountAsWrong()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { MaxWrongStrums = 2 });
        s.Start(0);
        s.OnStrum(null, 1); s.OnStrum(null, 2); var t = s.OnStrum(null, 3);
        Assert.True(t.Holding);
    }

    [Fact]
    public void OnStrum_StandByMeHoldingOnEmWithSixGs_DoesNotSkipEm()
    {
        var song = SongFiles.Load("stand-by-me-loop.song.json");
        var tl = new SongTimeline(song, 0.75);
        var s = new PlayAlongSession(tl);
        s.Start(0); s.OnStrum("G", 0);
        var t = s.Update(tl.Spans[1].StartSeconds + 0.5);
        Assert.True(t.Holding); Assert.Equal("Em", t.Current);
        for (int i = 0; i < 6; i++) t = s.OnStrum("G", tl.Spans[1].StartSeconds + 0.6 + 0.1 * i);
        Assert.True(t.Holding);
        Assert.Equal("Em", t.Current);
    }

    [Fact]
    public void OnStrum_WaitModeSixOtherRecognisedChords_StillSkips()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0);
        PlayAlongTarget t = default;
        for (int i = 0; i < 6; i++) t = s.OnStrum("D", 0.2 * (i + 1));
        Assert.False(t.Holding);
    }

    [Fact]
    public void Update_NaNClock_TreatedAsLastNowAndPlayheadStillAdvances()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        s.Start(0); s.Update(1);
        var t = s.Update(double.NaN);
        Assert.True(double.IsFinite(t.PositionSeconds));
        t = s.Update(3);
        Assert.True(double.IsFinite(t.PositionSeconds));
        Assert.Equal(3.0, t.PositionSeconds, 9);
        s.Start(double.NaN); s.OnStrum("G", double.PositiveInfinity); s.Skip(double.NegativeInfinity);
        Assert.True(double.IsFinite(s.Update(1).PositionSeconds));
    }

    [Fact]
    public void Update_HugeClockJump_DoesNotFinishSong()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0);
        s.Update(12345.6);
        Assert.False(s.Finished);
    }

    [Fact]
    public void Update_TwentySecondStall_ChainsAtMostOneHoldTimeout()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { HoldTimeoutSec = 3 });
        s.Start(0);
        var t = s.Update(20);
        Assert.True(t.Holding);
        Assert.Equal(1, t.CurrentIndex);       // G timed out, C is held with a fresh timer
        Assert.False(s.Finished);
        s.Update(20.7);
        Assert.Equal(1, s.Skipped);
    }

    [Fact]
    public void PauseResume_FreezesPlayheadAndHoldTimer()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        s.Start(0); s.Update(1);
        s.Pause(1);
        var t = s.Update(30);
        Assert.Equal(1.0, t.PositionSeconds, 9);
        s.Resume(30);
        t = s.Update(31);
        Assert.Equal(2.0, t.PositionSeconds, 9);

        var h = new PlayAlongSession(Small(), new PlayAlongConfig { HoldTimeoutSec = 3 });
        h.Start(0); h.Update(2);
        h.Pause(2); h.Update(100); h.Resume(100);
        Assert.True(h.Update(100.5).Holding);
        Assert.Equal(0, h.Skipped + h.Results.Count);
        Assert.False(h.Update(103.5).Holding);
    }

    [Fact]
    public void OnStrum_WhilePaused_IsIgnored()
    {
        var s = new PlayAlongSession(Small());
        s.Start(0); s.Pause(0);
        Assert.True(s.OnStrum("G", 1).Holding);
    }

    [Fact]
    public void Start_RestartMidSong_ThenPerfectReplayIs30Of30()
    {
        var tl = Thirty();
        var s = new PlayAlongSession(tl, new PlayAlongConfig { WaitMode = false });
        s.Start(0);
        for (int f = 1; f < 60 * 20; f++) { s.Update(f / 60.0); }   // misses the first ten chords
        Assert.True(s.Missed > 0);
        double t0 = 100;
        s.Start(t0);
        Assert.Equal(0, s.Missed); Assert.Equal(0, s.Hits); Assert.Empty(s.Results);
        int next = 0;
        for (int f = 1; f < 60 * 70 && !s.Finished; f++)
        {
            double t = t0 + f / 60.0;
            s.Update(t);
            while (next < tl.Spans.Count && t0 + tl.Spans[next].StartSeconds <= t + 1e-9) { s.OnStrum(tl.Spans[next].Chord, t0 + tl.Spans[next].StartSeconds); next++; }
        }
        Assert.True(s.Finished);
        Assert.Equal(30, s.Hits);
    }

    [Fact]
    public void Start_AfterFinished_ClearsFinished()
    {
        var s = new PlayAlongSession(Small(), new PlayAlongConfig { WaitMode = false });
        s.Start(0); s.Update(9);
        Assert.True(s.Finished);
        s.Start(20);
        Assert.False(s.Finished);
    }
}
