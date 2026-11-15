using UntitledGuitarApp.Core.Coach;
using Xunit;

public class CoachSanitizeTests
{
    [Fact]
    public void TryParse_ControlAndBidiCharacters_AreStripped()
    {
        string json = "{\"headline\":\"Nice\\u202E job\\u0007\",\"praise\":\"Good\\u2066 sound\",\"fix\":\"Press\\u0001 harder\",\"next\":\"Try\\u2069 again\"}";
        Assert.True(CoachResponseParser.TryParse(json, out var card));
        foreach (var f in new[] { card.Headline, card.Praise, card.Fix, card.Next })
            foreach (char c in f) Assert.True(c >= ' ' && (c < '‪' || c > '‮') && (c < '⁦' || c > '⁩'), f);
        Assert.Equal("Nice job", card.Headline);
    }
}
