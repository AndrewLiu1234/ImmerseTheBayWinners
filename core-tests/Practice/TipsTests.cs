using System;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Theory;
using Xunit;

public class TipsTests
{
    [Fact]
    public void ForPluck_EveryVerdictChordString_AtMostSevenWords()
    {
        foreach (var chord in ChordLibrary.All)
            for (int s = 1; s <= 6; s++)
                foreach (PluckVerdict v in Enum.GetValues(typeof(PluckVerdict)))
                {
                    int m = chord.IsPlayed(s) ? chord.MidiOn(s) : ChordShape.Muted;
                    foreach (var withChord in new[] { true, false })
                    {
                        string tip = Tips.ForPluck(v, s, m, withChord ? chord : null, s == 6 ? 5 : s + 1, m + 2);
                        if (v == PluckVerdict.Correct) { Assert.Null(tip); continue; }
                        Assert.False(string.IsNullOrWhiteSpace(tip));
                        Assert.True(Tips.WordCount(tip) <= Tips.MaxWords, $"{chord} s{s} {v}: \"{tip}\" has {Tips.WordCount(tip)} words");
                    }
                }
        Assert.True(Tips.WordCount(Tips.LateChange) <= Tips.MaxWords);
        Assert.True(Tips.WordCount(Tips.WrongChord("Em", "G")) <= Tips.MaxWords);
    }

    [Fact]
    public void ForPluck_EmFinger2Missing_PressStringFret()
    {
        Assert.Equal("Finger 2: press string 5, fret 2", Tips.ForPluck(PluckVerdict.OpenStringInstead, 5, 47, ChordLibrary.Em));
    }

    [Fact]
    public void ForPluck_GChordOpenBMuted_ArchTrebleSideFinger()
    {
        // B is open in G; string 3 (bass side) is open too, so the finger on string 1 (finger 3) is the one leaning on it.
        Assert.Equal("String 2 muted — arch finger 3", Tips.ForPluck(PluckVerdict.Muted, 2, 59, ChordLibrary.G));
    }

    [Fact]
    public void ForPluck_CChordOpenGMuted_ArchBassSideFinger()
    {
        Assert.Equal("String 3 muted — arch finger 2", Tips.ForPluck(PluckVerdict.Muted, 3, 55, ChordLibrary.C));
        Assert.Equal("String 1 muted — arch finger 1", Tips.ForPluck(PluckVerdict.Muted, 1, 64, ChordLibrary.C));
    }

    [Fact]
    public void ForPluck_AmFrettedBMuted_PressWithFingertip()
    {
        // music-teacher correction: a muted FRETTED string is the pressing finger's fault, not a neighbour's arch.
        Assert.Equal("Finger 1: press with fingertip", Tips.ForPluck(PluckVerdict.Muted, 2, 60, ChordLibrary.Am));
    }

    [Fact]
    public void ForPluck_OpenStringMutedNoFrettedNeighbour_ArchYourFingers()
    {
        Assert.Equal("String 1 muted — arch your fingers", Tips.ForPluck(PluckVerdict.Muted, 1, 64, ChordLibrary.Em));
    }

    [Fact]
    public void ForPluck_WrongFret_OneFretHigherOrLower()
    {
        Assert.Equal("Finger 3: one fret higher", Tips.ForPluck(PluckVerdict.WrongFretLow, 2, 62, ChordLibrary.D, heardMidi: 61));
        Assert.Equal("Finger 2: one fret lower", Tips.ForPluck(PluckVerdict.WrongFretHigh, 1, 66, ChordLibrary.D, heardMidi: 67));
        Assert.Equal("Finger 3: two frets higher", Tips.ForPluck(PluckVerdict.WrongFretLow, 1, 67, ChordLibrary.G, heardMidi: 65));
        Assert.Equal("Play string 3 open, no finger", Tips.ForPluck(PluckVerdict.WrongFretHigh, 3, 55, ChordLibrary.G, heardMidi: 56));
    }

    [Fact]
    public void ForPluck_OtherVerdicts_Templates()
    {
        Assert.Equal("Skip string 6", Tips.ForPluck(PluckVerdict.ExtraString, 6, ChordShape.Muted, ChordLibrary.C));
        Assert.Equal("Pluck string 4 again, slowly", Tips.ForPluck(PluckVerdict.Unclear, 4, 52, ChordLibrary.C));
        Assert.Equal("Let string 2 ring", Tips.ForPluck(PluckVerdict.ShortRing, 2, 60, ChordLibrary.C));
        Assert.Equal("Pluck string 3, not string 4", Tips.ForPluck(PluckVerdict.WrongString, 3, 55, ChordLibrary.G, heardString: 4));
    }

    [Fact]
    public void ForPluck_ResultOverload_UsesHeardFields()
    {
        var r = new PluckResult(2, 62, 277.18, -100, PluckVerdict.WrongFretLow, PluckTipKeys.FretHigher, 61, 2, 0.9f, 0.1f, 0, false, 10, 15);
        Assert.Equal("Finger 3: one fret higher", Tips.ForPluck(r, ChordLibrary.D));
    }
}
