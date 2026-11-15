using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Tests.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using Xunit;

[Trait("Category", "Synthetic")]
public class PluckFeedTests
{
    const int Sr = PluckScenes.Sr;

    static List<PluckEvent> Run(float[] x, int chunk, int s, int midi, double tailSec = 1.0)
    {
        var f = new PluckFeed(AudioConfig.Default, Sr);
        f.SetExpectation(s, midi);
        return Drive(f, x, chunk, tailSec);
    }

    static List<PluckEvent> Drive(PluckFeed f, float[] x, int chunk, double tailSec = 1.0)
    {
        var all = new float[x.Length + (int)(tailSec * Sr)];
        Array.Copy(x, all, x.Length);
        var ev = new List<PluckEvent>();
        for (int i = 0; i < all.Length; i += chunk)
        {
            f.Process(all, i, Math.Min(chunk, all.Length - i));
            while (f.TryRead(out var e)) ev.Add(e);
        }
        return ev;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(4096)]
    public void Process_PluckStreamedInChunks_OneCorrectVerdictAtOnset(int chunk)
    {
        var x = PluckScenes.Scene(55, PluckScenes.FreeT60, seed: 3);
        var ev = Run(x, chunk, 3, 55);
        Assert.Single(ev);
        Assert.Equal(PluckVerdict.Correct, ev[0].Result.Verdict);
        Assert.InRange(ev[0].TimeSec, PluckScenes.PreSec - 0.03, PluckScenes.PreSec + 0.03);
    }

    [Fact]
    public void Process_MutedPluck_FaultVerdict()
    {
        var x = PluckScenes.Scene(55, PluckScenes.MutedT60, seed: 4);
        var ev = Run(x, 512, 3, 55);
        Assert.Single(ev);
        Assert.NotEqual(PluckVerdict.Correct, ev[0].Result.Verdict);
    }

    [Fact]
    public void Process_WrongString_FaultVerdict()
    {
        var x = PluckScenes.Scene(59, PluckScenes.FreeT60, seed: 5);   // B3 played, G3 expected
        var ev = Run(x, 512, 3, 55);
        Assert.Single(ev);
        Assert.NotEqual(PluckVerdict.Correct, ev[0].Result.Verdict);
    }

    [Fact]
    public void Process_Silence_NoVerdict()
    {
        var ev = Run(new float[Sr * 2], 512, 3, 55);
        Assert.Empty(ev);
    }

    [Fact]
    public void Process_NoExpectation_SkipsVerification()
    {
        var f = new PluckFeed(AudioConfig.Default, Sr);
        f.SetExpectation(0, 0);
        var ev = Drive(f, PluckScenes.Scene(55, PluckScenes.FreeT60, seed: 3), 512);
        Assert.Empty(ev);
        Assert.True(f.Dropped >= 1);
    }

    [Fact]
    public void Process_ExpectationChangedMidRing_UsesExpectationAtOnset()
    {
        var x = PluckScenes.Scene(55, PluckScenes.FreeT60, seed: 3);
        var f = new PluckFeed(AudioConfig.Default, Sr);
        f.SetExpectation(3, 55);
        var ev = new List<PluckEvent>();
        int changeAt = PluckScenes.OnsetIndex + 8000;
        var chunk = new float[512];
        for (int i = 0; i < x.Length + Sr; i += 512)
        {
            for (int k = 0; k < 512; k++) chunk[k] = i + k < x.Length ? x[i + k] : 0f;
            f.Process(chunk, 0, 512);
            if (i >= changeAt) f.SetExpectation(1, 64);
            while (f.TryRead(out var e)) ev.Add(e);
        }
        Assert.Single(ev);
        Assert.Equal(3, ev[0].Result.String);
        Assert.Equal(PluckVerdict.Correct, ev[0].Result.Verdict);
    }

    [Fact]
    public void Process_SteadyState_AllocatesNothing()
    {
        var f = new PluckFeed(AudioConfig.Default, Sr);
        f.SetExpectation(3, 55);
        var x = PluckScenes.Scene(55, PluckScenes.FreeT60, seed: 3);
        Drive(f, x, 512);
        long a = AllocProbe.MinAllocatedBytes(() =>
        {
            for (int i = 0; i + 512 <= x.Length; i += 512) { f.Process(x, i, 512); while (f.TryRead(out _)) { } }
        });
        Assert.Equal(0, a);
    }
}
