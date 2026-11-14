// CoreNeckLayout: Person 2's Core/Theory (FretboardGeometry + ChordLibrary) mapped onto the neck's own types
// (NeckLayout, FingertipDot, StringMark). Replaces Neck/Dev/DevNeckLayout for the app; the dev class stays only for previews
// and tests that predate Core.
//
// FRAME (the one place it is converted): Core's NeckPoint is (AlongMm from the nut toward the bridge, AcrossMm "+ toward
// string 6": Core/Theory/FretboardGeometry.cs StringAcrossMm = span * ((s - 1) / 5 - 0.5)). The neck frame (poses, neck mesh,
// Blender) is +x toward the bridge, +z toward string 1. So x = AlongMm and z = -AcrossMm.
// Chord data comes from ChordLibrary (Appendix B2), geometry from FretboardConfig (Default = 648 mm scale, 35 mm string span
// at the nut, 53.5 at the saddle, fingertip 75% from fret n-1 to n) until the demo guitar is measured.
// Allocation: Create and ChordMarks allocate nothing per frame; call them on a chord change, never in Update.
using UnityEngine;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Runtime.Neck
{
    public static class CoreNeckLayout
    {
        /// <summary>Frets 0..LastFret are drawn (every MUST chord sits within fret 3).</summary>
        public const int LastFret = 5;

        /// <summary>Fretboard margin outside the outer strings, added to Core's string span (Core has no fretboard width).</summary>
        public const float FretboardMarginMm = 8f;

        /// <summary>ASSUMPTION (as in DevNeckLayout): string height over the fretboard near the nut.</summary>
        public const float StringHeightMm = 2f;

        static FretboardGeometry s_Default;

        /// <summary>Core's geometry for <see cref="FretboardConfig.Default"/> (shared; Core's class is immutable).</summary>
        public static FretboardGeometry DefaultGeometry
        {
            get
            {
                if (s_Default == null) s_Default = new FretboardGeometry(FretboardConfig.Default);
                return s_Default;
            }
        }

        /// <summary>The neck layout from Core's geometry: frets 0..LastFret, strings from Core's spans (z = -across).</summary>
        public static NeckLayout Create(FretboardGeometry geometry)
        {
            if (geometry == null) geometry = DefaultGeometry;
            FretboardConfig config = geometry.Config;
            float scale = (float)config.ScaleLengthMm;
            var layout = new NeckLayout
            {
                scaleLengthMm = scale,
                fretXMm = new float[LastFret + 1],
                stringZAtNutMm = new float[NeckLayout.StringCount],
                stringZAtSaddleMm = new float[NeckLayout.StringCount],
                fretboardWidthAtNutMm = (float)config.NutStringSpanMm + FretboardMarginMm,
                stringHeightMm = StringHeightMm,
                nutToSoundholeMm = 0f,   // unknown until the guitar is measured (FloatingNeck then uses its floating strum zone)
            };
            for (int n = 0; n <= LastFret; n++) layout.fretXMm[n] = (float)geometry.FretDistanceMm(n);
            for (int i = 0; i < NeckLayout.StringCount; i++)
            {
                int stringNumber = i + 1;
                layout.stringZAtNutMm[i] = ZMm(geometry.StringAcrossMm(stringNumber, 0.0));
                layout.stringZAtSaddleMm[i] = ZMm(geometry.StringAcrossMm(stringNumber, config.ScaleLengthMm));
            }
            layout.fretboardWidthAtLastFretMm = (float)geometry.StringSpanMm(layout.fretXMm[LastFret]) + FretboardMarginMm;
            return layout;
        }

        /// <summary>
        /// The named chord's fingertip dots and per-string marks (marks[s - 1] for string s: Open, Muted, or None when fretted).
        /// Dots are listed string 6 first, as DevNeckLayout did. Returns the number of dots, or -1 (dots and marks untouched)
        /// when ChordLibrary has no such chord.
        /// </summary>
        public static int ChordMarks(FretboardGeometry geometry, string chordName, FingertipDot[] dots, StringMark[] marks)
        {
            if (string.IsNullOrEmpty(chordName) || !ChordLibrary.TryGet(chordName, out ChordShape shape)) return -1;
            return ChordMarks(geometry, shape, dots, marks);
        }

        /// <summary>As <see cref="ChordMarks(FretboardGeometry, string, FingertipDot[], StringMark[])"/> for a shape.</summary>
        public static int ChordMarks(FretboardGeometry geometry, ChordShape shape, FingertipDot[] dots, StringMark[] marks)
        {
            if (shape == null) return -1;
            if (geometry == null) geometry = DefaultGeometry;
            int count = 0;
            for (int stringNumber = NeckLayout.StringCount; stringNumber >= 1; stringNumber--)
            {
                int fret = shape.FretOn(stringNumber);
                StringMark mark = fret == ChordShape.Muted ? StringMark.Muted : fret == 0 ? StringMark.Open : StringMark.None;
                if (marks != null && stringNumber - 1 < marks.Length) marks[stringNumber - 1] = mark;
                if (fret < 1 || dots == null || count >= dots.Length) continue;
                NeckPoint p = geometry.FingertipTarget(stringNumber, fret);
                dots[count++] = new FingertipDot(stringNumber, (float)p.AlongMm, ZMm(p.AcrossMm), shape.FingerOn(stringNumber));
            }
            return count;
        }

        /// <summary>Core's across-neck offset (+ toward string 6) as the neck frame's z (+ toward string 1).</summary>
        public static float ZMm(double acrossMm)
        {
            return (float)-acrossMm;
        }
    }
}
