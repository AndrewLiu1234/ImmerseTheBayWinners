using System;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// Pluck-check failure modes found on the real dev recordings (MacBook mic), reproduced synthetically. Not an accuracy claim.
[Trait("Category", "Synthetic")]
public class PluckRealGuitarTests
{
    const int Sr = PluckScenes.Sr;
    static int Onset => PluckScenes.OnsetIndex;

    static PluckResult Check(float[] x, int s, int expected, ChordShape chord) =>
        new PluckVerifier(AudioConfig.Default, Sr).Verify(x, 0, x.Length, Onset, s, expected, chord);

    // A free low string through a laptop mic: the fundamental and 2nd partial are rolled off and (on G2) overlap the body's
    // ~98 Hz air mode, so they fade within ~0.3 s while the 3rd-5th partials ring for seconds (dev clip G_s6__01: 98/196 Hz
    // -30 dB by 0.5 s, 392 Hz flat at -45 dB). Ring-out on partials 1-2 only said ShortRing.
    static float[] LaptopMicLowString(int midi, int seed)
    {
        double f0 = Midi.ToHz(midi);
        var low = Signals.Harmonic(f0, new[] { 0.5, 0.4 }, 1200, Sr, t60Sec: 0.35);            // rolled-off, body-damped
        var high = Signals.Harmonic(f0, new[] { 0.0, 0.0, 0.25, 0.45, 0.2 }, 1200, Sr, t60Sec: 4.0);   // the ringing partials
        var x = new float[Onset + low.Length];
        Signals.MixInto(x, low, Onset, 0.3f);
        Signals.MixInto(x, high, Onset, 0.3f);
        Signals.MixInto(x, Signals.Harmonic(98.1, new[] { 0.15 }, 1200, Sr, t60Sec: 0.5), Onset, 0.3f);   // body air mode
        Signals.AddNoise(x, 35, seed, Onset, Onset + Sr / 4);
        return x;
    }

    [Theory]
    [InlineData(6, 43)]   // G chord, string 6 = G2
    [InlineData(6, 40)]   // Em, open low E
    [InlineData(5, 45)]   // Am, open A
    public void Verify_LowStringRingingOnUpperPartials_CorrectNotShortRing(int s, int midi)
    {
        var chord = midi == 43 ? ChordLibrary.G : midi == 40 ? ChordLibrary.Em : ChordLibrary.Am;
        var r = Check(LaptopMicLowString(midi, 5), s, midi, chord);
        Assert.True(r.Verdict == PluckVerdict.Correct, r.ToString());
    }

    [Fact]
    public void Verify_LowStringAllPartialsDamped_StillShortRing()
    {
        // The other side: a bass string a finger leans on loses every partial quickly; still "let it ring".
        double f0 = Midi.ToHz(43);
        var tone = Signals.Harmonic(f0, new[] { 0.5, 0.4, 0.25, 0.45, 0.2 }, 1200, Sr, t60Sec: 0.6);
        var x = new float[Onset + tone.Length];
        Signals.MixInto(x, tone, Onset, 0.3f);
        Signals.AddNoise(x, 35, 9, Onset, Onset + Sr / 4);
        var r = Check(x, 6, 43, ChordLibrary.G);
        Assert.True(r.Verdict == PluckVerdict.ShortRing, r.ToString());
    }

    // The Correct verdict feeds the HUD's Hz/cents: it must be measured, not the hypothesis' nominal frequency echoed back.
    [Fact]
    public void Verify_CorrectViaSalienceFallback_ReportsMeasuredHz()
    {
        // 30 cents sharp D3 whose frames YIN cannot pitch confidently (another string rings a fifth above, plus noise).
        double f0 = Midi.ToHz(50) * Math.Pow(2, 30 / 1200.0);
        var tone = Signals.Harmonic(f0, new[] { 0.6, 0.5, 0.3, 0.2 }, 1200, Sr, t60Sec: 4.0);
        var other = Signals.Harmonic(Midi.ToHz(57) * Math.Pow(2, 13 / 1200.0), new[] { 0.6, 0.4 }, 1200, Sr, t60Sec: 4.0);
        var x = new float[Onset + tone.Length];
        Signals.MixInto(x, tone, Onset, 0.3f);
        Signals.MixInto(x, other, Onset, 0.3f);
        Signals.AddNoise(x, 12, 4, Onset, Onset + Sr / 4);
        var r = Check(x, 4, 50, ChordLibrary.D);
        Assert.True(r.Verdict == PluckVerdict.Correct || r.Verdict == PluckVerdict.ShortRing, r.ToString());
        Assert.InRange(r.Cents, 15, 45);
    }

    // Runtime swaps expectations asynchronously (LearnChordSession exposes string 0 / MIDI -1 in its WholeChord and
    // Mastered stages): a one-frame lag must give Unclear on the audio thread, never an exception.
    [Theory]
    [InlineData(0, -1)] [InlineData(0, 50)] [InlineData(7, 50)] [InlineData(-1, 50)] [InlineData(4, 0)] [InlineData(4, -2)] [InlineData(4, 128)]
    public void Verify_NoActiveStringOrNoExpectedNote_UnclearNeverThrows(int s, int midi)
    {
        var x = PluckScenes.Scene(50, 4, 1);
        var r = new PluckVerifier(AudioConfig.Default, Sr).Verify(x, 0, x.Length, Onset, s, midi, ChordLibrary.D);
        Assert.Equal(PluckVerdict.Unclear, r.Verdict);
        Assert.Equal(PluckTipKeys.PluckAgain, r.TipKey);
        Assert.False(double.IsNaN(r.DetectedHz) || double.IsNaN(r.Cents));
    }

    [Fact]
    public void Verify_MutedExpectationStillJudgesExtraString()
    {
        // ChordShape.Muted (-1) on a REAL string keeps its meaning: "this string should stay silent".
        var x = PluckScenes.Scene(40, 4, 1);
        var r = new PluckVerifier(AudioConfig.Default, Sr).Verify(x, 0, x.Length, Onset, 6, ChordShape.Muted, ChordLibrary.D);
        Assert.Equal(PluckVerdict.ExtraString, r.Verdict);
    }
}
