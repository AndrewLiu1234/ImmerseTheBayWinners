using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Tests.Audio
{
    /// <summary>One row of Fixtures/audio/labels.csv (docs/DATA.md: file,group,chord,string,expected_midi,label,take,source,env).</summary>
    public sealed class LabelRow
    {
        public string File, Group, Chord, String, ExpectedMidi, Label, Take, Source, Env;

        public int StringNumber => int.TryParse(String, out int s) ? s : 0;
        public int Midi => int.TryParse(ExpectedMidi, out int m) ? m : -1;

        /// <summary>A single open string (tuner clip): a string 1–6 whose expected note is that string's open note, and either no
        /// chord or a tuner/open group or label. Pluck passes inside a chord (chord = "G") are excluded.</summary>
        public bool IsOpenString
        {
            get
            {
                int s = StringNumber;
                if (s < 1 || s > Tuning.StringCount || Midi != Tuning.StandardMidi(s)) return false;
                string g = (Group ?? "").ToLowerInvariant(), l = (Label ?? "").ToLowerInvariant();
                return string.IsNullOrWhiteSpace(Chord) || g.Contains("tuner") || g.Contains("open") || l.StartsWith("open");
            }
        }
    }

    public static class FixtureLabels
    {
        public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio");

        public static List<LabelRow> Parse(string csvText)
        {
            var rows = new List<LabelRow>();
            var lines = csvText.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) return rows;
            var header = SplitCsv(lines[0]);
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++) col[header[i].Trim()] = i;
            string Get(List<string> f, string name) => col.TryGetValue(name, out int i) && i < f.Count ? f[i].Trim() : "";
            for (int li = 1; li < lines.Length; li++)
            {
                if (string.IsNullOrWhiteSpace(lines[li]) || lines[li].TrimStart().StartsWith("#")) continue;
                var f = SplitCsv(lines[li]);
                rows.Add(new LabelRow
                {
                    File = Get(f, "file"), Group = Get(f, "group"), Chord = Get(f, "chord"), String = Get(f, "string"),
                    ExpectedMidi = Get(f, "expected_midi"), Label = Get(f, "label"), Take = Get(f, "take"),
                    Source = Get(f, "source"), Env = Get(f, "env"),
                });
            }
            return rows;
        }

        public static List<LabelRow> Load()
        {
            string p = Path.Combine(FixturesDir, "labels.csv");
            return File.Exists(p) ? Parse(File.ReadAllText(p)) : new List<LabelRow>();
        }

        /// <summary>The fixture's absolute path; labels may give it relative to Fixtures/audio or with that prefix.</summary>
        public static string Resolve(string file)
        {
            string f = file.Replace('\\', '/');
            foreach (var prefix in new[] { "core-tests/Fixtures/audio/", "Fixtures/audio/" })
                if (f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) f = f.Substring(prefix.Length);
            return Path.Combine(FixturesDir, f);
        }

        static List<string> SplitCsv(string line)
        {
            var o = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; } else cur.Append(c); }
                else if (c == '"') q = true;
                else if (c == ',') { o.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            o.Add(cur.ToString());
            return o;
        }

        /// <summary>The ```json block of docs/AUDIO_BASELINE.md (human-owned), or an empty object when absent.</summary>
        public static JObject LoadBaseline()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "docs", "AUDIO_BASELINE.md");
                if (!File.Exists(p)) continue;
                var m = Regex.Match(File.ReadAllText(p), "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline);
                return m.Success ? JObject.Parse(m.Groups[1].Value) : new JObject();
            }
            return new JObject();
        }
    }
}
