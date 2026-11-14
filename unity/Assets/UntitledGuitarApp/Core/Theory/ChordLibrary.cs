using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Theory
{
    /// <summary>
    /// Chord shapes from MASTER_PLAN Appendix B2 (string 6 → 1; −1 = muted, finger 0 = none).
    /// MUST: G C D Em Am. SHOULD: E. COULD: A, Dm. Names are case-sensitive ("Em", not "EM").
    /// Pending: music-teacher review of the table before SL2 (Appendix B2 note).
    /// </summary>
    public static class ChordLibrary
    {
        const int X = ChordShape.Muted;

        public static readonly ChordShape G  = new ChordShape("G",  new[] { 3, 2, 0, 0, 0, 3 }, new[] { 2, 1, 0, 0, 0, 3 });
        public static readonly ChordShape C  = new ChordShape("C",  new[] { X, 3, 2, 0, 1, 0 }, new[] { 0, 3, 2, 0, 1, 0 });
        public static readonly ChordShape D  = new ChordShape("D",  new[] { X, X, 0, 2, 3, 2 }, new[] { 0, 0, 0, 1, 3, 2 });
        public static readonly ChordShape Em = new ChordShape("Em", new[] { 0, 2, 2, 0, 0, 0 }, new[] { 0, 2, 3, 0, 0, 0 });
        public static readonly ChordShape Am = new ChordShape("Am", new[] { X, 0, 2, 2, 1, 0 }, new[] { 0, 0, 2, 3, 1, 0 });
        public static readonly ChordShape E  = new ChordShape("E",  new[] { 0, 2, 2, 1, 0, 0 }, new[] { 0, 2, 3, 1, 0, 0 });
        public static readonly ChordShape A  = new ChordShape("A",  new[] { X, 0, 2, 2, 2, 0 }, new[] { 0, 0, 1, 2, 3, 0 });
        public static readonly ChordShape Dm = new ChordShape("Dm", new[] { X, X, 0, 2, 3, 1 }, new[] { 0, 0, 0, 2, 3, 1 });

        /// <summary>The five beginner chords every lesson and song may use, in teaching order.</summary>
        public static readonly IReadOnlyList<ChordShape> Must = new[] { G, C, D, Em, Am };

        public static readonly IReadOnlyList<ChordShape> All = new[] { G, C, D, Em, Am, E, A, Dm };

        static readonly Dictionary<string, ChordShape> ByName = BuildIndex();

        static Dictionary<string, ChordShape> BuildIndex()
        {
            var d = new Dictionary<string, ChordShape>(System.StringComparer.Ordinal);
            foreach (var c in All) d.Add(c.Name, c);
            return d;
        }

        public static bool TryGet(string name, out ChordShape shape)
        {
            if (name == null) { shape = null; return false; }
            return ByName.TryGetValue(name, out shape);
        }

        /// <exception cref="KeyNotFoundException">the chord is not in the library</exception>
        public static ChordShape Get(string name)
        {
            if (TryGet(name, out var shape)) return shape;
            throw new KeyNotFoundException($"chord '{name}' is not in ChordLibrary");
        }
    }
}
