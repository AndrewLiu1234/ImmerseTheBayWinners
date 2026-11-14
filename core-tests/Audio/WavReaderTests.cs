using System.IO;
using UntitledGuitarApp.Core.Tests.Audio;
using Xunit;

public class WavReaderTests
{
    static readonly float[] Ramp = { 0f, 0.25f, -0.5f, 0.999f, -1f };

    [Fact]
    public void ReadMono_Pcm16_RoundTripsWithinOneLsb()
    {
        var x = WavReader.ReadMono(new MemoryStream(WavReader.Encode(Ramp, 44100, asFloat: false)), out int sr);
        Assert.Equal(44100, sr);
        Assert.Equal(Ramp.Length, x.Length);
        for (int i = 0; i < Ramp.Length; i++) Assert.InRange(x[i], Ramp[i] - 1f / 32768, Ramp[i] + 1f / 32768);
    }

    [Fact]
    public void ReadMono_Float32_RoundTripsExactly()
    {
        var x = WavReader.ReadMono(new MemoryStream(WavReader.Encode(Ramp, 48000, asFloat: true)), out int sr);
        Assert.Equal(48000, sr);
        Assert.Equal(Ramp, x);
    }

    [Fact]
    public void ReadMono_Stereo_AveragesChannels()
    {
        var x = WavReader.ReadMono(new MemoryStream(WavReader.Encode(Ramp, 48000, asFloat: true, channels: 2)), out _);
        Assert.Equal(Ramp, x);
    }

    [Fact]
    public void ReadMono_NotRiff_Throws()
    {
        Assert.Throws<InvalidDataException>(() => WavReader.ReadMono(new MemoryStream(new byte[64]), out _));
    }
}
