using System.Globalization;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// Words for the Learn HUD: the length limits a glance allows, line breaking, an estimate of how wide a line is, and
    /// cached strings so that showing a prompt allocates nothing. Pure, no engine state, unit-tested.
    ///
    /// Why the limits: bold text at the project's minimum size (1.2 degrees of cap height) is about 0.9 degrees wide per
    /// letter, and what a player can read while looking at the neck is roughly 15 degrees wide. That is about 16
    /// letters per line, so a tip of 7 words or fewer is set on two short lines, broken where the sentence breaks.
    /// </summary>
    public static class LearnText
    {
        /// <summary>Tips are 7 words or fewer (docs/MASTER_PLAN.md section 17).</summary>
        public const int MaxTipWords = 7;

        /// <summary>Text this short stays on one line.</summary>
        public const int SingleLineChars = 14;

        /// <summary>The longest line a well-formed tip produces (two lines of this are one glance wide).</summary>
        public const int MaxLineChars = 18;

        /// <summary>Arial cap height in em (1467 / 2048), the same constant HudText uses.</summary>
        public const float CapHeightEm = 0.716f;

        const int BreakBonus = 3;          // prefer breaking where the sentence breaks (comma, colon, dash)
        const int NumberPenalty = 6;       // never strand "finger" from its "3"

        // Approximate advance widths of Arial / Helvetica Bold for ASCII 32..126, in 1/1000 em. Used only to centre a block
        // of text and to keep lines one glance wide; the real size on screen is measured by HudText.
        static readonly short[] s_Advance =
        {
            278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,   // space ! " # $ % & ' ( ) * + , - . /
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,   // 0-9 : ; < = > ?
            975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,   // @ A-O
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,   // P-Z [ \ ] ^ _
            333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,   // ` a-o
            611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,        // p-z { | } ~
        };

        const int OtherAdvance = 667;      // anything outside ASCII (a dash, an accented letter)

        static readonly string[] s_Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        static readonly string[] s_FingerHeadline = { "Finger 1", "Finger 2", "Finger 3", "Finger 4" };
        static readonly string[] s_StringLabel = { "string 1", "string 2", "string 3", "string 4", "string 5", "string 6" };
        static readonly string[] s_PluckPrompt =
        {
            "Pluck string 1", "Pluck string 2", "Pluck string 3", "Pluck string 4", "Pluck string 5", "Pluck string 6",
        };

        const int MaxCachedFret = 24;
        const int MaxCachedCents = 99;
        static string[] s_StringFret;
        static string[] s_Cents;

        // ------------------------------------------------------------------ limits

        /// <summary>Number of words: runs of non-space characters that contain a letter or a digit (a lone dash is not a word).</summary>
        public static int CountWords(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int words = 0;
            bool inToken = false;
            bool tokenCounts = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    if (inToken && tokenCounts) words++;
                    inToken = false;
                    tokenCounts = false;
                }
                else
                {
                    inToken = true;
                    if (char.IsLetterOrDigit(c)) tokenCounts = true;
                }
            }
            if (inToken && tokenCounts) words++;
            return words;
        }

        /// <summary>True when <paramref name="tip"/> is 7 words or fewer.</summary>
        public static bool WithinWordLimit(string tip)
        {
            return CountWords(tip) <= MaxTipWords;
        }

        /// <summary>True when both wrapped lines are one glance wide (<see cref="MaxLineChars"/> letters or fewer).</summary>
        public static bool FitsAtAGlance(string text)
        {
            string a, b;
            WrapTwoLines(text, out a, out b);
            return a.Length <= MaxLineChars && b.Length <= MaxLineChars;
        }

        // ------------------------------------------------------------------ line breaking

        /// <summary>
        /// Splits a short message into at most two lines for a narrow field of view. Text of
        /// <see cref="SingleLineChars"/> letters or fewer stays on one line (<paramref name="line2"/> is empty). Longer text
        /// breaks at the space that makes the longer line shortest, preferring the sentence's own break (comma, colon,
        /// semicolon, dash) and never separating a word from the number after it ("finger 3"). The separator at the
        /// break is dropped: the line break replaces it. Nothing is ever cut off. Allocates (two substrings), so call it
        /// only when the words change.
        /// </summary>
        public static void WrapTwoLines(string text, out string line1, out string line2)
        {
            string t = text == null ? string.Empty : text.Trim();
            line1 = t;
            line2 = string.Empty;
            if (t.Length <= SingleLineChars) return;

            int best = -1;
            int bestScore = int.MaxValue;
            for (int i = 1; i < t.Length - 1; i++)
            {
                if (t[i] != ' ') continue;

                int leftEnd = TrimEndIndex(t, i);
                int rightStart = TrimStartIndex(t, i + 1);
                int leftLen = leftEnd;
                int rightLen = t.Length - rightStart;
                if (leftLen <= 0 || rightLen <= 0) continue;

                int score = leftLen > rightLen ? leftLen : rightLen;
                if (leftEnd < i || rightStart > i + 1) score -= BreakBonus;
                if (char.IsDigit(t[rightStart]) && rightStart == i + 1) score += NumberPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0) return;
            int end = TrimEndIndex(t, best);
            int start = TrimStartIndex(t, best + 1);
            line1 = t.Substring(0, end);
            line2 = t.Substring(start);
        }

        // Exclusive end of the left part when breaking at the space at index 'space': separators before the break are dropped.
        static int TrimEndIndex(string t, int space)
        {
            int end = space;
            while (end > 0)
            {
                char c = t[end - 1];
                if (c == ' ' || c == ',' || c == ':' || c == ';' || c == '—' || c == '–') end--;
                else if (c == '-' && end >= 2 && t[end - 2] == ' ') end--;   // a free-standing hyphen used as a dash
                else break;
            }
            return end;
        }

        // Start of the right part when breaking before index 'start': a dash that opens the second half is dropped.
        static int TrimStartIndex(string t, int start)
        {
            int s = start;
            while (s < t.Length)
            {
                char c = t[s];
                if (c == ' ' || c == '—' || c == '–') s++;
                else if (c == '-' && s + 1 < t.Length && t[s + 1] == ' ') s++;
                else break;
            }
            return s;
        }

        // ------------------------------------------------------------------ width estimate

        /// <summary>
        /// Roughly how wide <paramref name="text"/> is in bold Arial, in cap heights (multiply by the cap height in degrees
        /// or metres). "Clean!" is about 4.3. An estimate for centring and for keeping lines short; not a measurement.
        /// </summary>
        public static float WidthInCaps(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            int sum = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int c = text[i];
                sum += c >= 32 && c <= 126 ? s_Advance[c - 32] : OtherAdvance;
            }
            return sum / (1000f * CapHeightEm);
        }

        // ------------------------------------------------------------------ cached words (no allocation when shown)

        /// <summary>"0".."9" without allocating; other numbers are formatted (culture-invariant).</summary>
        public static string Digit(int n)
        {
            if (n >= 0 && n <= 9) return s_Digits[n];
            return n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"Finger 2" (fingers 1-4 are cached).</summary>
        public static string FingerHeadline(int finger)
        {
            if (finger >= 1 && finger <= 4) return s_FingerHeadline[finger - 1];
            return "Finger " + finger.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"string 6" (strings 1-6 are cached).</summary>
        public static string StringLabel(int stringNumber)
        {
            if (stringNumber >= 1 && stringNumber <= 6) return s_StringLabel[stringNumber - 1];
            return "string " + stringNumber.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"Pluck string 6" (strings 1-6 are cached).</summary>
        public static string PluckPrompt(int stringNumber)
        {
            if (stringNumber >= 1 && stringNumber <= 6) return s_PluckPrompt[stringNumber - 1];
            return "Pluck string " + stringNumber.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"string 5, fret 2", or "string 5, open" for fret 0. Strings 1-6 and frets 0-24 are cached after first use.</summary>
        public static string StringFret(int stringNumber, int fret)
        {
            if (stringNumber < 1 || stringNumber > 6 || fret < 0 || fret > MaxCachedFret) return BuildStringFret(stringNumber, fret);
            if (s_StringFret == null) s_StringFret = new string[6 * (MaxCachedFret + 1)];
            int index = (stringNumber - 1) * (MaxCachedFret + 1) + fret;
            string cached = s_StringFret[index];
            if (cached == null)
            {
                cached = BuildStringFret(stringNumber, fret);
                s_StringFret[index] = cached;
            }
            return cached;
        }

        static string BuildStringFret(int stringNumber, int fret)
        {
            string s = "string " + stringNumber.ToString(CultureInfo.InvariantCulture);
            if (fret <= 0) return s + ", open";
            return s + ", fret " + fret.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The tuner's debug readout: "-12 cents", "+7 cents", "0 cents". Rounded, clamped to +-99, cached.</summary>
        public static string CentsText(float cents)
        {
            int rounded;
            if (float.IsNaN(cents)) rounded = 0;
            else if (cents >= MaxCachedCents) rounded = MaxCachedCents;
            else if (cents <= -MaxCachedCents) rounded = -MaxCachedCents;
            else rounded = (int)System.Math.Round(cents);

            if (s_Cents == null) s_Cents = new string[2 * MaxCachedCents + 1];
            string cached = s_Cents[rounded + MaxCachedCents];
            if (cached == null)
            {
                string sign = rounded > 0 ? "+" : string.Empty;
                cached = sign + rounded.ToString(CultureInfo.InvariantCulture) + " cents";
                s_Cents[rounded + MaxCachedCents] = cached;
            }
            return cached;
        }
    }
}
