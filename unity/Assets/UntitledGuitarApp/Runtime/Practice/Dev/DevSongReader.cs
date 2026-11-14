// TEMPORARY (dev play-along driver). Reads unity/Assets/StreamingAssets/songs/<id>.song.json (schema 1, docs/SONGS.md;
// Person 2's files, read-only here) into a DevSong. Parse and LoadFile are plain .NET (DevJson), so they run offline in tests;
// only Load(id) and SongsFolder touch the engine (Application.streamingAssetsPath).
//
// What it reads: id, title, artist, license, credit, tempoBpm, timeSignature [n, d], pickupBeats (optional, default 0),
// strum {subdivision, slots}, pick {subdivision, slots}, sections[] {name, lines[] {lyric, chords[] {chord, beats, at}}}.
// "at" is kept only on lines that have words. Unknown keys are ignored. It refuses (returns false with a reason) a file
// whose schema is not 1, whose tempo or time signature is missing or not positive, or that has no chord with beats > 0.
// It does NOT judge content rules (chords-only songs must have empty lyrics): that is Core's AllSongsValid.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Practice.Dev
{
    /// <summary>TEMPORARY. Song JSON to <see cref="DevSong"/>.</summary>
    public static class DevSongReader
    {
        public const int SchemaVersion = 1;
        public const string FolderName = "songs";
        public const string FileSuffix = ".song.json";

        /// <summary>StreamingAssets/songs (engine call: Application.streamingAssetsPath).</summary>
        public static string SongsFolder
        {
            get { return Path.Combine(Application.streamingAssetsPath, FolderName); }
        }

        /// <summary>The file of a song id in <paramref name="folder"/>: folder/&lt;id&gt;.song.json.</summary>
        public static string PathFor(string folder, string songId)
        {
            return Path.Combine(folder ?? string.Empty, (songId ?? string.Empty) + FileSuffix);
        }

        /// <summary>Loads StreamingAssets/songs/&lt;id&gt;.song.json. Never throws.</summary>
        public static bool Load(string songId, out DevSong song, out string error)
        {
            return LoadFile(PathFor(SongsFolder, songId), out song, out error);
        }

        /// <summary>Loads one song file. Never throws.</summary>
        public static bool LoadFile(string path, out DevSong song, out string error)
        {
            song = null;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                error = "cannot read " + path + " (" + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }
            if (!Parse(text, out song, out error))
            {
                error = Path.GetFileName(path) + ": " + error;
                return false;
            }
            return true;
        }

        /// <summary>Parses song JSON text into a laid-out timeline. Never throws.</summary>
        public static bool Parse(string json, out DevSong song, out string error)
        {
            song = null;
            object root;
            if (!DevJson.TryParse(json, out root, out error))
            {
                error = "not valid JSON: " + error;
                return false;
            }
            if (!(root is Dictionary<string, object>))
            {
                error = "the top level is not an object";
                return false;
            }

            double schema;
            if (!DevJson.TryGetNumber(root, "schema", out schema) || (int)schema != SchemaVersion)
            {
                error = "schema must be " + SchemaVersion;
                return false;
            }

            var s = new DevSong();
            s.Id = StringOr(root, "id");
            s.Title = StringOr(root, "title");
            s.Artist = StringOr(root, "artist");
            s.License = StringOr(root, "license");
            s.Credit = StringOr(root, "credit");

            if (!DevJson.TryGetNumber(root, "tempoBpm", out s.TempoBpm) || !(s.TempoBpm > 0))
            {
                error = "tempoBpm is missing or not positive";
                return false;
            }

            List<object> signature;
            if (!DevJson.TryGetArray(root, "timeSignature", out signature) || signature.Count != 2 ||
                !(signature[0] is double) || !(signature[1] is double) || (double)signature[0] < 1 || (double)signature[1] < 1)
            {
                error = "timeSignature must be [beats per bar, beat unit]";
                return false;
            }
            s.BeatsPerBar = (int)(double)signature[0];
            s.BeatUnit = (int)(double)signature[1];

            double pickup;
            s.PickupBeats = DevJson.TryGetNumber(root, "pickupBeats", out pickup) && pickup > 0 ? pickup : 0;

            ReadPattern(root, "strum", out s.StrumSubdivision, out s.StrumSlots);
            ReadPattern(root, "pick", out s.PickSubdivision, out s.PickSlots);
            s.SlotsPerBeat = DevTimeline.SlotsPerBeat(s.StrumSlots != null ? s.StrumSlots.Length : 0, s.StrumSubdivision, s.BeatsPerBar);

            var lines = new List<DevSongLine>();
            List<object> sections;
            if (DevJson.TryGetArray(root, "sections", out sections))
            {
                for (int si = 0; si < sections.Count; si++)
                {
                    object section = sections[si];
                    string sectionName = StringOr(section, "name");
                    List<object> sectionLines;
                    if (!DevJson.TryGetArray(section, "lines", out sectionLines)) continue;
                    for (int li = 0; li < sectionLines.Count; li++) lines.Add(ReadLine(sectionLines[li], sectionName));
                }
            }

            s.Lines = lines.ToArray();
            s.StartBeat = -s.PickupBeats;
            s.EndBeat = DevTimeline.LayOut(s.Lines, s.StartBeat);
            s.Runs = DevTimeline.BuildRuns(s.Lines);
            if (s.Runs.Length == 0)
            {
                error = "the song has no chord with beats > 0";
                return false;
            }
            song = s;
            error = null;
            return true;
        }

        static DevSongLine ReadLine(object node, string sectionName)
        {
            var line = new DevSongLine();
            line.Section = sectionName ?? string.Empty;
            line.Lyric = StringOr(node, "lyric");
            bool words = line.HasLyric;
            var chords = new List<DevLineChord>();
            List<object> list;
            if (DevJson.TryGetArray(node, "chords", out list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var chord = new DevLineChord();
                    chord.Chord = StringOr(list[i], "chord");
                    double beats;
                    chord.Beats = DevJson.TryGetNumber(list[i], "beats", out beats) ? beats : 0;
                    double at;
                    chord.At = words && DevJson.TryGetNumber(list[i], "at", out at) && at >= 0 ? (int)at : -1;
                    chords.Add(chord);
                }
            }
            line.Chords = chords.ToArray();
            return line;
        }

        static void ReadPattern(object root, string key, out int subdivision, out string[] slots)
        {
            subdivision = 0;
            slots = null;
            Dictionary<string, object> pattern;
            if (!DevJson.TryGetObject(root, key, out pattern)) return;
            double sub;
            if (DevJson.TryGetNumber(pattern, "subdivision", out sub) && sub > 0) subdivision = (int)sub;
            List<object> list;
            if (!DevJson.TryGetArray(pattern, "slots", out list) || list.Count == 0) return;
            slots = new string[list.Count];
            for (int i = 0; i < list.Count; i++) slots[i] = list[i] as string ?? "-";
        }

        static string StringOr(object node, string key)
        {
            string v;
            return DevJson.TryGetString(node, key, out v) ? v : string.Empty;
        }
    }
}
