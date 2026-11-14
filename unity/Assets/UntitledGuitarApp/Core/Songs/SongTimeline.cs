using System;
using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Songs
{
    /// <summary>One chord span, flattened from sections/lines. Beats are from song start (pickup included in the first chord).</summary>
    public sealed class ChordSpan
    {
        public int Index;
        public string Chord;
        public double StartBeat, Beats, EndBeat;
        public double StartSeconds, EndSeconds;
        public int SectionIndex, LineIndex, At;
        public string Lyric;
    }

    /// <summary>Answer to "what is playing at time t". Current/Next are -1 when absent (before start / last chord / after end).</summary>
    public readonly struct TimelineQuery
    {
        public readonly int CurrentIndex, NextIndex;
        public readonly double BeatsUntilChange, Beat;
        public readonly int Bar, BeatInBar; // Bar 0 = pickup; both 1-based afterwards
        public readonly bool Finished;
        public TimelineQuery(int cur, int next, double until, double beat, int bar, int beatInBar, bool finished)
        { CurrentIndex = cur; NextIndex = next; BeatsUntilChange = until; Beat = beat; Bar = bar; BeatInBar = beatInBar; Finished = finished; }
    }

    /// <summary>
    /// Chord events in beats and seconds for one tempo scale (0.5 = half speed). Pure; time (seconds from song start) is passed in.
    /// tempoBpm counts quarter-note beats; both supported signatures (3/4, 4/4) use quarter-note beats.
    /// </summary>
    public sealed class SongTimeline
    {
        public readonly IReadOnlyList<ChordSpan> Spans;
        public readonly double TempoScale, SecondsPerBeat, TotalBeats, TotalSeconds, PickupBeats;
        public readonly int BeatsPerBar;

        public SongTimeline(Song song, double tempoScale)
        {
            if (song == null) throw new ArgumentNullException(nameof(song));
            if (!(tempoScale > 0)) throw new ArgumentOutOfRangeException(nameof(tempoScale), "tempo scale must be > 0");
            TempoScale = tempoScale;
            BeatsPerBar = song.BeatsPerBar;
            PickupBeats = song.PickupBeats;
            SecondsPerBeat = 60.0 / (song.TempoBpm * tempoScale);
            var list = new List<ChordSpan>();
            double beat = 0;
            for (int si = 0; si < song.Sections.Count; si++)
                for (int li = 0; li < song.Sections[si].Lines.Count; li++)
                {
                    var line = song.Sections[si].Lines[li];
                    foreach (var c in line.Chords)
                    {
                        list.Add(new ChordSpan
                        {
                            Index = list.Count, Chord = c.Chord, StartBeat = beat, Beats = c.Beats, EndBeat = beat + c.Beats,
                            StartSeconds = beat * SecondsPerBeat, EndSeconds = (beat + c.Beats) * SecondsPerBeat,
                            SectionIndex = si, LineIndex = li, At = c.At, Lyric = line.Lyric
                        });
                        beat += c.Beats;
                    }
                }
            Spans = list;
            TotalBeats = beat;
            TotalSeconds = beat * SecondsPerBeat;
        }

        /// <summary>State at tSeconds. t &lt; 0 = count-in (current -1, next = first chord).</summary>
        public TimelineQuery At(double tSeconds)
        {
            if (double.IsNaN(tSeconds)) tSeconds = 0;
            double beat = tSeconds / SecondsPerBeat;
            int n = Spans.Count;
            if (n == 0) return new TimelineQuery(-1, -1, 0, beat, 0, 1, true);
            if (beat < 0) return new TimelineQuery(-1, 0, -beat, beat, BarOf(beat, out int bb0), bb0, false);
            if (beat >= TotalBeats) return new TimelineQuery(-1, -1, 0, beat, BarOf(beat, out int bb1), bb1, true);
            int cur = 0;
            for (int i = 0; i < n; i++) if (beat >= Spans[i].StartBeat && beat < Spans[i].EndBeat) { cur = i; break; }
            int bar = BarOf(beat, out int bib);
            return new TimelineQuery(cur, cur + 1 < n ? cur + 1 : -1, Spans[cur].EndBeat - beat, beat, bar, bib, false);
        }

        // Pickup beats sit at the end of "bar 0": a 1-beat pickup in 3/4 starts on beat 3.
        int BarOf(double beat, out int beatInBar)
        {
            double rel = beat - PickupBeats;
            if (rel < 0) { beatInBar = Math.Max(1, BeatsPerBar + (int)Math.Floor(rel) + 1); return 0; }
            beatInBar = (int)Math.Floor(rel % BeatsPerBar) + 1;
            return (int)Math.Floor(rel / BeatsPerBar) + 1;
        }
    }
}
