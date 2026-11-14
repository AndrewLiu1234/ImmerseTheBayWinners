using System;

namespace UntitledGuitarApp.Core.Coach
{
    /// <summary>
    /// Always-available coach card. Pure function of CoachInput, deterministic, never throws.
    /// Tip style follows docs/MASTER_PLAN.md Appendix B6 (short, imperative, concrete: which chord, what to do).
    /// </summary>
    public static class RuleBasedCoach
    {
        public static CoachCard Make(CoachInput inp, CoachConfig cfg = null)
        {
            cfg = cfg ?? new CoachConfig();
            if (inp == null) inp = new CoachInput();
            var c = new CoachCard { FromFallback = true };

            if (inp.Total == 0)
            {
                c.Headline = "Ready when you are";
                c.Praise = "Picking up the guitar is the first win.";
                c.Fix = "Strum each chord as it arrives.";
                c.Next = "Start with a slow tempo.";
            }
            else if (inp.Skipped == inp.Total)
            {
                c.Headline = "Let's slow it down";
                c.Praise = "You showed up, and that counts.";
                c.Fix = "Press one chord shape, then strum it.";
                c.Next = "Try 60% tempo, first chord only.";
            }
            else if (inp.HitRate >= cfg.CleanHitRate && inp.MeanAbsErrorSec <= cfg.CleanMeanAbsSec)
            {
                c.Headline = "Clean run";
                c.Praise = "Nearly every chord landed on time.";
                c.Fix = inp.MeanErrorSec > 0.1 ? "Start each change a little earlier."
                      : inp.MeanErrorSec < -0.1 ? "Wait for the beat before you strum."
                      : "Keep your strumming steady and even.";
                c.Next = NextTempo(inp);
            }
            else
            {
                bool rough = inp.HitRate < cfg.RoughHitRate;
                c.Headline = rough ? "Good start, keep going" : "Getting there";
                c.Praise = inp.Hits > 0
                    ? "You landed " + inp.Hits + " of " + inp.Total + " chords."
                    : "You kept going, and that matters.";
                bool pair = inp.WorstProblem != ChangeProblem.None && inp.WorstFrom.Length > 0 && inp.WorstTo.Length > 0;
                string ft = pair ? inp.WorstFrom + " to " + inp.WorstTo : "";
                if (pair)
                {
                    switch (inp.WorstProblem)
                    {
                        case ChangeProblem.Late: c.Fix = "Start the " + ft + " change one beat earlier."; break;
                        case ChangeProblem.Early: c.Fix = "Settle the " + inp.WorstTo + " shape before you strum."; break;
                        case ChangeProblem.Skipped: c.Fix = "Place the " + inp.WorstTo + " shape first, then strum."; break;
                        default: c.Fix = "Slow down " + ft + ": lift all fingers together."; break;
                    }
                    c.Next = rough ? "Try 75% tempo, drill " + ft + "." : "Drill " + ft + " for one minute.";
                }
                else
                {
                    c.Fix = inp.MeanErrorSec > 0.1 ? "Start each change a little earlier." : "Strum right when the ring lands.";
                    c.Next = rough ? "Try 75% tempo." : NextTempo(inp);
                }
            }
            c.Headline = Clip(c.Headline, cfg.HeadlineMaxWords);
            c.Praise = Clip(c.Praise, cfg.PraiseMaxWords);
            c.Fix = Clip(c.Fix, cfg.FixMaxWords);
            c.Next = Clip(c.Next, cfg.NextMaxWords);
            return c;
        }

        static string NextTempo(CoachInput inp)
        {
            if (inp.TempoPercent <= 75) return "Try 90% tempo.";
            if (inp.TempoPercent < 100) return "Try full tempo.";
            return "Try a new song.";
        }

        static readonly char[] Ws = { ' ', '\t', '\r', '\n' };

        /// <summary>Safety net: cut to maxWords so a card never overflows the HUD.</summary>
        internal static string Clip(string s, int maxWords)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var w = s.Split(Ws, StringSplitOptions.RemoveEmptyEntries);
            return w.Length <= maxWords ? string.Join(" ", w) : string.Join(" ", w, 0, maxWords);
        }

        public static int WordCount(string s) =>
            string.IsNullOrWhiteSpace(s) ? 0 : s.Split(Ws, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
