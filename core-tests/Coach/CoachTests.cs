using System.Collections.Generic;
using System.Linq;
using UntitledGuitarApp.Core.Coach;
using UntitledGuitarApp.Core.Practice;
using Xunit;

public class CoachTests
{
    static ChordResult R(int i, string chord, ChordOutcome o, double err = double.NaN) =>
        new ChordResult { Index = i, Chord = chord, Outcome = o, ErrorSec = err };

    static List<ChordResult> Perfect() => new List<ChordResult>
    {
        R(0, "G", ChordOutcome.Hit, 0.02), R(1, "C", ChordOutcome.Hit, 0.05), R(2, "G", ChordOutcome.Hit, -0.03), R(3, "D", ChordOutcome.Hit, 0.04),
    };

    static List<ChordResult> BadGC() => new List<ChordResult>
    {
        R(0, "G", ChordOutcome.Hit, 0.05), R(1, "C", ChordOutcome.Hit, 0.9), R(2, "D", ChordOutcome.Hit, 0.1), R(3, "G", ChordOutcome.Hit, 0.0),
        R(4, "C", ChordOutcome.Missed),
    };

    static CoachInput In(List<ChordResult> r, double pct = 75, int holds = 0) => CoachInput.Build("song-x", "Song X", 60, pct, r, holds);

    static void AssertLimits(CoachCard c)
    {
        Assert.InRange(RuleBasedCoach.WordCount(c.Headline), 1, 6);
        Assert.InRange(RuleBasedCoach.WordCount(c.Praise), 1, 14);
        Assert.InRange(RuleBasedCoach.WordCount(c.Fix), 1, 12);
        Assert.InRange(RuleBasedCoach.WordCount(c.Next), 1, 12);
    }

    [Fact]
    public void Input_summarises_counts_and_errors()
    {
        var i = In(new List<ChordResult> { R(0, "G", ChordOutcome.Hit, 0.2), R(1, "C", ChordOutcome.Hit, -0.1), R(2, "D", ChordOutcome.Missed), R(3, "G", ChordOutcome.Skipped) }, 90, 2);
        Assert.Equal(4, i.Total); Assert.Equal(2, i.Hits); Assert.Equal(1, i.Missed); Assert.Equal(1, i.Skipped);
        Assert.Equal(0.05, i.MeanErrorSec, 6); Assert.Equal(0.15, i.MeanAbsErrorSec, 6);
        Assert.Equal(2, i.WaitHolds); Assert.Equal("song-x", i.SongId);
        // Skipped beats Missed for worst change: D -> G.
        Assert.Equal("D", i.WorstFrom); Assert.Equal("G", i.WorstTo); Assert.Equal(ChangeProblem.Skipped, i.WorstProblem);
    }

    [Fact]
    public void Input_worst_change_late_hit_and_determinism()
    {
        var a = In(BadGC()); var b = In(BadGC());
        Assert.Equal("G", a.WorstFrom); Assert.Equal("C", a.WorstTo); // missed C at end beats late hit
        Assert.Equal(ChangeProblem.Missed, a.WorstProblem);
        var late = In(BadGC().Take(4).ToList());
        Assert.Equal(ChangeProblem.Late, late.WorstProblem); Assert.Equal("C", late.WorstTo);
        Assert.Equal(a.MeanErrorSec, b.MeanErrorSec); Assert.Equal(a.WorstTo, b.WorstTo);
    }

    [Fact]
    public void Input_handles_null_empty_and_nan()
    {
        Assert.Equal(0, CoachInput.Build(null, null, double.NaN, double.NaN, null).Total);
        var i = In(new List<ChordResult> { R(0, "G", ChordOutcome.Hit, double.NaN), null });
        Assert.Equal(1, i.Total); Assert.Equal(0, i.MeanAbsErrorSec);
    }

    [Fact]
    public void Rule_perfect_run()
    {
        var c = RuleBasedCoach.Make(In(Perfect(), 75));
        AssertLimits(c); Assert.True(c.FromFallback);
        Assert.Equal("Clean run", c.Headline); Assert.Contains("90%", c.Next);
        Assert.Equal("Try a new song.", RuleBasedCoach.Make(In(Perfect(), 100)).Next);
    }

    [Fact]
    public void Rule_bad_G_to_C_change_names_the_pair()
    {
        var c = RuleBasedCoach.Make(In(BadGC().Take(4).ToList()));
        AssertLimits(c);
        Assert.Contains("G to C", c.Fix); Assert.Contains("earlier", c.Fix);
        Assert.Contains("G to C", c.Next);
        var m = RuleBasedCoach.Make(In(BadGC())); AssertLimits(m); Assert.Contains("G to C", m.Fix);
    }

    [Fact]
    public void Rule_all_skipped_and_empty()
    {
        var s = RuleBasedCoach.Make(In(new List<ChordResult> { R(0, "G", ChordOutcome.Skipped), R(1, "C", ChordOutcome.Skipped) }));
        AssertLimits(s); Assert.Contains("slow", s.Headline);
        var e = RuleBasedCoach.Make(In(new List<ChordResult>())); AssertLimits(e);
        AssertLimits(RuleBasedCoach.Make(null));
    }

    [Fact]
    public void Prompt_has_no_lyrics_and_is_small()
    {
        var i = In(BadGC());
        string user = CoachPrompt.BuildUser(i);
        Assert.True(CoachPrompt.System.Length < 900, "system " + CoachPrompt.System.Length);
        Assert.True(user.Length < 1000, "user " + user.Length);
        Assert.Contains("Never quote song lyrics", CoachPrompt.System);
        Assert.Contains("worstChange", user); Assert.Contains("\"from\":\"G\"", user);
        Assert.DoesNotContain("lyric", user.ToLowerInvariant());
        Assert.Equal("claude-haiku-5-5", CoachPrompt.ModelId);
    }

    const string Good = "{\"headline\":\"Nice steady run\",\"praise\":\"Your G chords rang out clearly.\",\"fix\":\"Start the G to C change earlier.\",\"next\":\"Try 90% tempo.\"}";

    [Fact]
    public void Parser_accepts_good_and_wrapped_json()
    {
        Assert.True(CoachResponseParser.TryParse(Good, out var c));
        Assert.Equal("Nice steady run", c.Headline); Assert.False(c.FromFallback);
        Assert.True(CoachResponseParser.TryParse("Sure! Here you go:\n```json\n" + Good + "\n```\nHope it helps {x}", out var w));
        Assert.Equal("Try 90% tempo.", w.Next);
        Assert.True(CoachResponseParser.TryParse(Good.Replace("Nice steady run", "Brace } inside"), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"headline\":\"a\"")]
    [InlineData("{\"headline\":\"a\",\"praise\":\"b\",\"fix\":\"c\"}")]
    [InlineData("{\"headline\":\"a\",\"praise\":\"b\",\"fix\":\"c\",\"next\":5}")]
    [InlineData("{\"headline\":\"\",\"praise\":\"b\",\"fix\":\"c\",\"next\":\"d\"}")]
    [InlineData("{\"headline\":\"one two three four five six seven\",\"praise\":\"b\",\"fix\":\"c\",\"next\":\"d\"}")]
    [InlineData("{\"headline\":\"a\",\"praise\":\"b\",\"fix\":\"one two three four five six seven eight nine ten eleven twelve thirteen\",\"next\":\"d\"}")]
    [InlineData("[1,2]")]
    public void Parser_rejects_bad(string t)
    {
        Assert.False(CoachResponseParser.TryParse(t, out var c));
        Assert.Null(c);
    }

    [Fact]
    public void Parser_null_is_rejected() => Assert.False(CoachResponseParser.TryParse(null, out _));
}
