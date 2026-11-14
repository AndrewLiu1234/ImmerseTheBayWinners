using System;
using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>Tunables for timing judgement. Each number says why it exists.</summary>
    public sealed class PatternJudgeConfig
    {
        /// <summary>An onset within this of an expected slot counts as that slot (150 ms = loose enough for a beginner, tight enough to not steal the neighbor).</summary>
        public double MatchWindowSec = 0.150;
        /// <summary>The window never exceeds this fraction of the slot spacing, so a slot cannot claim its neighbor's onset at fast tempi.</summary>
        public double MaxWindowSlotFraction = 0.45;
        /// <summary>|offset| at or under this is "on time" (60 ms is about where a listener starts to hear a strum as off the beat).</summary>
        public double OnTimeSec = 0.060;
        /// <summary>Median offset beyond this is a bias worth telling the player about ("rushing"/"dragging"); under it, "steady".</summary>
        public double BiasSec = 0.030;
        /// <summary>Fewer matched hits than this and feedback is "notEnough" (a median of 2 strums is noise).</summary>
        public int MinHitsForFeedback = 4;
        /// <summary>Audio latency to subtract from onset times, ms (flags audio.latencyOffsetMs). Positive = detections arrive late.</summary>
        public double LatencyOffsetMs = 0;
    }

    public readonly struct TimingHit
    {
        public readonly int SlotIndex;
        public readonly double OnsetSec, OffsetMs; // OffsetMs: negative = early, positive = late
        public TimingHit(int slot, double onset, double offsetMs) { SlotIndex = slot; OnsetSec = onset; OffsetMs = offsetMs; }
    }

    /// <summary>Result of one judging pass. FeedbackKey: "steady" | "rushing" | "dragging" | "notEnough".</summary>
    public sealed class TimingReport
    {
        public readonly List<TimingHit> Hits = new List<TimingHit>();
        public readonly List<int> MissedSlots = new List<int>();
        public int ExpectedSlots, ExtraOnsets;
        public double MeanOffsetMs, MedianOffsetMs, MeanAbsOffsetMs, OnTimePercent;
        public string FeedbackKey = "notEnough";
    }

    /// <summary>
    /// Matches detected onset times to the expected non-rest slots of a PatternClock and reports how early/late the player is.
    /// Timing only: strum direction (D vs U) is not reliably detectable from audio, so the slot symbol is never judged.
    /// Onsets and slot times share one clock (seconds from song start). Garbage (NaN/Inf/unsorted) is tolerated, never throws.
    /// </summary>
    public sealed class PatternJudge
    {
        readonly PatternClock _clock;
        readonly PatternJudgeConfig _cfg;
        public PatternJudge(PatternClock clock, PatternJudgeConfig config = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _cfg = config ?? new PatternJudgeConfig();
        }

        /// <summary>Judge onsets against slots whose time lies in [fromSec, toSec]; toSec = +inf judges the whole song.</summary>
        public TimingReport Judge(IReadOnlyList<double> onsetsSec, double fromSec = 0, double toSec = double.PositiveInfinity)
        {
            var rep = new TimingReport();
            if (double.IsNaN(fromSec)) fromSec = 0;
            if (double.IsNaN(toSec)) toSec = double.PositiveInfinity;
            double window = Math.Min(_cfg.MatchWindowSec, _cfg.MaxWindowSlotFraction * _clock.SlotSeconds);
            if (!(window > 0)) window = _cfg.MatchWindowSec;

            var slots = _clock.Slots;
            var expected = new List<int>();
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].IsRest && slots[i].TimeSeconds >= fromSec && slots[i].TimeSeconds <= toSec) expected.Add(i);
            rep.ExpectedSlots = expected.Count;

            var on = new List<double>();
            double lat = _cfg.LatencyOffsetMs / 1000.0;
            if (onsetsSec != null)
                for (int i = 0; i < onsetsSec.Count; i++)
                {
                    double v = onsetsSec[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    v -= lat;
                    if (v < fromSec - window || v > toSec + window) continue;
                    on.Add(v);
                }
            on.Sort();

            var bestOnset = new double[expected.Count];
            var has = new bool[expected.Count];
            foreach (double o in on)
            {
                int k = Nearest(expected, slots, o);
                if (k < 0 || Math.Abs(o - slots[expected[k]].TimeSeconds) > window) { rep.ExtraOnsets++; continue; }
                double t = slots[expected[k]].TimeSeconds;
                if (!has[k]) { has[k] = true; bestOnset[k] = o; }
                else
                {
                    if (Math.Abs(o - t) < Math.Abs(bestOnset[k] - t)) bestOnset[k] = o;
                    rep.ExtraOnsets++; // the second onset on one slot is extra either way
                }
            }

            var offs = new List<double>();
            int onTime = 0; double sum = 0, absSum = 0;
            for (int k = 0; k < expected.Count; k++)
            {
                if (!has[k]) { rep.MissedSlots.Add(expected[k]); continue; }
                double ms = (bestOnset[k] - slots[expected[k]].TimeSeconds) * 1000.0;
                rep.Hits.Add(new TimingHit(expected[k], bestOnset[k] + lat, ms));
                offs.Add(ms); sum += ms; absSum += Math.Abs(ms);
                if (Math.Abs(ms) <= _cfg.OnTimeSec * 1000.0) onTime++;
            }
            int n = offs.Count;
            if (n > 0)
            {
                rep.MeanOffsetMs = sum / n; rep.MeanAbsOffsetMs = absSum / n;
                offs.Sort();
                rep.MedianOffsetMs = n % 2 == 1 ? offs[n / 2] : 0.5 * (offs[n / 2 - 1] + offs[n / 2]);
                rep.OnTimePercent = 100.0 * onTime / n;
            }
            if (n >= _cfg.MinHitsForFeedback)
            {
                double bias = _cfg.BiasSec * 1000.0;
                rep.FeedbackKey = rep.MedianOffsetMs < -bias ? "rushing" : rep.MedianOffsetMs > bias ? "dragging" : "steady";
            }
            return rep;
        }

        static int Nearest(List<int> expected, IReadOnlyList<PatternSlot> slots, double t)
        {
            int lo = 0, hi = expected.Count - 1;
            if (hi < 0) return -1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (slots[expected[mid]].TimeSeconds < t) lo = mid + 1; else hi = mid;
            }
            if (lo > 0 && Math.Abs(slots[expected[lo - 1]].TimeSeconds - t) <= Math.Abs(slots[expected[lo]].TimeSeconds - t)) lo--;
            return lo;
        }
    }
}
