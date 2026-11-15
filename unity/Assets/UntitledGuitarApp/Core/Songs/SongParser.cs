using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UntitledGuitarApp.Core.Songs
{
    /// <summary>
    /// Outcome of parse (and optionally validate). Song is null when the JSON was structurally unreadable;
    /// otherwise it is set even if Errors is non-empty (so tools can show a partial song). Ok = Song set and no errors.
    /// </summary>
    public sealed class ParseResult
    {
        public Song Song;
        public readonly List<string> Errors = new List<string>();
        public bool Ok => Song != null && Errors.Count == 0;
    }

    /// <summary>Song JSON v1 → Song. Structural checks only (types, required fields); SongValidator does the rules. Never throws.</summary>
    public static class SongParser
    {
        public static ParseResult Parse(string json)
        {
            try { return ParseCore(json); }
            catch (Exception e) { var r = new ParseResult(); r.Errors.Add("song could not be read: " + e.Message); return r; }
        }

        static ParseResult ParseCore(string json)
        {
            var r = new ParseResult();
            JObject root;
            try
            {
                if (string.IsNullOrWhiteSpace(json)) { r.Errors.Add("song JSON is empty"); return r; }
                root = JToken.Parse(json) as JObject;
                if (root == null) { r.Errors.Add("song JSON must be an object"); return r; }
            }
            catch (Exception e) { r.Errors.Add("invalid JSON: " + e.Message); return r; }

            var s = new Song();
            var err = r.Errors;
            s.Schema = Int(root, "schema", "", true, err);
            s.Id = Str(root, "id", "", true, err);
            s.Title = Str(root, "title", "", true, err);
            s.Artist = Str(root, "artist", "", false, err);
            s.License = Str(root, "license", "", true, err);
            s.Source = Str(root, "source", "", false, err);
            s.Credit = Str(root, "credit", "", false, err);
            s.Key = Str(root, "key", "", false, err);
            s.Notes = Str(root, "notes", "", false, err);
            s.TempoBpm = Num(root, "tempoBpm", "", true, err);
            s.PickupBeats = Num(root, "pickupBeats", "", false, err);

            if (root["timeSignature"] is JArray ts && ts.Count == 2 && ts[0].Type == JTokenType.Integer && ts[1].Type == JTokenType.Integer)
            {
                if (TryInt(ts[0], out int bpb) && TryInt(ts[1], out int bu)) { s.BeatsPerBar = bpb; s.BeatUnit = bu; }
                else err.Add("timeSignature numbers are out of range");
            }
            else err.Add("timeSignature must be [beatsPerBar, beatUnit], e.g. [3, 4]");

            s.Strum = Pattern(root, "strum", err);
            s.Pick = Pattern(root, "pick", err);

            if (!(root["sections"] is JArray secs) || secs.Count == 0) err.Add("sections must be a non-empty array");
            else
            {
                for (int i = 0; i < secs.Count; i++)
                {
                    var so = secs[i] as JObject; var sp = $"sections[{i}]";
                    if (so == null) { err.Add(sp + " must be an object"); continue; }
                    var sec = new SongSection { Name = Str(so, "name", sp + ".", false, err) };
                    if (!(so["lines"] is JArray lines)) err.Add(sp + ".lines must be an array");
                    else for (int j = 0; j < lines.Count; j++) sec.Lines.Add(Line(lines[j], $"{sp}.lines[{j}]", err));
                    s.Sections.Add(sec);
                }
            }
            r.Song = s;
            return r;
        }

        static SongLine Line(JToken t, string p, List<string> err)
        {
            var line = new SongLine();
            var o = t as JObject;
            if (o == null) { err.Add(p + " must be an object"); return line; }
            line.Lyric = Str(o, "lyric", p + ".", false, err);
            if (!(o["chords"] is JArray cs) || cs.Count == 0) { err.Add(p + ".chords must be a non-empty array"); return line; }
            for (int k = 0; k < cs.Count; k++)
            {
                var co = cs[k] as JObject; var cp = $"{p}.chords[{k}]";
                if (co == null) { err.Add(cp + " must be an object"); continue; }
                line.Chords.Add(new SongChord
                {
                    Chord = Str(co, "chord", cp + ".", true, err),
                    Beats = Num(co, "beats", cp + ".", true, err),
                    At = co["at"] == null ? -1 : Int(co, "at", cp + ".", false, err)
                });
            }
            return line;
        }

        static SongPattern Pattern(JObject root, string name, List<string> err)
        {
            if (root[name] == null) return null; // validator reports it as required
            var o = root[name] as JObject;
            if (o == null) { err.Add(name + " must be an object"); return null; }
            var p = new SongPattern { Subdivision = Int(o, "subdivision", name + ".", true, err) };
            if (!(o["slots"] is JArray a)) err.Add(name + ".slots must be an array of strings");
            else foreach (var x in a)
                {
                    if (x.Type != JTokenType.String) { err.Add(name + ".slots entries must be strings"); break; }
                    p.Slots.Add((string)x);
                }
            return p;
        }

        static string Str(JObject o, string k, string pre, bool req, List<string> err)
        {
            var t = o[k];
            if (t == null || t.Type == JTokenType.Null) { if (req) err.Add($"{pre}{k} is required"); return ""; }
            if (t.Type != JTokenType.String) { err.Add($"{pre}{k} must be a string"); return ""; }
            return (string)t;
        }

        static double Num(JObject o, string k, string pre, bool req, List<string> err)
        {
            var t = o[k];
            if (t == null || t.Type == JTokenType.Null) { if (req) err.Add($"{pre}{k} is required"); return 0; }
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) { err.Add($"{pre}{k} must be a number"); return 0; }
            return (double)t;
        }

        static bool TryInt(JToken t, out int v)
        {
            v = 0;
            try { v = checked((int)t); return true; } catch (Exception) { return false; }
        }

        static int Int(JObject o, string k, string pre, bool req, List<string> err)
        {
            var t = o[k];
            if (t == null || t.Type == JTokenType.Null) { if (req) err.Add($"{pre}{k} is required"); return 0; }
            if (t.Type != JTokenType.Integer) { err.Add($"{pre}{k} must be an integer"); return 0; }
            if (!TryInt(t, out int v)) { err.Add($"{pre}{k} is out of range"); return 0; }
            return v;
        }
    }
}
