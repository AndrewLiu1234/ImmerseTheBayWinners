using System;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>
    /// Verdict → beginner tip (MASTER_PLAN Appendix B6, with the music-teacher corrections for SL2). Every tip has at most
    /// <see cref="MaxWords"/> words; string 1 = high E. Wording:
    /// <list type="bullet">
    /// <item>OpenStringInstead: "Finger {f}: press string {s}, fret {n}".</item>
    /// <item>WrongFretLow / WrongFretHigh: "Finger {f}: one fret higher" / "… lower" ("two frets" when 2 off); an open target
    /// sounding sharp: "Play string {s} open, no finger".</item>
    /// <item>WrongString: "Pluck string {s}, not string {n}" — only the picking hand can make a neighbour's own chord note
    /// (replaces B6's "Finger {f} goes on string {s}"; approved by Person 2, D-P2-WRONGSTRING).</item>
    /// <item>Muted, FRETTED string: "Finger {f}: press with fingertip". Muted OPEN string: "String {s} muted — arch finger {g}",
    /// g = the finger on the bass-side neighbour, else the treble-side one; "… arch your fingers" when neither is fretted.</item>
    /// <item>ExtraString "Skip string {s}" · Unclear "Pluck string {s} again, slowly" · ShortRing "Let string {s} ring".</item>
    /// </list>
    /// Allocates the returned string; call per verdict, not per audio hop.
    /// </summary>
    public static class Tips
    {
        public const int MaxWords = 7;
        public const string LateChange = "Start moving one beat earlier";

        /// <summary>Tip for a pluck verdict, or null for Correct. <paramref name="chord"/> gives the finger numbers (optional).</summary>
        public static string ForPluck(in PluckResult r, ChordShape chord) =>
            ForPluck(r.Verdict, r.String, r.ExpectedMidi, chord, r.HeardString, r.HeardMidi);

        public static string ForPluck(PluckVerdict verdict, int stringNumber, int expectedMidi, ChordShape chord,
                                      int heardString = 0, int heardMidi = -1)
        {
            Tuning.CheckString(stringNumber);
            int s = stringNumber;
            int open = Tuning.StandardMidi(s);
            int fret = expectedMidi >= open ? expectedMidi - open : 0;
            int f = chord != null && fret > 0 && chord.FretOn(s) == fret ? chord.FingerOn(s) : 0;
            switch (verdict)
            {
                case PluckVerdict.Correct:
                    return null;
                case PluckVerdict.OpenStringInstead:
                    return f > 0 ? $"Finger {f}: press string {s}, fret {fret}" : $"Press string {s} at fret {fret}";
                case PluckVerdict.WrongFretLow:
                case PluckVerdict.WrongFretHigh:
                {
                    bool low = verdict == PluckVerdict.WrongFretLow;
                    if (!low && fret == 0) return $"Play string {s} open, no finger";
                    int off = heardMidi >= 0 ? Math.Abs(expectedMidi - heardMidi) : 1;
                    string frets = off == 2 ? "two frets" : "one fret";
                    string dir = low ? "higher" : "lower";
                    return f > 0 ? $"Finger {f}: {frets} {dir}" : $"String {s}: {frets} {dir}";
                }
                case PluckVerdict.WrongString:
                    return heardString >= 1 && heardString <= Tuning.StringCount && heardString != s
                        ? $"Pluck string {s}, not string {heardString}"
                        : $"Pluck string {s} only";
                case PluckVerdict.Muted:
                {
                    if (fret > 0) return f > 0 ? $"Finger {f}: press with fingertip" : $"Press string {s} with fingertip";
                    int g = ArchFinger(s, chord);
                    return g > 0 ? $"String {s} muted — arch finger {g}" : $"String {s} muted — arch your fingers";
                }
                case PluckVerdict.ExtraString:
                    return $"Skip string {s}";
                case PluckVerdict.ShortRing:
                    return $"Let string {s} ring";
                default:
                    return $"Pluck string {s} again, slowly";
            }
        }

        /// <summary>"Heard {X} — target is {Y}" (strum check, SL3).</summary>
        public static string WrongChord(string heard, string target) => $"Heard {heard} — target is {target}";

        public const string StrumTopFour = "Strum only the top four";
        public const string StrumAgain = "Strum again, let it ring";

        /// <summary>
        /// Tip for a strum-check result (SL3), or null when clean. Wrong chord: "Heard {X} — target is {Y}". Extra bass strings:
        /// "Strum only the top four" when the chord's bass is string 4 (D, Dm; Appendix B2), else "Skip string {s}". One-string
        /// faults reuse the pluck wording (finger numbers from <paramref name="expected"/>). Nothing heard / ambiguous:
        /// "Strum again, let it ring".
        /// </summary>
        public static string ForStrum(in StrumCheckResult r, ChordShape expected)
        {
            if (r.Clean) return null;
            switch (r.Issue)
            {
                case StrumIssue.None: return null;
                case StrumIssue.WrongChord: return WrongChord(r.BestMatch ?? "?", r.ExpectedChord);
                case StrumIssue.ExtraString:
                    if (expected != null && expected.BassString == 4) return StrumTopFour;
                    return r.IssueString >= 1 && r.IssueString <= Tuning.StringCount ? $"Skip string {r.IssueString}" : StrumTopFour;
                case StrumIssue.OpenStringInstead:
                case StrumIssue.WrongFretLow:
                case StrumIssue.WrongFretHigh:
                case StrumIssue.Muted:
                    if (r.IssueString < 1 || r.IssueString > Tuning.StringCount) return StrumAgain;
                    return ForPluck(r.StringVerdict, r.IssueString, r.IssueExpectedMidi, expected, r.IssueString, r.IssueHeardMidi);
                default: return StrumAgain;
            }
        }

        /// <summary>The finger most likely leaning on open string <paramref name="s"/>: the one on the bass-side neighbour
        /// (s + 1), else the treble-side neighbour (s − 1); 0 if neither neighbour is fretted.</summary>
        public static int ArchFinger(int s, ChordShape chord)
        {
            if (chord == null) return 0;
            if (s + 1 <= Tuning.StringCount && chord.FingerOn(s + 1) > 0) return chord.FingerOn(s + 1);
            if (s - 1 >= 1 && chord.FingerOn(s - 1) > 0) return chord.FingerOn(s - 1);
            return 0;
        }

        /// <summary>Words in a tip (a lone dash is punctuation, not a word).</summary>
        public static int WordCount(string tip)
        {
            if (string.IsNullOrEmpty(tip)) return 0;
            int n = 0;
            foreach (var w in tip.Split(' '))
            {
                if (w.Length == 0 || w == "—" || w == "-") continue;
                n++;
            }
            return n;
        }
    }
}
