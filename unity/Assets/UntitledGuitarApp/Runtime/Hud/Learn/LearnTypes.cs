using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// What a per-string mark says. Display-side only: an adapter maps Core's verdict to one of these after the contracts
    /// freeze. Suggested mapping from the plan's sketch (docs/MASTER_PLAN.md section 18): Correct -> Correct;
    /// OpenStringInstead, WrongFretLow, WrongFretHigh, WrongString, ExtraString -> Wrong; Muted -> Muted; Unclear -> Unclear;
    /// the ring-out quality fault ("let string N ring") -> LetRing. Every value has its own shape as well as its own
    /// colour, so colour never carries the meaning alone.
    /// </summary>
    public enum StringMark
    {
        /// <summary>No mark (hides the mark for that string).</summary>
        None = 0,
        /// <summary>The right note rang: a green check.</summary>
        Correct = 1,
        /// <summary>A wrong note, or a string that should stay silent: a red-orange cross.</summary>
        Wrong = 2,
        /// <summary>The string did not ring: a red-orange flat bar ("flat line").</summary>
        Muted = 3,
        /// <summary>The app could not tell: an amber question mark ("pluck it again").</summary>
        Unclear = 4,
        /// <summary>The note was right but cut short: amber sound waves ("let it ring").</summary>
        LetRing = 5,
    }

    /// <summary>
    /// The icon drawn before a tip (icon first, then 7 words or fewer). Suggested mapping from the tip table
    /// (docs/MASTER_PLAN.md Appendix B6): Muted -> Arch; OpenStringInstead -> Press; WrongFret*, WrongString -> Move;
    /// ExtraString -> Skip; Unclear -> Again; let ring -> Ring; wrong chord -> Mismatch; late change -> Timing; clean -> Good.
    /// </summary>
    public enum TipIcon
    {
        None = 0,
        /// <summary>An arched finger over a string.</summary>
        Arch = 1,
        /// <summary>An arrow pressing down onto a line.</summary>
        Press = 2,
        /// <summary>A two-headed arrow: slide to another fret or string.</summary>
        Move = 3,
        /// <summary>A circle with a slash: do not play this.</summary>
        Skip = 4,
        /// <summary>A circular arrow: once more.</summary>
        Again = 5,
        /// <summary>Sound waves: let it ring.</summary>
        Ring = 6,
        /// <summary>A not-equal sign: heard something other than the target.</summary>
        Mismatch = 7,
        /// <summary>A clock: timing.</summary>
        Timing = 8,
        /// <summary>A check: well done.</summary>
        Good = 9,
    }

    /// <summary>Every shape the Learn HUD draws as geometry (the built-in font has no check or cross glyphs).</summary>
    public enum LearnGlyph
    {
        Check = 0,
        Cross = 1,
        Bar = 2,
        Question = 3,
        Waves = 4,
        Arch = 5,
        Press = 6,
        Move = 7,
        Skip = 8,
        Again = 9,
        Mismatch = 10,
        Timing = 11,
        ArrowUp = 12,
        ArrowDown = 13,
        Disc = 14,
        Ring = 15,
        Target = 16,
        DialTrack = 17,
        DialZone = 18,
        DialNeedle = 19,
        Line = 20,
    }

    /// <summary>
    /// Colours of the Learn HUD. All bright: on the additive glasses black is invisible and a dim colour is simply faint, so
    /// "secondary" means less bright, never dark. Feedback colours follow docs/MASTER_PLAN.md section 17.
    /// </summary>
    public static class LearnPalette
    {
        public static readonly Color Good = new Color(0.412f, 0.941f, 0.682f, 1f);     // #69F0AE green
        public static readonly Color Bad = new Color(1f, 0.439f, 0.263f, 1f);          // #FF7043 red-orange
        public static readonly Color Caution = new Color(1f, 0.835f, 0.310f, 1f);      // #FFD54F amber
        public static readonly Color Text = Color.white;
        public static readonly Color Info = new Color(0.702f, 0.898f, 0.988f, 1f);     // #B3E5FC, the ghost-hand rim colour
        public static readonly Color Accent = new Color(0.310f, 0.765f, 0.969f, 1f);   // #4FC3F7, the ghost-hand colour
        public static readonly Color Quiet = new Color(0.36f, 0.50f, 0.58f, 1f);       // legend, tracks, pips still to do

        // Finger colours (docs/MASTER_PLAN.md section 17): 1 yellow, 2 magenta, 3 lime, 4 orange. Used only with ghost.fingerColors.
        static readonly Color Finger1 = new Color(1f, 0.918f, 0.231f, 1f);             // #FFEB3B
        static readonly Color Finger2 = new Color(1f, 0.341f, 0.882f, 1f);             // #FF57E1
        static readonly Color Finger3 = new Color(0.698f, 1f, 0.349f, 1f);             // #B2FF59
        static readonly Color Finger4 = new Color(1f, 0.671f, 0.251f, 1f);             // #FFAB40

        /// <summary>The colour for a finger number: per finger when <paramref name="fingerColors"/> is on, otherwise the ghost-hand blue.</summary>
        public static Color Finger(int finger, bool fingerColors)
        {
            if (!fingerColors) return Accent;
            switch (finger)
            {
                case 1: return Finger1;
                case 2: return Finger2;
                case 3: return Finger3;
                case 4: return Finger4;
                default: return Accent;
            }
        }
    }

    /// <summary>How each <see cref="StringMark"/> looks and behaves. Pure.</summary>
    public static class StringMarkStyle
    {
        /// <summary>A passed string is a short confirmation: it fades by itself after this long.</summary>
        public const float CorrectHoldSeconds = 1.6f;

        /// <summary>Problems stay until they are replaced or cleared (the player needs them while fixing a finger).</summary>
        public const float StickyHold = float.PositiveInfinity;

        public static LearnGlyph Glyph(StringMark mark)
        {
            switch (mark)
            {
                case StringMark.Correct: return LearnGlyph.Check;
                case StringMark.Wrong: return LearnGlyph.Cross;
                case StringMark.Muted: return LearnGlyph.Bar;
                case StringMark.Unclear: return LearnGlyph.Question;
                case StringMark.LetRing: return LearnGlyph.Waves;
                default: return LearnGlyph.Disc;
            }
        }

        public static Color Color(StringMark mark)
        {
            switch (mark)
            {
                case StringMark.Correct: return LearnPalette.Good;
                case StringMark.Wrong: return LearnPalette.Bad;
                case StringMark.Muted: return LearnPalette.Bad;
                case StringMark.Unclear: return LearnPalette.Caution;
                case StringMark.LetRing: return LearnPalette.Caution;
                default: return LearnPalette.Quiet;
            }
        }

        /// <summary>How long a mark stays before it fades by itself (seconds); infinity = until replaced or cleared.</summary>
        public static float DefaultHoldSeconds(StringMark mark)
        {
            return mark == StringMark.Correct ? CorrectHoldSeconds : StickyHold;
        }

        /// <summary>A plain word for the mark, for the spectator panel (whose font has no check or cross).</summary>
        public static string Word(StringMark mark)
        {
            switch (mark)
            {
                case StringMark.Correct: return "correct";
                case StringMark.Wrong: return "wrong note";
                case StringMark.Muted: return "muted";
                case StringMark.Unclear: return "unclear";
                case StringMark.LetRing: return "let it ring";
                default: return string.Empty;
            }
        }
    }

    /// <summary>The shape and colour of each <see cref="TipIcon"/>. Pure.</summary>
    public static class TipIconStyle
    {
        public static bool HasGlyph(TipIcon icon)
        {
            return icon != TipIcon.None;
        }

        public static LearnGlyph Glyph(TipIcon icon)
        {
            switch (icon)
            {
                case TipIcon.Arch: return LearnGlyph.Arch;
                case TipIcon.Press: return LearnGlyph.Press;
                case TipIcon.Move: return LearnGlyph.Move;
                case TipIcon.Skip: return LearnGlyph.Skip;
                case TipIcon.Again: return LearnGlyph.Again;
                case TipIcon.Ring: return LearnGlyph.Waves;
                case TipIcon.Mismatch: return LearnGlyph.Mismatch;
                case TipIcon.Timing: return LearnGlyph.Timing;
                case TipIcon.Good: return LearnGlyph.Check;
                default: return LearnGlyph.Disc;
            }
        }

        public static Color Color(TipIcon icon)
        {
            return icon == TipIcon.Good ? LearnPalette.Good : LearnPalette.Caution;
        }
    }
}
