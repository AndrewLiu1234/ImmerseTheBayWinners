using System;
using System.Linq;
using UntitledGuitarApp.Core.Songs;
using Xunit;

public class SongTimelineTests
{
    [Fact]
    public void Presets_AreTheFlagsList() => Assert.Equal(new[] { 0.5, 0.6, 0.75, 0.9, 1.0 }, SongFiles.Presets());

    [Fact]
    public void EveryPreset_ScalesSecondsAndKeepsBeats()
    {
        foreach (var file in new[] { "happy-birthday.song.json", "riptide-loop.song.json", "stand-by-me-loop.song.json" })
        {
            var song = SongFiles.Load(file);
            foreach (var scale in SongFiles.Presets())
            {
                var tl = new SongTimeline(song, scale);
                double spb = 60.0 / (song.TempoBpm * scale);
                Assert.Equal(spb, tl.SecondsPerBeat, 9);
                double beat = 0;
                foreach (var s in tl.Spans)
                {
                    Assert.Equal(beat, s.StartBeat, 9);
                    Assert.Equal(beat * spb, s.StartSeconds, 9);
                    Assert.Equal(s.EndBeat * spb, s.EndSeconds, 9);
                    beat = s.EndBeat;
                }
                Assert.Equal(tl.TotalBeats * spb, tl.TotalSeconds, 9);
            }
        }
    }

    [Fact]
    public void StandByMe_SecondsAt75Percent()
    {
        var tl = new SongTimeline(SongFiles.Load("stand-by-me-loop.song.json"), 0.75);
        Assert.Equal(1.0, tl.SecondsPerBeat, 9); // 80 bpm × 0.75 = 60 bpm
        Assert.Equal("Em", tl.Spans[1].Chord);
        Assert.Equal(8.0, tl.Spans[1].StartSeconds, 9);
    }

    [Fact]
    public void HappyBirthday_PickupStartsOnBeat3_ThenBar1Beat1()
    {
        var tl = new SongTimeline(SongFiles.Load("happy-birthday.song.json"), 1.0);
        var q0 = tl.At(0);
        Assert.Equal(0, q0.Bar);
        Assert.Equal(3, q0.BeatInBar);
        var q1 = tl.At(tl.SecondsPerBeat * 1.01);
        Assert.Equal(1, q1.Bar);
        Assert.Equal(1, q1.BeatInBar);
        var q4 = tl.At(tl.SecondsPerBeat * 4.01);
        Assert.Equal(2, q4.Bar);
        Assert.Equal(1, q4.BeatInBar);
    }

    [Fact]
    public void Query_ReportsCurrentNextAndBeatsUntilChange()
    {
        var tl = new SongTimeline(SongFiles.Load("riptide-loop.song.json"), 1.0); // Am2 G2 C4 ...
        var q = tl.At(0.5 * tl.SecondsPerBeat);
        Assert.Equal("Am", tl.Spans[q.CurrentIndex].Chord);
        Assert.Equal("G", tl.Spans[q.NextIndex].Chord);
        Assert.Equal(1.5, q.BeatsUntilChange, 9);
        q = tl.At(2 * tl.SecondsPerBeat); // exactly on the change
        Assert.Equal("G", tl.Spans[q.CurrentIndex].Chord);
    }

    [Fact]
    public void Query_BeforeStartAndAfterEnd_AreSafe()
    {
        var tl = new SongTimeline(SongFiles.Load("riptide-loop.song.json"), 0.5);
        var pre = tl.At(-2);
        Assert.Equal(-1, pre.CurrentIndex); Assert.Equal(0, pre.NextIndex); Assert.False(pre.Finished);
        var post = tl.At(tl.TotalSeconds + 1);
        Assert.True(post.Finished); Assert.Equal(-1, post.NextIndex);
        Assert.True(tl.At(double.NaN).CurrentIndex >= 0);
        Assert.Equal(-1, tl.At(tl.Spans.Last().EndSeconds - 0.001).NextIndex);
    }

    [Fact]
    public void ZeroScale_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => new SongTimeline(SongFiles.Load("riptide-loop.song.json"), 0));
}
