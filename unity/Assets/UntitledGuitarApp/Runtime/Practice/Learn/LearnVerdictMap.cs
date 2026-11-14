// LearnVerdictMap: Core's verdict classes -> the Learn HUD's display-side marks and icons (Runtime/Hud/Learn/LearnTypes.cs).
// The mapping follows the plan's sketch (docs/MASTER_PLAN.md section 18): Correct -> Correct; OpenStringInstead,
// WrongFretLow, WrongFretHigh, WrongString, ExtraString -> Wrong; Muted -> Muted; Unclear -> Unclear; ShortRing -> LetRing.
// Icons follow the tip table (Appendix B6): Muted -> Arch; OpenStringInstead -> Press; WrongFret* / WrongString -> Move;
// ExtraString -> Skip; Unclear -> Again; ShortRing -> Ring; WrongChord -> Mismatch; clean -> Good. Pure, no engine types.
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Runtime.Hud.Learn;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    public static class LearnVerdictMap
    {
        /// <summary>The neck-column mark for a pluck verdict (PluckCheck). Correct is the only pass; ShortRing is its own shape.</summary>
        public static StringMark MarkForPluck(PluckVerdict verdict)
        {
            switch (verdict)
            {
                case PluckVerdict.Correct: return StringMark.Correct;
                case PluckVerdict.OpenStringInstead:
                case PluckVerdict.WrongFretLow:
                case PluckVerdict.WrongFretHigh:
                case PluckVerdict.WrongString:
                case PluckVerdict.ExtraString: return StringMark.Wrong;
                case PluckVerdict.Muted: return StringMark.Muted;
                case PluckVerdict.ShortRing: return StringMark.LetRing;
                default: return StringMark.Unclear;
            }
        }

        /// <summary>The tip icon for a pluck verdict.</summary>
        public static TipIcon IconForPluck(PluckVerdict verdict)
        {
            switch (verdict)
            {
                case PluckVerdict.Correct: return TipIcon.Good;
                case PluckVerdict.OpenStringInstead: return TipIcon.Press;
                case PluckVerdict.WrongFretLow:
                case PluckVerdict.WrongFretHigh:
                case PluckVerdict.WrongString: return TipIcon.Move;
                case PluckVerdict.Muted: return TipIcon.Arch;
                case PluckVerdict.ExtraString: return TipIcon.Skip;
                case PluckVerdict.ShortRing: return TipIcon.Ring;
                default: return TipIcon.Again;
            }
        }

        /// <summary>The tip icon for a strum-check result (clean -> Good, wrong chord -> Mismatch, one-string issues reuse the pluck icons).</summary>
        public static TipIcon IconForStrum(in StrumCheckResult result)
        {
            if (result.Clean) return TipIcon.Good;
            switch (result.Issue)
            {
                case StrumIssue.WrongChord: return TipIcon.Mismatch;
                case StrumIssue.ExtraString: return TipIcon.Skip;
                case StrumIssue.OpenStringInstead: return TipIcon.Press;
                case StrumIssue.WrongFretLow:
                case StrumIssue.WrongFretHigh: return TipIcon.Move;
                case StrumIssue.Muted: return TipIcon.Arch;
                default: return TipIcon.Again;
            }
        }
    }
}
