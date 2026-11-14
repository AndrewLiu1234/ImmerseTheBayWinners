using System;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

[Trait("Category", "Synthetic")]
public class TunerTests
{
    const int Sr = 48000;
    const int Frame = 2048;
    const int Hop = 512;

    static Tuner NewTuner(AudioConfig cfg = null) => new Tuner(cfg ?? AudioConfig.Default, Sr);

    static TunerState At(Tuner t, float[] x, double sec, int targetString = 0) =>
        t.Process(x, (int)(sec * Sr), Frame, sec, targetString);

    [Theory]
    [InlineData(6)] [InlineData(5)] [InlineData(4)] [InlineData(3)] [InlineData(2)] [InlineData(1)]
    public void Process_OpenStringInTune_NearestStringAndInTune(int stringNumber)
    {
        int midi = Tuning.StandardMidi(stringNumber);
        var r = At(NewTuner(), KarplusStrong.Pluck(midi, 600, Sr, seed: stringNumber), 0.2);
        Assert.True(r.HasPitch);
        Assert.Equal(stringNumber, r.String);
        Assert.Equal(midi, r.TargetMidi);
        Assert.InRange(r.Cents, -3.0, 3.0);
        Assert.True(r.InTune);
    }

    [Theory]
    [InlineData(6, 40.0)] [InlineData(6, -40.0)] [InlineData(5, 20.0)] [InlineData(4, -20.0)]
    [InlineData(3, 5.0)] [InlineData(2, -5.0)] [InlineData(1, 20.0)] [InlineData(1, -40.0)]
    public void Process_DetunedString_CentsOffWithinThree(int stringNumber, double detune)
    {
        int midi = Tuning.StandardMidi(stringNumber);
        var r = At(NewTuner(), KarplusStrong.Pluck(midi, 600, Sr, seed: 3, detuneCents: detune), 0.2);
        Assert.Equal(stringNumber, r.String);
        Assert.InRange(r.Cents, detune - 3.0, detune + 3.0);
    }

    [Theory]
    [InlineData(3.0, true)] [InlineData(-3.0, true)] [InlineData(10.0, false)] [InlineData(-10.0, false)]
    public void Process_InTuneFlag_FollowsTolerance(double detune, bool inTune)
    {
        var r = At(NewTuner(), KarplusStrong.Pluck(55, 600, Sr, detuneCents: detune), 0.2);
        Assert.Equal(inTune, r.InTune);
    }

    [Fact]
    public void Process_TargetString_ReportsCentsAgainstTarget()
    {
        // An A2 played while the user is tuning the low E: the needle shows +500 cents vs E2, not "A in tune".
        var r = At(NewTuner(), KarplusStrong.Pluck(45, 600, Sr), 0.2, targetString: 6);
        Assert.Equal(6, r.String);
        Assert.Equal(40, r.TargetMidi);
        Assert.InRange(r.Cents, 497.0, 503.0);
        Assert.False(r.InTune);
    }

    // High E plucked while the low E rings sympathetically at −14 dB (E2's 2nd/6th partials sit on E3's odd partials, so
    // the mix is genuinely periodic at E2). With the high E chosen as the target, the expected hypothesis wins.
    static float[] HighEWithRingingLowE()
    {
        var hi = KarplusStrong.Pluck(64, 600, Sr, seed: 2);
        var low = KarplusStrong.Pluck(40, 600, Sr, seed: 1);
        var x = new float[hi.Length];
        for (int i = 0; i < x.Length; i++) x[i] = hi[i] + 0.2f * low[i];
        return x;
    }

    [Fact]
    public void Process_TargetHighEWithSympatheticLowE_ReadsHighE()
    {
        var r = At(NewTuner(), HighEWithRingingLowE(), 0.1, targetString: 1);
        Assert.Equal(1, r.String);
        Assert.InRange(r.Cents, -3.0, 3.0);
        Assert.True(r.InTune);
    }

    [Fact]
    public void Process_FarFromAnyString_StillNearestAndNotInTune()
    {
        // G string tuned a whole tone flat (F3): nearest open string is still G3, at −200 cents.
        var r = At(NewTuner(), KarplusStrong.Pluck(53, 600, Sr), 0.2);
        Assert.Equal(3, r.String);
        Assert.InRange(r.Cents, -203.0, -197.0);
        Assert.False(r.InTune);
    }

    [Fact]
    public void Process_Smoothing_ConvergesAfterStepAndIsTimeBased()
    {
        // 20 cents sharp, then the player turns the peg to in-tune: the needle glides, it doesn't jump.
        var sharp = KarplusStrong.Pluck(50, 1000, Sr, seed: 1, detuneCents: 20.0);
        var inTune = KarplusStrong.Pluck(50, 1000, Sr, seed: 1);
        var t = NewTuner();
        double now = 0;
        for (int i = 0; i < 20; i++, now += Hop / (double)Sr) t.Process(sharp, 4800 + Hop * i, Frame, now);
        var first = t.Process(inTune, 4800, Frame, now);
        Assert.InRange(first.Cents, 5.0, 19.0);                 // partway: smoothed
        Assert.InRange(first.RawCents, -3.0, 3.0);              // raw follows the input immediately
        TunerState last = first;
        for (int i = 1; i < 40; i++) { now += Hop / (double)Sr; last = t.Process(inTune, 4800 + Hop * i, Frame, now); }
        Assert.InRange(last.Cents, -3.0, 3.0);
        Assert.True(last.InTune);
    }

    [Fact]
    public void Process_GapLongerThanHold_RestartsSmoothing()
    {
        var sharp = KarplusStrong.Pluck(50, 1000, Sr, detuneCents: 30.0);
        var inTune = KarplusStrong.Pluck(50, 1000, Sr);
        var t = NewTuner();
        for (int i = 0; i < 10; i++) t.Process(sharp, 4800 + Hop * i, Frame, i * 0.01);
        var r = t.Process(inTune, 4800, Frame, 0.09 + AudioConfig.Default.TunerHoldSec + 0.01);
        Assert.InRange(r.Cents, -3.0, 3.0);
    }

    [Fact]
    public void Process_StringChange_RestartsSmoothing()
    {
        var t = NewTuner();
        var a = KarplusStrong.Pluck(45, 1000, Sr, detuneCents: 30.0);
        for (int i = 0; i < 10; i++) t.Process(a, 4800 + Hop * i, Frame, i * 0.01);
        var r = t.Process(KarplusStrong.Pluck(50, 600, Sr), 4800, Frame, 0.1);
        Assert.Equal(4, r.String);
        Assert.InRange(r.Cents, -3.0, 3.0);
    }

    [Fact]
    public void Process_TimeGoesBackwards_RestartsSmoothingNoThrow()
    {
        var t = NewTuner();
        var a = KarplusStrong.Pluck(45, 1000, Sr, detuneCents: 30.0);
        for (int i = 0; i < 10; i++) t.Process(a, 4800 + Hop * i, Frame, 5.0 + i * 0.01);
        var r = t.Process(KarplusStrong.Pluck(45, 600, Sr), 4800, Frame, 1.0);
        Assert.InRange(r.Cents, -3.0, 3.0);
    }

    [Fact]
    public void Process_Silence_NoPitchNotInTuneFinite()
    {
        var r = NewTuner().Process(new float[Frame], 0, Frame, 0.0);
        Assert.False(r.HasPitch);
        Assert.False(r.InTune);
        Assert.Equal(0, r.String);
        Assert.Equal(PitchStatus.Silent, r.Status);
        Assert.True(double.IsFinite(r.Cents) && double.IsFinite(r.RawCents) && double.IsFinite(r.Hz) && double.IsFinite(r.TargetHz));
    }

    [Fact]
    public void Process_SilenceWithTarget_KeepsTargetForTheNeedle()
    {
        var r = NewTuner().Process(new float[Frame], 0, Frame, 0.0, targetString: 2);
        Assert.False(r.HasPitch);
        Assert.Equal(2, r.String);
        Assert.Equal(59, r.TargetMidi);
        Assert.Equal(0.0, r.Cents);
    }

    [Theory]
    [InlineData(-1)] [InlineData(7)]
    public void Process_InvalidTargetString_Throws(int s)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewTuner().Process(new float[Frame], 0, Frame, 0.0, s));
    }

    [Fact]
    public void Process_SteadyState_AllocatesNothing()
    {
        var x = KarplusStrong.Pluck(40, 1000);
        var t = NewTuner();
        for (int i = 0; i < 5; i++) t.Process(x, 512 * i, Frame, i * 0.01);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) t.Process(x, 4800 + 512 * i, Frame, 0.05 + i * 0.01, i % 2 == 0 ? 0 : 6);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
