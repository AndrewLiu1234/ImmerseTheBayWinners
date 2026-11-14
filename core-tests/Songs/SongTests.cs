using System;
using System.IO;
using System.Linq;
using UntitledGuitarApp.Core.Songs;
using Xunit;

public static class SongFiles
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Songs");
    public static string Read(string file) => File.ReadAllText(Path.Combine(Dir, file));
    public static Song Load(string file)
    {
        var r = SongValidator.Load(Read(file));
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        return r.Song;
    }

    public static double[] Presets()
    {
        // Real list from flags.default.json (walk up to the repo root).
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "unity/Assets/StreamingAssets/flags.default.json"))) d = d.Parent;
        Assert.NotNull(d);
        var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(d.FullName, "unity/Assets/StreamingAssets/flags.default.json")));
        return j["tempoPresets"].Select(t => (double)t).ToArray();
    }

    public const string Minimal = @"{""schema"":1,""id"":""x"",""title"":""X"",""license"":""original"",""tempoBpm"":60,""timeSignature"":[4,4],
        ""strum"":{""subdivision"":2,""slots"":[""D"",""-""]},""pick"":{""subdivision"":2,""slots"":[""B"",""1""]},
        ""sections"":[{""name"":""A"",""lines"":[{""lyric"":"""",""chords"":[{""chord"":""G"",""beats"":4},{""chord"":""C"",""beats"":4}]}]}]}";
}

public class AllSongsValid
{
    [Fact]
    public void EveryShippedSong_ParsesAndValidates()
    {
        var files = Directory.GetFiles(SongFiles.Dir, "*.json");
        Assert.True(files.Length >= 3);
        foreach (var f in files)
        {
            var r = SongValidator.Load(File.ReadAllText(f));
            Assert.True(r.Ok, Path.GetFileName(f) + ": " + string.Join("; ", r.Errors));
        }
    }

    [Fact]
    public void HappyBirthday_Has3_4TimeAndOneBeatPickup()
    {
        var s = SongFiles.Load("happy-birthday.song.json");
        Assert.Equal(3, s.BeatsPerBar);
        Assert.Equal(1, s.PickupBeats);
    }
}

public class SongValidatorTests
{
    static string With(string from, string to) => SongFiles.Minimal.Replace(from, to);
    static bool Fails(string json, string contains)
    {
        var r = SongValidator.Load(json);
        return !r.Ok && r.Errors.Any(e => e.Contains(contains));
    }

    [Fact] public void Minimal_IsValid() => Assert.True(SongValidator.Load(SongFiles.Minimal).Ok);
    [Fact] public void BadJson_ReportsErrorWithoutThrowing() => Assert.True(Fails("{nope", "invalid JSON"));
    [Fact] public void Empty_ReportsError() => Assert.True(Fails("", "empty"));
    [Fact] public void NonObject_ReportsError() => Assert.True(Fails("[1]", "object"));
    [Fact] public void WrongSchema_Fails() => Assert.True(Fails(With("\"schema\":1", "\"schema\":2"), "schema must be 1"));
    [Fact] public void UnknownChord_Fails() => Assert.True(Fails(With("\"C\"", "\"F\""), "not in ChordLibrary"));
    [Fact] public void ZeroBeats_Fails() => Assert.True(Fails(With("\"beats\":4},{", "\"beats\":0},{"), "beats must be > 0"));
    [Fact] public void BadSignature_Fails() => Assert.True(Fails(With("[4,4]", "[6,8]"), "timeSignature"));
    [Fact] public void BadStrumSlot_Fails() => Assert.True(Fails(With("\"D\",\"-\"", "\"X\",\"-\""), "strum.slots"));
    [Fact] public void StrumSlotCountMismatch_Fails() => Assert.True(Fails(With("\"subdivision\":2,\"slots\":[\"D\",\"-\"]", "\"subdivision\":3,\"slots\":[\"D\",\"-\"]"), "subdivision"));
    [Fact] public void BadPickString_Fails() => Assert.True(Fails(With("\"B\",\"1\"", "\"B\",\"7\""), "pick.slots"));
    [Fact] public void PickupNotWholeBars_Fails() => Assert.True(Fails(With("\"tempoBpm\":60", "\"tempoBpm\":60,\"pickupBeats\":1"), "whole number"));
    [Fact] public void PublicDomainWithoutSource_Fails() => Assert.True(Fails(With("\"original\"", "\"public-domain\""), "source"));
    [Fact] public void UserProvided_Fails() => Assert.True(Fails(With("\"original\"", "\"user-provided\""), "never committed"));
    [Fact] public void UnknownLicense_Fails() => Assert.True(Fails(With("\"original\"", "\"mit\""), "unknown license"));
    [Fact] public void ChordsOnlyWithoutCredit_Fails() => Assert.True(Fails(With("\"original\"", "\"chords-only\""), "credit"));

    [Fact]
    public void ChordsOnlyWithLyric_Fails()
    {
        var j = With("\"original\"", "\"chords-only\",\"credit\":\"c\"").Replace("\"lyric\":\"\"", "\"lyric\":\"la\"");
        Assert.True(Fails(j, "every lyric"));
    }

    [Fact]
    public void AtOutsideLyric_Fails()
    {
        var j = With("{\"chord\":\"G\",\"beats\":4}", "{\"chord\":\"G\",\"beats\":4,\"at\":9}").Replace("\"lyric\":\"\"", "\"lyric\":\"la\"");
        Assert.True(Fails(j, "outside the lyric"));
    }

    [Fact]
    public void LyricWithoutAt_Fails()
        => Assert.True(Fails(SongFiles.Minimal.Replace("\"lyric\":\"\"", "\"lyric\":\"la\""), "'at' is required"));

    [Fact]
    public void WrongTypes_AreReportedNotThrown()
        => Assert.True(Fails(With("\"tempoBpm\":60", "\"tempoBpm\":\"fast\""), "tempoBpm must be a number"));
}
