using System;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// Some audio interfaces / macOS aggregate devices run at 88.2 or 96 kHz. The tuner and pluck check must work there instead of
// throwing "FrameSize 2048 is too short" in their constructors. Synthetic only.
[Trait("Category", "Synthetic")]
public class HighSampleRateTests
{
    [Theory]
    [InlineData(44100)] [InlineData(48000)]
    public void YinFrameSize_At441And48k_IsConfigFrameSize(int sr)
    {
        Assert.Equal(AudioConfig.Default.FrameSize, new YinPitchTracker(AudioConfig.Default, sr).FrameSize);
        Assert.Equal(AudioConfig.Default.FrameSize, new Tuner(AudioConfig.Default, sr).FrameSize);
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)]
    public void Tuner_HighSampleRate_SilenceSilentAndLowEInTune(int sr)
    {
        var tuner = new Tuner(AudioConfig.Default, sr);
        Assert.True(tuner.FrameSize >= tuner.Tracker.MinWindow);
        int n = tuner.FrameSize;
        var silence = new float[n];
        var s0 = tuner.Process(silence, 0, n, 0.0, 6);
        Assert.Equal(PitchStatus.Silent, s0.Status);

        var e2 = KarplusStrong.Pluck(40, 1000, sr, seed: 2);
        var s = tuner.Process(e2, sr / 4, n, 0.25, 6);
        Assert.Equal(PitchStatus.Pitched, s.Status);
        Assert.InRange(Midi.CentsBetween(Midi.ToHz(40), s.Hz), -3.0, 3.0);
    }

    [Theory]
    [InlineData(88200)] [InlineData(96000)]
    public void PluckVerifier_HighSampleRate_LowECorrectSilenceUnclear(int sr)
    {
        var v = new PluckVerifier(AudioConfig.Default, sr);
        int onset = sr / 4;
        var x = new float[onset + sr];
        Signals.MixInto(x, KarplusStrong.Pluck(40, 1000, sr, seed: 2), onset, 1f);
        var r = v.Verify(x, 0, x.Length, onset, 6, 40, ChordLibrary.Em);
        Assert.True(r.Verdict == PluckVerdict.Correct, r.ToString());
        Assert.InRange(r.Cents, -5.0, 5.0);
        Assert.Equal(PluckVerdict.Unclear, v.Verify(new float[x.Length], 0, x.Length, onset, 6, 40, ChordLibrary.Em).Verdict);
    }
}
