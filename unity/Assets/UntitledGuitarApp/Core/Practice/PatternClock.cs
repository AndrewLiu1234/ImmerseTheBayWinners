using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>
    /// One slot of the strum/pick pattern, as the strumming hand and the pattern strip consume it (PatternSlot(index, D|U|string)).
    /// Symbol: "D", "U", "-" (rest) for strum; "B" or "1".."6" for pick. String: for pick slots the resolved string (B = the current
    /// chord's BassString), else 0. Index counts from the first slot of bar 1 (pickup beats have no slots).
    /// </summary>
    public readonly struct PatternSlot
    {
        public readonly int Index, Bar, SlotInBar, ChordIndex, String;
        public readonly string Symbol;
        public readonly double TimeSeconds;
        public readonly bool IsRest;
        public PatternSlot(int index, int bar, int slotInBar, string symbol, int stringNumber, int chordIndex, double timeSeconds)
        {
            Index = index; Bar = bar; SlotInBar = slotInBar; Symbol = symbol; String = stringNumber; ChordIndex = chordIndex;
            TimeSeconds = timeSeconds; IsRest = symbol == "-";
        }
    }

    /// <summary>
    /// Slot grid of a song's strum or pick pattern at one tempo scale. Pattern.Subdivision = slots per bar; bar 1 starts after the
    /// pickup (the pickup beats are not patterned: beginners just play the chord). Slots run to the end of the last chord.
    /// Pure; time is seconds from song start (same clock as SongTimeline).
    /// </summary>
    public sealed class PatternClock
    {
        readonly PatternSlot[] _slots;
        readonly double _slotSec, _firstSec;

        public IReadOnlyList<PatternSlot> Slots => _slots;
        public double SlotSeconds => _slotSec;
        public bool IsPick { get; }

        public PatternClock(Song song, SongTimeline timeline, bool pick)
        {
            if (song == null) throw new ArgumentNullException(nameof(song));
            if (timeline == null) throw new ArgumentNullException(nameof(timeline));
            IsPick = pick;
            var pat = pick ? song.Pick : song.Strum;
            if (pat == null || pat.Subdivision <= 0 || pat.Slots.Count != pat.Subdivision)
            { _slots = new PatternSlot[0]; _slotSec = 0; _firstSec = 0; return; }
            double slotBeats = (double)song.BeatsPerBar / pat.Subdivision;
            _slotSec = slotBeats * timeline.SecondsPerBeat;
            _firstSec = song.PickupBeats * timeline.SecondsPerBeat;
            var list = new List<PatternSlot>();
            for (int i = 0; ; i++)
            {
                double t = _firstSec + i * _slotSec;
                if (t >= timeline.TotalSeconds - 1e-9) break;
                int sIn = i % pat.Subdivision;
                string sym = pat.Slots[sIn] ?? "-";
                int chord = timeline.At(t + 1e-9).CurrentIndex;
                list.Add(new PatternSlot(i, i / pat.Subdivision + 1, sIn, sym, ResolveString(sym, timeline, chord), chord, t));
            }
            _slots = list.ToArray();
        }

        static int ResolveString(string sym, SongTimeline tl, int chordIndex)
        {
            if (sym == "B")
            {
                if (chordIndex >= 0 && ChordLibrary.TryGet(tl.Spans[chordIndex].Chord, out var shape)) return shape.BassString;
                return 6;
            }
            if (sym.Length == 1 && sym[0] >= '1' && sym[0] <= '6') return sym[0] - '0';
            return 0;
        }

        /// <summary>Index of the slot whose window [time, time+slot) contains t; -1 before bar 1 or after the last slot (NaN-safe).</summary>
        public int ActiveIndex(double tSeconds)
        {
            if (_slots.Length == 0 || double.IsNaN(tSeconds) || double.IsInfinity(tSeconds) || tSeconds < _firstSec) return -1;
            int i = (int)Math.Floor((tSeconds - _firstSec) / _slotSec + 1e-9);
            return i >= 0 && i < _slots.Length ? i : -1;
        }

        public bool TryActive(double tSeconds, out PatternSlot slot)
        {
            int i = ActiveIndex(tSeconds);
            slot = i >= 0 ? _slots[i] : default;
            return i >= 0;
        }
    }
}
