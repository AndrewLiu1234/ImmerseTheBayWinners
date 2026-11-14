using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using Xunit;

public class PatternJudgeTests
{
    static PatternClock Clock(string file, double scale, bool pick = false)
    {
        var song = SongFiles.Load(file);
        return new PatternClock(song, new SongTimeline(song, scale), pick);
    }

    [Theory]
    [InlineData(0.5)] [InlineData(0.6)] [InlineData(0.75)] [InlineData(0.9)] [InlineData(1.0)]
    public void SlotTimes_44_AreExactAtEveryPreset(double scale)
    {
        var song = SongFiles.Load("riptide-loop.song.json");
        var c = new PatternClock(song, new SongTimeline(song, scale), false);
        double slot = 60.0 / (song.TempoBpm * scale) * song.BeatsPerBar / song.Strum.Subdivision;
        Assert.Equal(slot, c.SlotSeconds, 12);
        Assert.True(c.Slots.Count > 0);
        for (int i = 0; i < c.Slots.Count; i++) Assert.Equal(i * slot, c.Slots[i].TimeSeconds, 9);
        Assert.Equal(song.Strum.Slots[3], c.Slots[3].Symbol);
    }

    [Theory]
    [InlineData(0.5)] [InlineData(0.75)] [InlineData(1.0)]
    public void SlotTimes_34WithPickup_StartAfterPickup(double scale)
    {
        var song = SongFiles.Load("happy-birthday.song.json");
        var c = new PatternClock(song, new SongTimeline(song, scale), false);
        double spb = 60.0 / (song.TempoBpm * scale);
        Assert.Equal(1 * spb, c.Slots[0].TimeSeconds, 9);             // pickup = 1 beat, bar 1 starts there
        Assert.Equal(1.5 * spb, c.Slots[1].TimeSeconds, 9);           // 6 slots per 3 beats
        Assert.Equal(1, c.Slots[0].Bar);
        Assert.Equal(2, c.Slots[6].Bar);
        Assert.Equal(-1, c.ActiveIndex(0.5 * spb));                   // inside the pickup: no slot
        Assert.Equal(0, c.ActiveIndex(1 * spb));
        Assert.Equal(1, c.ActiveIndex(1.6 * spb));
    }

    [Fact]
    public void Pick_BassSymbolResolvesToChordBassString()
    {
        var c = Clock("happy-birthday.song.json", 1.0, true);
        Assert.Equal("B", c.Slots[0].Symbol);
        Assert.Equal(6, c.Slots[0].String);            // G chord
        Assert.Equal(3, c.Slots[1].String);
        PatternClockSlot? onD = null;
        foreach (var s in c.Slots) if (s.Symbol == "B" && s.ChordIndex == 1) { onD = s; break; }
        Assert.True(onD.HasValue);
        Assert.Equal(4, onD.Value.String);             // D chord bass = string 4
    }

    [Fact]
    public void ActiveIndex_GarbageIsMinusOne()
    {
        var c = Clock("riptide-loop.song.json", 1.0);
        Assert.Equal(-1, c.ActiveIndex(double.NaN));
        Assert.Equal(-1, c.ActiveIndex(double.PositiveInfinity));
        Assert.Equal(-1, c.ActiveIndex(-3));
        Assert.Equal(-1, c.ActiveIndex(1e9));
    }

    static List<double> Perfect(PatternClock c, double shiftSec, int max = int.MaxValue)
    {
        var l = new List<double>();
        foreach (var s in c.Slots) if (!s.IsRest && l.Count < max) l.Add(s.TimeSeconds + shiftSec);
        return l;
    }

    [Fact]
    public void Judge_PerfectPlaying_IsSteadyAndAllOnTime()
    {
        var c = Clock("riptide-loop.song.json", 0.75);
        var r = new PatternJudge(c).Judge(Perfect(c, 0));
        Assert.Equal(r.ExpectedSlots, r.Hits.Count);
        Assert.Empty(r.MissedSlots); Assert.Equal(0, r.ExtraOnsets);
        Assert.Equal(100, r.OnTimePercent, 6); Assert.Equal("steady", r.FeedbackKey);
    }

    [Fact]
    public void Judge_LateAndEarly_Feedback()
    {
        var c = Clock("riptide-loop.song.json", 0.75);
        var late = new PatternJudge(c).Judge(Perfect(c, 0.08));
        Assert.Equal("dragging", late.FeedbackKey); Assert.Equal(80, late.MedianOffsetMs, 3); Assert.Equal(0, late.OnTimePercent);
        var early = new PatternJudge(c).Judge(Perfect(c, -0.08));
        Assert.Equal("rushing", early.FeedbackKey); Assert.True(early.MeanOffsetMs < 0);
    }

    [Fact]
    public void Judge_LatencyOffset_IsSubtracted()
    {
        var c = Clock("riptide-loop.song.json", 0.75);
        var r = new PatternJudge(c, new PatternJudgeConfig { LatencyOffsetMs = 80 }).Judge(Perfect(c, 0.08));
        Assert.Equal("steady", r.FeedbackKey); Assert.Equal(0, r.MedianOffsetMs, 3); Assert.Equal(100, r.OnTimePercent, 6);
    }

    [Fact]
    public void Judge_JitterWithinOnTime_StaysSteady()
    {
        var c = Clock("riptide-loop.song.json", 1.0);
        var on = Perfect(c, 0);
        for (int i = 0; i < on.Count; i++) on[i] += (i % 2 == 0 ? 0.02 : -0.02);
        var r = new PatternJudge(c).Judge(on);
        Assert.Equal(100, r.OnTimePercent, 6); Assert.Equal("steady", r.FeedbackKey);
    }

    [Fact]
    public void Judge_MissedSlotAndStrayOnset()
    {
        var c = Clock("riptide-loop.song.json", 1.0);
        var on = Perfect(c, 0);
        on.RemoveAt(2);
        on.Add(c.Slots[0].TimeSeconds + 0.5 * c.SlotSeconds); // halfway between slots: outside the capped window
        var r = new PatternJudge(c).Judge(on);
        Assert.Single(r.MissedSlots);
        Assert.Equal(1, r.ExtraOnsets);
        Assert.Equal(r.ExpectedSlots - 1, r.Hits.Count);
    }

    [Fact]
    public void Judge_DoubleOnsetOnOneSlot_CountsExtra()
    {
        var c = Clock("riptide-loop.song.json", 1.0);
        var on = Perfect(c, 0);
        on.Add(on[0] + 0.03);
        var r = new PatternJudge(c).Judge(on);
        Assert.Equal(1, r.ExtraOnsets); Assert.Equal(r.ExpectedSlots, r.Hits.Count);
    }

    [Fact]
    public void Judge_PartialRun_OnlyJudgesRequestedRange()
    {
        var c = Clock("riptide-loop.song.json", 1.0);
        var on = Perfect(c, 0, 4);
        var r = new PatternJudge(c).Judge(on, 0, on[3] + 0.01);
        Assert.Equal(4, r.ExpectedSlots); Assert.Empty(r.MissedSlots);
    }

    [Fact]
    public void Judge_GarbageNeverThrowsOrNaN()
    {
        var c = Clock("happy-birthday.song.json", 0.5);
        var j = new PatternJudge(c);
        foreach (var input in new List<double>[] {
            null, new List<double>(),
            new List<double> { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -5, 1e12 },
            new List<double> { 3, 1, 2, 2, 2 } })
        {
            var r = j.Judge(input);
            Assert.False(double.IsNaN(r.MeanOffsetMs) || double.IsNaN(r.MedianOffsetMs) || double.IsNaN(r.OnTimePercent)
                || double.IsNaN(r.MeanAbsOffsetMs));
        }
        var empty = j.Judge(new List<double>());
        Assert.Equal("notEnough", empty.FeedbackKey); Assert.Equal(empty.ExpectedSlots, empty.MissedSlots.Count);
    }
}
