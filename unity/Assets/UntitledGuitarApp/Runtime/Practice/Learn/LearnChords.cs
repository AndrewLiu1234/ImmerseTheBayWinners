// LearnChords: which chord learn mode teaches and its shape, from Person 2's Core (Core/Theory/ChordLibrary; the rows of
// docs/MASTER_PLAN.md Appendix B2) and its dots and X / O marks on our neck from CoreNeckLayout (Core's frame converted there,
// never by hand here). The chord comes from the flags: the first entry of FlagSet.chords when it names one of the five MUST
// chords (the judge profile sets ["Em"]), otherwise G.
using System;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Neck;
using FingertipDot = UntitledGuitarApp.Runtime.Neck.FingertipDot;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    public static class LearnChords
    {
        public const string DefaultChord = "G";

        static readonly string[] s_Names = { "G", "C", "D", "Em", "Am" };   // the MUST chords (docs/MASTER_PLAN.md)

        /// <summary>The chords learn mode offers, in the plan's order: G, C, D, Em, Am (a copy).</summary>
        public static string[] Names { get { return (string[])s_Names.Clone(); } }

        /// <summary>The chord called <paramref name="name"/> (case-insensitive, one of <see cref="Names"/>) from Core's ChordLibrary, or null.</summary>
        public static LearnChord Get(string name)
        {
            int i = IndexOf(name);
            ChordShape shape;
            if (i < 0 || !ChordLibrary.TryGet(s_Names[i], out shape)) return null;
            return LearnChord.FromShape(shape);
        }

        /// <summary>The chord the flags ask for (see the file summary). Never null while Core's library has G.</summary>
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
        /// The chord's fingertip dots and X / O marks on the default neck (CoreNeckLayout.DefaultGeometry, the neck Bootstrap
        /// builds). Returns the dot count, or -1 for an unknown chord (arrays untouched).
        /// </summary>
        public static int ToNeck(LearnChord chord, FingertipDot[] dots, StringMark[] marks)
        {
            if (chord == null) return -1;
            return CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, chord.Name, dots, marks);
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
