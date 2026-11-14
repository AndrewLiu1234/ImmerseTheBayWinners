using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Songs
{
    /// <summary>Semantic rules for Song JSON v1: docs/SONGS.md content rules + docs/ARCHITECTURE.md schema. Empty list = valid.</summary>
    public static class SongValidator
    {
        public static List<string> Validate(Song s)
        {
            var e = new List<string>();
            if (s == null) { e.Add("song is null"); return e; }
            if (s.Schema != 1) e.Add($"schema must be 1 (got {s.Schema})");
            if (string.IsNullOrWhiteSpace(s.Id)) e.Add("id is empty");
            if (string.IsNullOrWhiteSpace(s.Title)) e.Add("title is empty");
            if (!(s.TempoBpm > 0) || double.IsInfinity(s.TempoBpm)) e.Add("tempoBpm must be > 0");

            bool okSig = s.BeatUnit == 4 && (s.BeatsPerBar == 3 || s.BeatsPerBar == 4);
            if (!okSig) e.Add($"timeSignature must be [3,4] or [4,4] (got [{s.BeatsPerBar},{s.BeatUnit}])");
            if (s.PickupBeats < 0 || (okSig && s.PickupBeats >= s.BeatsPerBar)) e.Add("pickupBeats must be >= 0 and less than one bar");

            bool anyLyric = false;
            foreach (var sec in s.Sections) foreach (var l in sec.Lines) if (!string.IsNullOrEmpty(l.Lyric)) anyLyric = true;

            switch (s.License)
            {
                case "original": break;
                case "public-domain":
                    if (string.IsNullOrWhiteSpace(s.Source)) e.Add("license public-domain requires a source URL");
                    break;
                case "chords-only":
                    if (string.IsNullOrWhiteSpace(s.Credit)) e.Add("license chords-only requires a credit");
                    if (anyLyric) e.Add("license chords-only requires every lyric to be \"\"");
                    break;
                case "user-provided": e.Add("license user-provided songs are never committed"); break;
                default: e.Add($"unknown license '{s.License}' (original | public-domain | chords-only)"); break;
            }

            Pattern(s.Strum, "strum", e, x => x == "D" || x == "U" || x == "-");
            Pattern(s.Pick, "pick", e, x => x == "B" || x == "-" || (x.Length == 1 && x[0] >= '1' && x[0] <= '6'));

            double total = 0;
            for (int i = 0; i < s.Sections.Count; i++)
                for (int j = 0; j < s.Sections[i].Lines.Count; j++)
                {
                    var line = s.Sections[i].Lines[j]; var p = $"sections[{i}].lines[{j}]";
                    string lyric = line.Lyric ?? "";
                    if (line.Chords.Count == 0) e.Add(p + " has no chords");
                    for (int k = 0; k < line.Chords.Count; k++)
                    {
                        var c = line.Chords[k]; var cp = $"{p}.chords[{k}]";
                        if (!ChordLibrary.TryGet(c.Chord, out _)) e.Add($"{cp}: chord '{c.Chord}' is not in ChordLibrary");
                        if (!(c.Beats > 0) || double.IsInfinity(c.Beats)) e.Add(cp + ": beats must be > 0"); else total += c.Beats;
                        if (lyric.Length > 0)
                        {
                            if (c.At < 0) e.Add(cp + ": 'at' is required when the line has a lyric");
                            else if (c.At >= lyric.Length) e.Add($"{cp}: at {c.At} is outside the lyric (length {lyric.Length})");
                        }
                        else if (c.At >= 0) e.Add(cp + ": 'at' given but the lyric is empty");
                    }
                }
            if (total <= 0) e.Add("song has no chord beats");
            else if (okSig && Math.Abs(Math.IEEERemainder(total - s.PickupBeats, s.BeatsPerBar)) > 1e-9)
                e.Add($"total beats {total} minus pickup {s.PickupBeats} is not a whole number of {s.BeatsPerBar}-beat bars");
            return e;
        }

        static void Pattern(SongPattern p, string name, List<string> e, Func<string, bool> okSlot)
        {
            if (p == null) { e.Add(name + " pattern is required"); return; }
            if (p.Subdivision <= 0) e.Add(name + ".subdivision must be > 0");
            if (p.Slots.Count != p.Subdivision) e.Add($"{name}.slots has {p.Slots.Count} entries but subdivision is {p.Subdivision}");
            foreach (var x in p.Slots) if (x == null || !okSlot(x)) { e.Add($"{name}.slots has invalid entry '{x}'"); break; }
        }

        /// <summary>Parse + validate in one call. Never throws; all messages land in Errors.</summary>
        public static ParseResult Load(string json)
        {
            var r = SongParser.Parse(json);
            if (r.Errors.Count == 0) r.Errors.AddRange(Validate(r.Song));
            return r;
        }
    }
}
