using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Practice;

namespace UntitledGuitarApp.Core.Coach
{
    /// <summary>Tunables for coaching. Each number says why it exists.</summary>
    public sealed class CoachConfig
    {
        /// <summary>A strum this far (seconds) from the chord start counts as a late/early change (about a third of a beat at 60 bpm).</summary>
        public double BadTimingSec = 0.35;
        /// <summary>Hit rate at or above this is a clean run.</summary>
        public double CleanHitRate = 0.9;
        /// <summary>Mean absolute error below this (seconds) is "on time" for a clean run.</summary>
        public double CleanMeanAbsSec = 0.20;
        /// <summary>Hit rate below this is a rough run: coach slows down instead of fine-tuning.</summary>
        public double RoughHitRate = 0.5;
        /// <summary>Card word limits: keep the HUD card readable at a glance.</summary>
        public int HeadlineMaxWords = 6;
        public int PraiseMaxWords = 14;
        public int FixMaxWords = 12;
        public int NextMaxWords = 12;
    }

    public enum ChangeProblem { None, Skipped, Missed, Late, Early }

    /// <summary>Compact, deterministic summary of one play-along run. Contains no lyrics and no personal data.</summary>
    public sealed class CoachInput
    {
        public string SongId = "", SongTitle = "";
        public double TempoBpm;
        /// <summary>Practice tempo as percent of the song's written tempo (e.g. 75).</summary>
        public double TempoPercent = 100;
        public int Total, Hits, Missed, Skipped;
        /// <summary>Signed mean of (strum - chord start) over chords with a strum; positive = late. 0 if none.</summary>
        public double MeanErrorSec;
        public double MeanAbsErrorSec;
        public int WaitHolds;
        public string WorstFrom = "", WorstTo = "";
        public ChangeProblem WorstProblem = ChangeProblem.None;

        public double HitRate => Total == 0 ? 0 : (double)Hits / Total;

        /// <summary>Summarise results. waitHolds = how many times wait mode held the playhead (caller counts it).</summary>
        public static CoachInput Build(string songId, string songTitle, double tempoBpm, double tempoPercent,
            IReadOnlyList<ChordResult> results, int waitHolds = 0, CoachConfig cfg = null)
        {
            cfg = cfg ?? new CoachConfig();
            var ci = new CoachInput
            {
                SongId = songId ?? "", SongTitle = songTitle ?? "",
                TempoBpm = Fin(tempoBpm), TempoPercent = tempoPercent > 0 && !double.IsNaN(tempoPercent) && !double.IsInfinity(tempoPercent) ? tempoPercent : 100,
                WaitHolds = Math.Max(0, waitHolds)
            };
            if (results == null) return ci;
            double sum = 0, abs = 0; int n = 0; double worstScore = 0;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r == null) continue;
                ci.Total++;
                if (r.Outcome == ChordOutcome.Hit) ci.Hits++;
                else if (r.Outcome == ChordOutcome.Missed) ci.Missed++;
                else if (r.Outcome == ChordOutcome.Skipped) ci.Skipped++;
                bool hasErr = !double.IsNaN(r.ErrorSec) && !double.IsInfinity(r.ErrorSec);
                if (hasErr) { sum += r.ErrorSec; abs += Math.Abs(r.ErrorSec); n++; }

                // Worst change = a transition between two different chords. Skipped > Missed > badly timed hit.
                if (i == 0 || results[i - 1] == null || results[i - 1].Chord == r.Chord) continue;
                double score = 0; var prob = ChangeProblem.None;
                if (r.Outcome == ChordOutcome.Skipped) { score = 30; prob = ChangeProblem.Skipped; }
                else if (r.Outcome == ChordOutcome.Missed) { score = 20; prob = ChangeProblem.Missed; }
                else if (r.Outcome == ChordOutcome.Hit && hasErr && Math.Abs(r.ErrorSec) >= cfg.BadTimingSec)
                {
                    score = Math.Min(Math.Abs(r.ErrorSec), 9.0);
                    prob = r.ErrorSec > 0 ? ChangeProblem.Late : ChangeProblem.Early;
                }
                if (score > worstScore) { worstScore = score; ci.WorstProblem = prob; ci.WorstFrom = results[i - 1].Chord ?? ""; ci.WorstTo = r.Chord ?? ""; }
            }
            if (n > 0) { ci.MeanErrorSec = sum / n; ci.MeanAbsErrorSec = abs / n; }
            return ci;
        }

        static double Fin(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
    }

    /// <summary>The coach card shown after a run.</summary>
    public sealed class CoachCard
    {
        public string Headline = "", Praise = "", Fix = "", Next = "";
        /// <summary>True when produced by RuleBasedCoach (the offline fallback).</summary>
        public bool FromFallback;
    }
}
