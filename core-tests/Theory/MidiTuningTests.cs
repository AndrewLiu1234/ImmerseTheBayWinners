using UntitledGuitarApp.Core.Theory;
using Xunit;

// Ground truth: docs/MASTER_PLAN.md Appendix B1 + B3.
public class MidiTuningTests
{
    [Theory]
    [InlineData(40, 82.41)] [InlineData(43, 98.00)] [InlineData(45, 110.00)] [InlineData(47, 123.47)]
    [InlineData(48, 130.81)] [InlineData(50, 146.83)] [InlineData(52, 164.81)] [InlineData(55, 196.00)]
    [InlineData(56, 207.65)] [InlineData(57, 220.00)] [InlineData(59, 246.94)] [InlineData(60, 261.63)]
    [InlineData(61, 277.18)] [InlineData(62, 293.66)] [InlineData(64, 329.63)] [InlineData(65, 349.23)]
    [InlineData(66, 369.99)] [InlineData(67, 392.00)] [InlineData(69, 440.00)]
    public void ToHz_AppendixB3_WithinOneHundredthHz(int midi, double hz)
    {
        Assert.InRange(Midi.ToHz(midi), hz - 0.01, hz + 0.01);
    }

    [Theory]
    [InlineData(40)] [InlineData(55)] [InlineData(69)] [InlineData(88)]
    public void FromHz_RoundTripsToHz(int midi)
    {
        Assert.Equal(midi, Midi.FromHz(Midi.ToHz(midi)), 9);
    }

    [Fact]
    public void CentsBetween_OneSemitone_Is100()
    {
        Assert.Equal(100.0, Midi.CentsBetween(Midi.ToHz(40), Midi.ToHz(41)), 9);
        Assert.Equal(-1200.0, Midi.CentsBetween(440.0, 220.0), 9);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(-5.0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void FromHz_InvalidInput_ReturnsNaNNotThrow(double hz)
    {
        Assert.True(double.IsNaN(Midi.FromHz(hz)));
        Assert.True(double.IsNaN(Midi.CentsBetween(hz, 440.0)));
    }

    [Theory]
    [InlineData(40, "E2")] [InlineData(56, "G#3")] [InlineData(60, "C4")] [InlineData(66, "F#4")] [InlineData(69, "A4")]
    public void Name_UsesSharpsAndScientificOctave(int midi, string name)
    {
        Assert.Equal(name, Midi.Name(midi));
    }

    [Theory]
    [InlineData(6, 40)] [InlineData(5, 45)] [InlineData(4, 50)] [InlineData(3, 55)] [InlineData(2, 59)] [InlineData(1, 64)]
    public void StandardMidi_StringNumber_MatchesAppendixB1(int stringNumber, int midi)
    {
        Assert.Equal(midi, Tuning.StandardMidi(stringNumber));
    }

    [Fact]
    public void StandardMidi_OutOfRange_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Tuning.StandardMidi(0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Tuning.StandardMidi(7));
    }
}
