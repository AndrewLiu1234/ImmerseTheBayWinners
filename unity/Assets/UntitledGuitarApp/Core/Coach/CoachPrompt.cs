using System;
using Newtonsoft.Json.Linq;

namespace UntitledGuitarApp.Core.Coach
{
    /// <summary>
    /// LLM request text for the coach card. Core makes NO network calls. Runtime (Person 1) must:
    ///  - POST to the Anthropic Messages API with model id "claude-haiku-5-5" (fast enough for the 3 s budget),
    ///    max_tokens about 200, system = System, one user message = BuildUser(input);
    ///  - use a hard 3 s timeout (TimeoutSec);
    ///  - read the API key from an environment variable (never committed, never logged);
    ///  - only call when flags "coach" is not "off" (unity/Assets/StreamingAssets/flags.default.json);
    ///  - parse with CoachResponseParser.TryParse, and on ANY error (timeout, non-200, offline, parse failure)
    ///    show RuleBasedCoach.Make(input) instead.
    /// </summary>
    public static class CoachPrompt
    {
        public const string ModelId = "claude-haiku-5-5";
        public const double TimeoutSec = 3.0;

        public const string System =
            "You are a warm, concrete guitar coach for an absolute beginner. " +
            "Reply with ONLY one JSON object, no other text: " +
            "{\"headline\":string,\"praise\":string,\"fix\":string,\"next\":string}. " +
            "Limits: headline 6 words max, praise 14 words max, fix 12 words max, next 12 words max. " +
            "Fix must name one chord or one chord change and say what to do. Next is one practice step, e.g. a slower tempo or a chord-change drill. " +
            "Plain words, no jargon, no emojis. Never quote song lyrics.";

        /// <summary>User message: the run summary as one compact JSON object (chords and numbers only).</summary>
        public static string BuildUser(CoachInput i)
        {
            if (i == null) i = new CoachInput();
            var o = new JObject
            {
                ["song"] = i.SongTitle,
                ["tempoPercent"] = Math.Round(i.TempoPercent),
                ["chords"] = i.Total,
                ["hit"] = i.Hits,
                ["missed"] = i.Missed,
                ["skipped"] = i.Skipped,
                ["meanErrorSec"] = Math.Round(i.MeanErrorSec, 2),
                ["waitHolds"] = i.WaitHolds,
            };
            if (i.WorstProblem != ChangeProblem.None)
                o["worstChange"] = new JObject { ["from"] = i.WorstFrom, ["to"] = i.WorstTo, ["problem"] = i.WorstProblem.ToString().ToLowerInvariant() };
            return "Beginner finished a play-along run (positive meanErrorSec = late). Write the coach card.\n" + o.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
