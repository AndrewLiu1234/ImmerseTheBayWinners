// TEMPORARY (dev play-along driver). Core (Person 2: Core/Songs) owns the real song model; until it lands, DevSongReader
// turns a StreamingAssets/songs/<id>.song.json file (schema 1, docs/SONGS.md) into this flat timeline, and
// DevPlayAlongClock plays it into Runtime/Practice/PracticeFrame. When Core lands, an adapter fills PracticeFrame from Core's
// PlayAlongSession and this folder is deleted.
//
// TIME. Beats. Beat 0 is the first downbeat (the first full bar); a pickup (Happy Birthday: pickupBeats 1) starts before
// it, so the first chord begins at StartBeat = -pickupBeats. Bar lines are at whole multiples of BeatsPerBar from beat 0.
// The one-bar count-in runs on the BeatsPerBar beats before StartBeat (so it is always on negative beats).
//
// All the maths is in DevTimeline (pure static, no engine types), so it is tested offline (Tests/EditMode/PlayFlowTests.cs).
using System;

namespace UntitledGuitarApp.Runtime.Practice.Dev
{
    /// <summary>TEMPORARY. One chord of a line exactly as the file writes it (neighbours with the same name are not merged here).</summary>
    public sealed class DevLineChord
    {
        /// <summary>Chord name ("G").</summary>
        public string Chord;

        /// <summary>How long it lasts, in beats.</summary>
        public double Beats;

        /// <summary>Character index in the lyric above which the name is drawn; -1 when the line has no words or no "at".</summary>
        public int At = -1;

        /// <summary>Song beat at which it starts.</summary>
        public double StartBeat;
    }

    /// <summary>TEMPORARY. One line of the song: its words (empty for chords-only songs) and its chords.</summary>
    public sealed class DevSongLine
    {
        /// <summary>The section it belongs to ("Intro", "Loop", "Song").</summary>
        public string Section = string.Empty;

        /// <summary>The words; empty for a chords-only line. Only public-domain songs have any (docs/SONGS.md).</summary>
        public string Lyric = string.Empty;

        /// <summary>Song beat at which the line's first chord starts.</summary>
        public double StartBeat;

        /// <summary>Length of the line in beats (the sum of its chords).</summary>
        public double Beats;

        /// <summary>The chords, in playing order.</summary>
        public DevLineChord[] Chords = new DevLineChord[0];

        /// <summary>True when the line has words to show.</summary>
        public bool HasLyric { get { return !string.IsNullOrWhiteSpace(Lyric); } }

        /// <summary>Song beat at which the line ends.</summary>
        public double EndBeat { get { return StartBeat + Beats; } }
    }

    /// <summary>
    /// TEMPORARY. A stretch of one chord. Consecutive file chords with the same name (Happy Birthday's D that runs across a
    /// line break) are merged, so two neighbouring runs always differ: a run start IS a chord change.
    /// </summary>
    public struct DevChordRun
    {
        /// <summary>Chord name.</summary>
        public string Chord;

        /// <summary>Song beat at which it starts.</summary>
        public double StartBeat;

        /// <summary>Length in beats.</summary>
        public double Beats;

        /// <summary>Index of the line in which the run starts.</summary>
        public int Line;

        /// <summary>Song beat at which it ends (the next change, or the end of the song).</summary>
        public double EndBeat { get { return StartBeat + Beats; } }
    }

    /// <summary>TEMPORARY. A whole song as a flat timeline (see the file header for the beat convention).</summary>
    public sealed class DevSong
    {
        public string Id = string.Empty;
        public string Title = string.Empty;
        public string Artist = string.Empty;
        public string License = string.Empty;
        public string Credit = string.Empty;

        /// <summary>Tempo at 100%, beats per minute.</summary>
        public double TempoBpm = 100;

        /// <summary>Time-signature numerator (3 or 4), at least 1.</summary>
        public int BeatsPerBar = 4;

        /// <summary>Time-signature denominator (4).</summary>
        public int BeatUnit = 4;

        /// <summary>Beats before the first full bar (0 unless the song has a pickup).</summary>
        public double PickupBeats;

        /// <summary>strum.subdivision as written in the file.</summary>
        public int StrumSubdivision;

        /// <summary>One bar of the strumming pattern ("D", "U", "-"); null when the file has none. Shared: never modify.</summary>
        public string[] StrumSlots;

        /// <summary>pick.subdivision as written in the file.</summary>
        public int PickSubdivision;

        /// <summary>One bar of the picking pattern ("B", "1".."6"); null when the file has none. Shared: never modify.</summary>
        public string[] PickSlots;

        /// <summary>Strum pattern slots per beat (DevTimeline.SlotsPerBeat): 2 for all three songs. At least 1.</summary>
        public int SlotsPerBeat = 1;

        /// <summary>The lines in playing order.</summary>
        public DevSongLine[] Lines = new DevSongLine[0];

        /// <summary>The chord runs in playing order (neighbours always differ).</summary>
        public DevChordRun[] Runs = new DevChordRun[0];

        /// <summary>Song beat of the first chord: -PickupBeats.</summary>
        public double StartBeat;

        /// <summary>Song beat at which the last chord ends.</summary>
        public double EndBeat;

        /// <summary>First beat of the one-bar count-in: StartBeat - BeatsPerBar.</summary>
        public double CountInStartBeat { get { return StartBeat - BeatsPerBar; } }

        /// <summary>True when at least one line has words.</summary>
        public bool HasLyrics
        {
            get
            {
                for (int i = 0; i < Lines.Length; i++)
                {
                    if (Lines[i].HasLyric) return true;
                }
                return false;
            }
        }
    }

    /// <summary>TEMPORARY. The timeline maths, pure and static (no engine types, no allocation except in the Build methods).</summary>
    public static class DevTimeline
    {
        /// <summary>Tolerance for "on the boundary" comparisons of beats.</summary>
        public const double Epsilon = 1e-9;

        /// <summary>
        /// Lays the lines end to end from <paramref name="startBeat"/>: sets every line's and chord's StartBeat and every
        /// line's Beats. Returns the beat at which the last line ends. Chords with no positive length take no time.
        /// </summary>
        public static double LayOut(DevSongLine[] lines, double startBeat)
        {
            double beat = startBeat;
            if (lines == null) return beat;
            for (int l = 0; l < lines.Length; l++)
            {
                DevSongLine line = lines[l];
                line.StartBeat = beat;
                double length = 0;
                for (int c = 0; c < line.Chords.Length; c++)
                {
                    DevLineChord chord = line.Chords[c];
                    chord.StartBeat = beat + length;
                    if (chord.Beats > 0) length += chord.Beats;
                }
                line.Beats = length;
                beat += length;
            }
            return beat;
        }

        /// <summary>
        /// The chord runs of laid-out lines: consecutive chords with the same name merge into one run; chords with no
        /// positive length or no name are skipped. Call after <see cref="LayOut"/>.
        /// </summary>
        public static DevChordRun[] BuildRuns(DevSongLine[] lines)
        {
            var runs = new System.Collections.Generic.List<DevChordRun>();
            if (lines == null) return runs.ToArray();
            for (int l = 0; l < lines.Length; l++)
            {
                DevLineChord[] chords = lines[l].Chords;
                for (int c = 0; c < chords.Length; c++)
                {
                    DevLineChord chord = chords[c];
                    if (!(chord.Beats > 0) || string.IsNullOrEmpty(chord.Chord)) continue;
                    int last = runs.Count - 1;
                    if (last >= 0 && string.Equals(runs[last].Chord, chord.Chord, StringComparison.Ordinal))
                    {
                        DevChordRun merged = runs[last];
                        merged.Beats += chord.Beats;
                        runs[last] = merged;
                        continue;
                    }
                    runs.Add(new DevChordRun { Chord = chord.Chord, StartBeat = chord.StartBeat, Beats = chord.Beats, Line = l });
                }
            }
            return runs.ToArray();
        }

        /// <summary>
        /// Index of the run that sounds at <paramref name="beat"/> (start inclusive, end exclusive); -1 before the first run,
        /// runs.Length at or after the end of the last one. Binary search, no allocation.
        /// </summary>
        public static int RunIndexAt(DevChordRun[] runs, double beat)
        {
            if (runs == null || runs.Length == 0) return 0;
            if (beat < runs[0].StartBeat - Epsilon) return -1;
            if (beat >= runs[runs.Length - 1].EndBeat - Epsilon) return runs.Length;
            int lo = 0, hi = runs.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (runs[mid].StartBeat <= beat + Epsilon) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Index of the line at <paramref name="beat"/>, clamped to the first and the last line (0 for no lines).</summary>
        public static int LineIndexAt(DevSongLine[] lines, double beat)
        {
            if (lines == null || lines.Length == 0) return 0;
            for (int i = lines.Length - 1; i > 0; i--)
            {
                if (beat >= lines[i].StartBeat - Epsilon) return i;
            }
            return 0;
        }

        /// <summary>
        /// Beats from <paramref name="beat"/> to the start of the run after <paramref name="runIndex"/> (the next DIFFERENT
        /// chord, because neighbouring runs always differ); +infinity when there is none. Never negative.
        /// </summary>
        public static double BeatsToNextChange(DevChordRun[] runs, int runIndex, double beat)
        {
            int next = runIndex + 1;
            if (runs == null || next < 0 || next >= runs.Length) return double.PositiveInfinity;
            return Math.Max(0, runs[next].StartBeat - beat);
        }

        /// <summary>
        /// The count-in number at <paramref name="beat"/>: 4, 3, 2, 1 over the bar before <paramref name="startBeat"/> (the
        /// first chord), 0 from the first chord on. It changes exactly on the beat.
        /// </summary>
        public static int CountInBeatsLeft(double beat, double startBeat)
        {
            if (beat >= startBeat - Epsilon) return 0;
            return (int)Math.Ceiling(startBeat - beat - Epsilon);
        }

        /// <summary>
        /// Whole beats left until a change <paramref name="beatsToChange"/> beats away, counting the current beat, as the
        /// beat-dot countdown wants it (BeatDots.Set): 2.0 and 1.5 give 2, 0.5 gives 1, 0 gives 0 (due now). Infinity or NaN
        /// gives 0; large values are capped at 64.
        /// </summary>
        public static int WholeBeatsLeft(double beatsToChange)
        {
            if (double.IsNaN(beatsToChange) || double.IsInfinity(beatsToChange) || beatsToChange <= Epsilon) return 0;
            return (int)Math.Min(64, Math.Ceiling(beatsToChange - Epsilon));
        }

        /// <summary>Position inside the bar, 0 up to beatsPerBar (bar lines at whole multiples of beatsPerBar from beat 0).</summary>
        public static double BeatInBar(double beat, int beatsPerBar)
        {
            int bpb = beatsPerBar < 1 ? 1 : beatsPerBar;
            double r = beat % bpb;
            if (r < 0) r += bpb;
            if (r >= bpb) r -= bpb;
            return r;
        }

        /// <summary>
        /// Pattern slots per beat. One pattern is one bar, so it is the slot count over the beats in a bar (Riptide: 8 slots
        /// in 4/4 = 2; Happy Birthday: 6 slots in 3/4 = 2). When that does not divide evenly, falls back to subdivision / 4
        /// (the note value: 8 = eighths). Always at least 1.
        /// </summary>
        public static int SlotsPerBeat(int slotCount, int subdivision, int beatsPerBar)
        {
            int bpb = beatsPerBar < 1 ? 1 : beatsPerBar;
            if (slotCount > 0 && slotCount % bpb == 0) return Math.Max(1, slotCount / bpb);
            if (subdivision >= 4) return Math.Max(1, subdivision / 4);
            return 1;
        }

        /// <summary>The pattern cursor (PatternStrip.SetCursor): slots since beat 0. Whole numbers are the strokes; it wraps per bar.</summary>
        public static double SlotPosition(double beat, int slotsPerBeat)
        {
            return beat * (slotsPerBeat < 1 ? 1 : slotsPerBeat);
        }

        /// <summary>Beats per second for a tempo and a tempo preset (0.75 = 75%). 0 for non-positive input.</summary>
        public static float BeatsPerSecond(double tempoBpm, float tempoPreset)
        {
            if (!(tempoBpm > 0) || !(tempoPreset > 0)) return 0f;
            return (float)(tempoBpm / 60.0 * tempoPreset);
        }

        /// <summary>
        /// The preset of <paramref name="presets"/> nearest to <paramref name="wanted"/> (a tie goes to the slower one).
        /// <paramref name="wanted"/> itself when the list is null or empty. Non-positive presets are ignored.
        /// </summary>
        public static float NearestPreset(float wanted, float[] presets)
        {
            if (presets == null || presets.Length == 0) return wanted;
            float best = float.NaN;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < presets.Length; i++)
            {
                float p = presets[i];
                if (!(p > 0f) || float.IsInfinity(p)) continue;
                float distance = Math.Abs(p - wanted);
                if (distance < bestDistance - 1e-6f || (Math.Abs(distance - bestDistance) <= 1e-6f && p < best))
                {
                    best = p;
                    bestDistance = distance;
                }
            }
            return float.IsNaN(best) ? wanted : best;
        }
    }
}
