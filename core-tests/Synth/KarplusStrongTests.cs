using System;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// The synth must itself be in tune far below the ±3-cent tracker tolerance, or the tracker tests prove nothing.
// Measured with an independent long-window phase-advance estimator (Signals.ReferenceHz), not with YIN.
[Trait("Category", "Synthetic")]
public class KarplusStrongTests
{
    [Theory]
    [InlineData(40, 0.0)] [InlineData(45, 0.0)] [InlineData(50, 0.0)] [InlineData(55, 0.0)] [InlineData(59, 0.0)] [InlineData(64, 0.0)]
    [InlineData(40, -40.0)] [InlineData(64, 20.0)] [InlineData(59, -5.0)] [InlineData(76, 13.7)]
    public void Pluck_FractionalDelay_PitchWithinTenthOfACent(int midi, double detune)
    {
        var x = KarplusStrong.Pluck(midi, 1500, 48000, seed: 3, detuneCents: detune);
        double expected = Midi.ToHz(midi) * Math.Pow(2.0, detune / 1200.0);
        double measured = Signals.ReferenceHz(x, expected, 48000, startSec: 0.2, windowSec: 0.4, gapSec: 0.5);
        Assert.InRange(Midi.CentsBetween(expected, measured), -0.1, 0.1);
    }

    // The reference estimator must see an offset from its nominal frequency, or the test above would pass trivially.
    [Theory]
    [InlineData(82.40689, 0.25)] [InlineData(329.6276, -0.6)] [InlineData(196.0, 0.05)]
    public void ReferenceHz_KnownOffsetSine_RecoversTrueFrequency(double nominal, double offsetHz)
    {
        var x = Signals.Harmonic(nominal + offsetHz, new[] { 1.0, 0.5, 0.25 }, 1500);
        double measured = Signals.ReferenceHz(x, nominal, 48000, 0.2, 0.4, 0.5);
        Assert.InRange(measured - (nominal + offsetHz), -0.002, 0.002);
    }

    [Fact]
    public void Pluck_SameSeed_IsBitIdentical()
    {
        Assert.Equal(KarplusStrong.Pluck(45, 300, seed: 7), KarplusStrong.Pluck(45, 300, seed: 7));
    }

    [Fact]
    public void Pluck_DifferentSeed_Differs()
    {
        Assert.NotEqual(KarplusStrong.Pluck(45, 300, seed: 7), KarplusStrong.Pluck(45, 300, seed: 8));
    }

    [Fact]
    public void Pluck_Length_MatchesDurationAndRate()
    {
        Assert.Equal(44100 / 2, KarplusStrong.Pluck(40, 500, 44100).Length);
    }
}
