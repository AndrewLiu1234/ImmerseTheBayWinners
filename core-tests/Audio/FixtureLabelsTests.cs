using System;
using System.IO;
using System.Linq;
using UntitledGuitarApp.Core.Tests.Audio;
using UntitledGuitarApp.Core.Tests.Synth;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// The AudioEval harness must work before the first recording lands (in-memory CSV + WAV; never writes fixtures).
public class FixtureLabelsTests
{
    const string Csv =
        "file,group,chord,string,expected_midi,label,take,source,env\n" +
        "tuner/open-E2__01.wav,tuner,,6,40,open-E2,1,macbook,quiet\n" +
        "tuner/open-B3__01.wav,tuner,,2,59,open-B3,1,glasses,venue\n" +
        "chords/G_pluck__01.wav,chords,G,3,55,G_pluck,1,macbook,quiet\n" +
        "chords/G_pluck__01.wav,chords,G,6,43,G_pluck,1,macbook,quiet\n" +
        "noise/room__01.wav,noise,,,,room,1,macbook,venue\n" +
        "\"odd, name.wav\",tuner,,5,45,open-A2,2,macbook,quiet\n";

    [Fact]
    public void Parse_HeaderOnly_NoRows()
    {
        Assert.Empty(FixtureLabels.Parse("file,group,chord,string,expected_midi,label,take,source,env\n"));
    }

    [Fact]
    public void Parse_Rows_FieldsByHeaderName()
    {
        var rows = FixtureLabels.Parse(Csv);
        Assert.Equal(6, rows.Count);
        Assert.Equal("tuner/open-B3__01.wav", rows[1].File);
        Assert.Equal(2, rows[1].StringNumber);
        Assert.Equal(59, rows[1].Midi);
        Assert.Equal("venue", rows[1].Env);
        Assert.Equal("odd, name.wav", rows[5].File);
    }

    [Fact]
    public void IsOpenString_SelectsTunerRowsOnly()
    {
        var open = FixtureLabels.Parse(Csv).Where(r => r.IsOpenString).Select(r => r.File).ToArray();
        Assert.Equal(new[] { "tuner/open-E2__01.wav", "tuner/open-B3__01.wav", "odd, name.wav" }, open);
    }

    [Fact]
    public void Resolve_AcceptsPrefixedAndRelativePaths()
    {
        Assert.Equal(FixtureLabels.Resolve("tuner/a.wav"), FixtureLabels.Resolve("core-tests/Fixtures/audio/tuner/a.wav"));
        Assert.StartsWith(FixtureLabels.FixturesDir, FixtureLabels.Resolve("tuner/a.wav"));
    }

    [Fact]
    public void TrustedFrames_SyntheticWavRoundTrip_MedianWithinOneCent()
    {
        // 16-bit WAV of a 2 s low-E pluck (+6 cents) preceded by 0.3 s of silence, as a recording would look.
        var pluck = KarplusStrong.Pluck(40, 2000, 48000, seed: 3, detuneCents: 6.0);
        var x = new float[pluck.Length + 14400];
        Array.Copy(pluck, 0, x, 14400, pluck.Length);
        var y = WavReader.ReadMono(new MemoryStream(WavReader.Encode(x, 48000, asFloat: false)), out int sr);
        var hz = TunerRecordedEvalTests.TrustedFrames(y, sr, out int total);
        Assert.True(hz.Count > 50, $"{hz.Count} of {total} frames trusted");
        hz.Sort();
        Assert.InRange(Midi.CentsBetween(Midi.ToHz(40), hz[hz.Count / 2]), 5.0, 7.0);
    }
}
