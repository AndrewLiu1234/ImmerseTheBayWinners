using System;
using Newtonsoft.Json.Linq;

namespace UntitledGuitarApp.Core.Coach
{
    /// <summary>
    /// Parses and validates an LLM reply. Tolerates prose or code fences around the JSON object; rejects anything
    /// malformed, with a missing/non-string/empty field or over a word limit (caller then uses RuleBasedCoach).
    /// Never throws.
    /// </summary>
    public static class CoachResponseParser
    {
        public static bool TryParse(string text, out CoachCard card, CoachConfig cfg = null)
        {
            card = null;
            cfg = cfg ?? new CoachConfig();
            string json = ExtractObject(text);
            if (json == null) return false;
            JObject o;
            try { o = JObject.Parse(json); } catch (Exception) { return false; }
            if (!Field(o, "headline", cfg.HeadlineMaxWords, out var h)) return false;
            if (!Field(o, "praise", cfg.PraiseMaxWords, out var p)) return false;
            if (!Field(o, "fix", cfg.FixMaxWords, out var f)) return false;
            if (!Field(o, "next", cfg.NextMaxWords, out var n)) return false;
            card = new CoachCard { Headline = h, Praise = p, Fix = f, Next = n, FromFallback = false };
            return true;
        }

        static bool Field(JObject o, string name, int maxWords, out string value)
        {
            value = null;
            if (!o.TryGetValue(name, out var t) || t.Type != JTokenType.String) return false;
            string s = ((string)t ?? "").Trim();
            int w = RuleBasedCoach.WordCount(s);
            if (w == 0 || w > maxWords) return false;
            value = RuleBasedCoach.Clip(s, maxWords);
            return true;
        }

        /// <summary>First balanced {...} (string-aware), or null.</summary>
        static string ExtractObject(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int start = s.IndexOf('{');
            if (start < 0) return null;
            int depth = 0; bool inStr = false, esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return s.Substring(start, i - start + 1);
            }
            return null;
        }
    }
}
