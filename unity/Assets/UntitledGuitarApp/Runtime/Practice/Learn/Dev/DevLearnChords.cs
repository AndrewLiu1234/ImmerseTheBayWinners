// TEMPORARY until Core's ChordLibrary + FretboardGeometry land (Person 2). Delete with DevNeckLayout.
// The five MUST chords as rows of docs/MASTER_PLAN.md Appendix B2 (frets and fingers listed string 6 -> 1; -1 = x):
//   Em 0 2 2 0 0 0 / - 2 3 - - -    G 3 2 0 0 0 3 / 2 1 - - - 3    C x 3 2 0 1 0 / x 3 2 - 1 -
//   D  x x 0 2 3 2 / x x - 1 3 2    Am x 0 2 2 1 0 / x - 2 3 1 -
// and the learn chord the flags ask for (FlagSet.chords[0], the judge profile sets ["Em"]; else G).
using System;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;

namespace UntitledGuitarApp.Runtime.Practice.Learn.Dev
{
    public static class DevLearnChords
    {
        public const string DefaultChord = "G";

        static readonly string[] s_Names = { "G", "C", "D", "Em", "Am" };
        static readonly int[][] s_Frets =
        {
            new[] { 3, 2, 0, 0, 0, 3 },
            new[] { -1, 3, 2, 0, 1, 0 },
            new[] { -1, -1, 0, 2, 3, 2 },
            new[] { 0, 2, 2, 0, 0, 0 },
            new[] { -1, 0, 2, 2, 1, 0 },
        };
        static readonly int[][] s_Fingers =
        {
            new[] { 2, 1, 0, 0, 0, 3 },
            new[] { 0, 3, 2, 0, 1, 0 },
            new[] { 0, 0, 0, 1, 3, 2 },
            new[] { 0, 2, 3, 0, 0, 0 },
            new[] { 0, 0, 2, 3, 1, 0 },
        };

        /// <summary>The names this table knows, in the order of the plan: G, C, D, Em, Am.</summary>
        public static string[] Names { get { return (string[])s_Names.Clone(); } }

        /// <summary>The chord called <paramref name="name"/> (case-insensitive), or null.</summary>
        public static LearnChord Get(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : LearnChord.FromRow(s_Names[i], s_Frets[i], s_Fingers[i]);
        }

        /// <summary>
        /// The chord to learn: the first entry of the flags' chords list when it names a chord in this table (the judge
        /// profile sets ["Em"]), otherwise G. Never null.
        /// </summary>
        public static LearnChord FromFlags(FlagSet flags)
        {
            return Get(NameFromFlags(flags));
        }

        /// <summary>The chord name <see cref="FromFlags"/> picks. Pure.</summary>
        public static string NameFromFlags(FlagSet flags)
        {
            string[] chords = flags != null ? flags.chords : null;
            if (chords != null && chords.Length > 0 && chords[0] != null)
            {
                int i = IndexOf(chords[0].Trim());
                if (i >= 0) return s_Names[i];
            }
            return DefaultChord;
        }

        /// <summary>
        /// The chord's fingertip dots on <paramref name="layout"/> (the 75%-of-the-way rule of DevNeckLayout) and its X / O marks.
        /// Returns the number of dots.
        /// </summary>
        public static int ToNeck(LearnChord chord, NeckLayout layout, FingertipDot[] dots, StringMark[] marks)
        {
            if (chord == null || layout == null) return 0;
            var frets = new int[LearnChord.StringCount];
            var fingers = new int[LearnChord.StringCount];
            for (int column = 0; column < LearnChord.StringCount; column++)
            {
                int s = LearnChord.StringCount - column;
                frets[column] = chord.Fret(s);
                fingers[column] = chord.Finger(s);
            }
            return DevNeckLayout.FromChordRow(layout, frets, fingers, dots, marks);
        }

        static int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < s_Names.Length; i++)
                if (string.Equals(s_Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
