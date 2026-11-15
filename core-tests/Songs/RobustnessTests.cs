using System;
using System.Linq;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using Xunit;

// Hardening: degenerate input must never throw, hang or yield int.MinValue/NaN.
public class RobustnessTests
{
    [Theory]
    [InlineData("\"schema\":99999999999,\"id\":\"x\",\"title\":\"X\",\"license\":\"original\",\"tempoBpm\":60,\"timeSignature\":[4,4],\"sections\":[]")]
    [InlineData("\"schema\":1,\"id\":\"x\",\"title\":\"X\",\"license\":\"original\",\"tempoBpm\":60,\"timeSignature\":[99999999999,4],\"sections\":[]")]
    [InlineData("\"schema\":1,\"id\":\"x\",\"title\":\"X\",\"license\":\"original\",\"tempoBpm\":60,\"timeSignature\":[4,4],\"strum\":{\"subdivision\":99999999999,\"slots\":[]},\"sections\":[{\"lines\":[{\"chords\":[{\"chord\":\"G\",\"beats\":4,\"at\":99999999999}]}]}]")]
    [InlineData("\"schema\":1.5e400,\"tempoBpm\":1e999")]
    public void Parse_HugeIntegers_NeverThrowsAndReportsErrors(string body)
    {
        var r = SongParser.Parse("{" + body + "}");
        Assert.NotEmpty(r.Errors);
        var v = SongValidator.Load("{" + body + "}");
        Assert.False(v.Ok);
    }

    static Song Degenerate(double bpm, int bar, double pickup)
    {
        var s = new Song { Schema = 1, Id = "x", Title = "x", TempoBpm = bpm, BeatsPerBar = bar, BeatUnit = 4, PickupBeats = pickup };
        var line = new SongLine();
        line.Chords.Add(new SongChord { Chord = "G", Beats = 4, At = -1 });
        var sec = new SongSection { Name = "A" };
        sec.Lines.Add(line);
        s.Sections.Add(sec);
        return s;
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(60, 0, 0)]
    [InlineData(60, 4, double.NaN)]
    [InlineData(double.PositiveInfinity, 4, 0)]
    public void At_DegenerateSong_NeverReturnsMinValueOrNaN(double bpm, int bar, double pickup)
    {
        var tl = new SongTimeline(Degenerate(bpm, bar, pickup), 1.0);
        foreach (var t in new[] { -5.0, 0.0, 1.0, 100.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var q = tl.At(t);
            Assert.NotEqual(int.MinValue, q.Bar);
            Assert.NotEqual(int.MinValue, q.BeatInBar);
            Assert.False(double.IsNaN(q.Beat) || double.IsNaN(q.BeatsUntilChange));
        }
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(60, 0)]
    [InlineData(double.NaN, 4)]
    [InlineData(double.PositiveInfinity, 4)]
    public void PatternClock_DegenerateSong_ReturnsEmptyWithoutHanging(double bpm, int bar)
    {
        var song = Degenerate(60, 4, 0);
        song.Strum = new SongPattern { Subdivision = 2 };
        song.Strum.Slots.Add("D"); song.Strum.Slots.Add("-");
        song.TempoBpm = bpm; song.BeatsPerBar = bar;
        var tl = new SongTimeline(song, 1.0);
        var clock = new PatternClock(song, tl, false);
        Assert.Empty(clock.Slots);
    }
}
