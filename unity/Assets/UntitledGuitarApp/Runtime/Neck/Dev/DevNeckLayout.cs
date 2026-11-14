// TEMPORARY until Core's FretboardGeometry and ChordLibrary land (Person 2, SL0-core, Sat ~09:00). Bootstrap switches to
// Core then; delete this file. Never use it to judge audio or to place anything in the final app.
//
// It exists so the dev scene and the preview can show a neck before Core does: default measurements (not the demo guitar's),
// the Appendix B4 formulas, and the G chord's row of the Appendix B2 table (docs/MASTER_PLAN.md).
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck.Dev
{
    public static class DevNeckLayout
    {
        // Default measurements, from the task brief and Appendix B4. Not measured on the demo guitar.
        public const float ScaleLengthMm = 648f;             // 25.5 in
        public const float NutWidthMm = 43f;
        public const float StringSpreadAtNutMm = 35f;        // string 1 to string 6
        public const float StringSpreadAtSaddleMm = 53f;
        public const int LastFret = 5;                       // frets 0-5
        public const float StringHeightMm = 2f;              // ASSUMPTION: typical string height over the fretboard near the nut
        public const float FingertipFraction = 0.75f;        // B4: "about 75% of the way from fret n-1 to fret n"

        /// <summary>Appendix B4: distance from the nut to fret n, d(n) = L * (1 - 2^(-n/12)).</summary>
        public static float FretXMm(int fret)
        {
            return (float)(ScaleLengthMm * (1.0 - Math.Pow(2.0, -fret / 12.0)));
        }

        /// <summary>Appendix B4: the fingertip target for fret n, 75% of the way from fret n-1 to fret n (27.3, 62.1, 95.0 mm for frets 1-3).</summary>
        public static float FingertipXMm(int fret)
        {
            if (fret < 1) return 0f;
            float behind = FretXMm(fret - 1);
            return behind + FingertipFraction * (FretXMm(fret) - behind);
        }

        /// <summary>The default neck: frets 0-5 on a 648 mm scale, strings spreading from 35 mm at the nut to 53 mm at the saddle.</summary>
        public static NeckLayout Create()
        {
            var layout = new NeckLayout
            {
                scaleLengthMm = ScaleLengthMm,
                fretXMm = new float[LastFret + 1],
                stringZAtNutMm = new float[NeckLayout.StringCount],
                stringZAtSaddleMm = new float[NeckLayout.StringCount],
                fretboardWidthAtNutMm = NutWidthMm,
                stringHeightMm = StringHeightMm,
                nutToSoundholeMm = 0f,                       // unknown until the guitar is measured
            };
            for (int n = 0; n <= LastFret; n++) layout.fretXMm[n] = FretXMm(n);
            for (int i = 0; i < NeckLayout.StringCount; i++)
            {
                // String 1 (index 0) is on the +z side, string 6 on the -z side, evenly spaced about the centre line.
                float t = 0.5f - i / (float)(NeckLayout.StringCount - 1);
                layout.stringZAtNutMm[i] = StringSpreadAtNutMm * t;
                layout.stringZAtSaddleMm[i] = StringSpreadAtSaddleMm * t;
            }
            // The fretboard keeps the same margin outside the outer strings as it has at the nut.
            float margin = NutWidthMm - StringSpreadAtNutMm;
            float lastX = layout.fretXMm[LastFret];
            float spreadAtLastFret = StringSpreadAtNutMm + (StringSpreadAtSaddleMm - StringSpreadAtNutMm) * (lastX / ScaleLengthMm);
            layout.fretboardWidthAtLastFretMm = spreadAtLastFret + margin;
            return layout;
        }

        /// <summary>
        /// One row of the Appendix B2 chord table as dots and marks. <paramref name="fretsString6To1"/> and
        /// <paramref name="fingersString6To1"/> are listed string 6 first, as in the table: fret -1 = x (do not play),
        /// 0 = open; finger 0 = none. Fills <paramref name="dots"/> and <paramref name="marks"/> (indexed by string number - 1)
        /// and returns the number of dots.
        /// </summary>
        public static int FromChordRow(NeckLayout layout, int[] fretsString6To1, int[] fingersString6To1, FingertipDot[] dots, StringMark[] marks)
        {
            int count = 0;
            for (int column = 0; column < NeckLayout.StringCount; column++)
            {
                int stringNumber = NeckLayout.StringCount - column;   // the table runs string 6 -> 1
                int fret = fretsString6To1[column];
                if (fret < 0)
                {
                    marks[stringNumber - 1] = StringMark.Muted;
                }
                else if (fret == 0)
                {
                    marks[stringNumber - 1] = StringMark.Open;
                }
                else
                {
                    marks[stringNumber - 1] = StringMark.None;
                    if (count < dots.Length)
                    {
                        float x = FingertipXMm(fret);
                        dots[count++] = new FingertipDot(stringNumber, x, NeckMath.StringZMm(layout, stringNumber, x), fingersString6To1[column]);
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// The G chord, Appendix B2: frets 3 2 0 0 0 3 and fingers 2 1 - - - 3 (string 6 to 1). Three dots (string 6 fret 3
        /// finger 2, string 5 fret 2 finger 1, string 1 fret 3 finger 3) and O marks on strings 4, 3 and 2.
        /// </summary>
        public static int GChord(NeckLayout layout, FingertipDot[] dots, StringMark[] marks)
        {
            return FromChordRow(layout, GFrets, GFingers, dots, marks);
        }

        static readonly int[] GFrets = { 3, 2, 0, 0, 0, 3 };
        static readonly int[] GFingers = { 2, 1, 0, 0, 0, 3 };
    }
}
