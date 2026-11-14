// PlayAlongMath: the small, pure conversions between Core's play-along timeline and what the XR side draws (PracticeFrame
// and the Hud/Play widgets). No engine types, no allocation except NextDifferent. Tested offline (PlayFlowTests).
//
// BEATS. Core's SongTimeline counts beats from the song's first chord (a pickup included: Happy Birthday's pickup is Core
// beat 0). PracticeFrame counts from the first DOWNBEAT, so a pickup is negative: frame beat = Core beat - pickupBeats.
// Bar lines are then at whole multiples of beatsPerBar from frame beat 0, which is also where Core's PatternClock starts
// bar 1 (pickup beats have no pattern slots).
using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Songs;

namespace UntitledGuitarApp.Runtime.Practice
{
    /// <summary>Pure helpers for the play-along adapter and presenter.</summary>
    public static class PlayAlongMath
    {
        /// <summary>Tolerance for "on the boundary" comparisons of beats.</summary>
        public const double Epsilon = 1e-9;

        /// <summary>Core beat (from the first chord) to PracticeFrame beat (from the first downbeat).</summary>
        public static double FrameBeat(double coreBeat, double pickupBeats)
        {
            return coreBeat - (pickupBeats > 0 ? pickupBeats : 0);
        }

        /// <summary>
        /// For every span, the index of the next span whose chord is DIFFERENT (-1 when none). Core's spans are one per chord
        /// of the file, so a chord that runs across a line break (Happy Birthday's D) is two spans; its "next chord" is the
        /// first span after both. Allocates the array once per song.
        /// </summary>
        public static int[] NextDifferent(IReadOnlyList<ChordSpan> spans)
        {
            int n = spans != null ? spans.Count : 0;
            var next = new int[n];
            int candidate = -1;
            for (int i = n - 1; i >= 0; i--)
            {
                if (i + 1 < n && !string.Equals(spans[i + 1].Chord, spans[i].Chord, StringComparison.Ordinal)) candidate = i + 1;
                next[i] = candidate;
            }
            return next;
        }

        /// <summary>
        /// Pattern slots per beat. Core's PatternClock: Subdivision = slots per bar, so this is subdivision / beatsPerBar
        /// (Riptide 8 in 4/4 and Happy Birthday 6 in 3/4 both give 2). Uneven or missing patterns give 1.
        /// </summary>
        public static int SlotsPerBeat(int subdivision, int beatsPerBar)
        {
            int bpb = beatsPerBar < 1 ? 1 : beatsPerBar;
            if (subdivision <= 0 || subdivision % bpb != 0) return 1;
            return Math.Max(1, subdivision / bpb);
        }

        /// <summary>The count-in number at <paramref name="frameBeat"/>: 4, 3, 2, 1 over the bar before the first chord, then 0.</summary>
        public static int CountInBeatsLeft(double frameBeat, double firstChordFrameBeat)
        {
            if (frameBeat >= firstChordFrameBeat - Epsilon) return 0;
            return (int)Math.Ceiling(firstChordFrameBeat - frameBeat - Epsilon);
        }

        /// <summary>
        /// Whole beats left before a change <paramref name="beatsToChange"/> beats away, counting the current beat (BeatDots.Set):
        /// 2.0 and 1.5 give 2, 0.5 gives 1, 0 gives 0 (due now). Infinity or NaN give 0; large values are capped at 64.
        /// </summary>
        public static int WholeBeatsLeft(double beatsToChange)
        {
            if (double.IsNaN(beatsToChange) || double.IsInfinity(beatsToChange) || beatsToChange <= Epsilon) return 0;
            return (int)Math.Min(64, Math.Ceiling(beatsToChange - Epsilon));
        }

        /// <summary>Position inside the bar, 0 up to beatsPerBar, for a frame beat (bar lines at multiples of beatsPerBar).</summary>
        public static double BeatInBar(double frameBeat, int beatsPerBar)
        {
            int bpb = beatsPerBar < 1 ? 1 : beatsPerBar;
            double r = frameBeat % bpb;
            if (r < 0) r += bpb;
            if (r >= bpb) r -= bpb;
            return r;
        }

        /// <summary>The pattern cursor for PatternStrip.SetCursor: slots since frame beat 0 (slot 0 lands on every bar line).</summary>
        public static double SlotPosition(double frameBeat, int slotsPerBeat)
        {
            return frameBeat * (slotsPerBeat < 1 ? 1 : slotsPerBeat);
        }

        /// <summary>
        /// The preset of <paramref name="presets"/> (the flags' tempoPresets) nearest to <paramref name="wanted"/>; a tie goes to
        /// the slower one; non-positive entries are ignored; <paramref name="wanted"/> itself when the list has none.
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
