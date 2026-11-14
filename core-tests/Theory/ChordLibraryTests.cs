using System.Linq;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// Ground truth: docs/MASTER_PLAN.md Appendix B2 (listed string 6 → 1; -1 = muted, finger 0 = none).
public class ChordLibraryTests
{
    public static TheoryData<string, int[], int[], int[], int> AppendixB2 => new TheoryData<string, int[], int[], int[], int>
    {
        //            frets 6→1                    fingers 6→1                  MIDI 6→1                          bass
        { "Em", new[] { 0, 2, 2, 0, 0, 0 },  new[] { 0, 2, 3, 0, 0, 0 },  new[] { 40, 47, 52, 55, 59, 64 },  6 },
        { "G",  new[] { 3, 2, 0, 0, 0, 3 },  new[] { 2, 1, 0, 0, 0, 3 },  new[] { 43, 47, 50, 55, 59, 67 },  6 },
        { "C",  new[] { -1, 3, 2, 0, 1, 0 }, new[] { 0, 3, 2, 0, 1, 0 },  new[] { -1, 48, 52, 55, 60, 64 },  5 },
        { "D",  new[] { -1, -1, 0, 2, 3, 2 },new[] { 0, 0, 0, 1, 3, 2 },  new[] { -1, -1, 50, 57, 62, 66 }, 4 },
        { "Am", new[] { -1, 0, 2, 2, 1, 0 }, new[] { 0, 0, 2, 3, 1, 0 },  new[] { -1, 45, 52, 57, 60, 64 },  5 },
    };

    [Theory, MemberData(nameof(AppendixB2))]
    public void Get_MustChord_MatchesAppendixB2(string name, int[] frets, int[] fingers, int[] midi, int bass)
    {
        var shape = ChordLibrary.Get(name);
        Assert.Equal(name, shape.Name);
        Assert.Equal(frets, shape.FretsLowToHigh.ToArray());
        Assert.Equal(fingers, shape.FingersLowToHigh.ToArray());
        Assert.Equal(midi, shape.ExpectedMidiLowToHigh.ToArray());
        Assert.Equal(bass, shape.BassString);
    }

    [Theory, MemberData(nameof(AppendixB2))]
    public void FretOn_StringNumber_IsOneForHighE(string name, int[] frets, int[] fingers, int[] midi, int bass)
    {
        var shape = ChordLibrary.Get(name);
        for (int s = 1; s <= 6; s++)
        {
            Assert.Equal(frets[6 - s], shape.FretOn(s));
            Assert.Equal(fingers[6 - s], shape.FingerOn(s));
            Assert.Equal(midi[6 - s], shape.MidiOn(s));
            Assert.Equal(frets[6 - s] >= 0, shape.IsPlayed(s));
        }
        Assert.True(shape.IsPlayed(bass));
    }

    [Fact]
    public void Must_ContainsExactlyTheFiveBeginnerChords()
    {
        Assert.Equal(new[] { "G", "C", "D", "Em", "Am" }, ChordLibrary.Must.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void All_EveryShape_ExpectedMidiEqualsTuningPlusFret()
    {
        foreach (var shape in ChordLibrary.All)
            for (int s = 1; s <= 6; s++)
                Assert.Equal(shape.IsPlayed(s) ? Tuning.StandardMidi(s) + shape.FretOn(s) : -1, shape.MidiOn(s));
    }

    [Fact]
    public void All_ShouldAndCouldChords_MatchAppendixB2()
    {
        Assert.Equal(new[] { 40, 47, 52, 56, 59, 64 }, ChordLibrary.Get("E").ExpectedMidiLowToHigh.ToArray());
        Assert.Equal(new[] { -1, 45, 52, 57, 61, 64 }, ChordLibrary.Get("A").ExpectedMidiLowToHigh.ToArray());
        Assert.Equal(new[] { -1, -1, 50, 57, 62, 65 }, ChordLibrary.Get("Dm").ExpectedMidiLowToHigh.ToArray());
    }

    [Fact]
    public void TryGet_UnknownChord_ReturnsFalse()
    {
        Assert.False(ChordLibrary.TryGet("F#m7b5", out var shape));
        Assert.Null(shape);
        Assert.False(ChordLibrary.TryGet(null, out _));
    }

    [Fact]
    public void Get_UnknownChord_Throws()
    {
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => ChordLibrary.Get("Bb"));
    }

    [Fact]
    public void Get_IsCaseSensitive_EmIsNotEM()
    {
        Assert.False(ChordLibrary.TryGet("EM", out _));
    }

    [Fact]
    public void FretOn_OutOfRangeString_Throws()
    {
        var g = ChordLibrary.Get("G");
        Assert.Throws<System.ArgumentOutOfRangeException>(() => g.FretOn(0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => g.FretOn(7));
    }

    [Fact]
    public void FrettedNotes_G_ListsFingerStringFret()
    {
        var notes = ChordLibrary.Get("G").FrettedNotes().ToArray();
        // string 6 fret 3 finger 2 · string 5 fret 2 finger 1 · string 1 fret 3 finger 3
        Assert.Equal(3, notes.Length);
        Assert.Contains(notes, n => n.String == 6 && n.Fret == 3 && n.Finger == 2);
        Assert.Contains(notes, n => n.String == 5 && n.Fret == 2 && n.Finger == 1);
        Assert.Contains(notes, n => n.String == 1 && n.Fret == 3 && n.Finger == 3);
    }

    [Fact]
    public void Shape_ArraysAreNotMutableFromOutside()
    {
        var frets = new[] { 3, 2, 0, 0, 0, 3 };
        var shape = new ChordShape("X", frets, new[] { 2, 1, 0, 0, 0, 3 });
        frets[0] = 9;
        Assert.Equal(3, shape.FretOn(6));
    }
}
