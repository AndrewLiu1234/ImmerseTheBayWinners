// NeckLayout: where everything on the neck is, as plain data. The visual (FloatingNeck) only draws what it is given here;
// it never computes music theory. Core's FretboardGeometry (Person 2) fills one of these; until it lands the dev scene uses
// Dev/DevNeckLayout.
//
// THE NECK FRAME (pinned; the ghost-hand pose files and the Blender mock-up use exactly this frame), in Unity axes:
//   origin  on the neck's centre line, at the nut face, on the fretboard's playing surface
//   +x      along the neck from the nut toward the bridge
//   +y      up out of the fretboard (toward the fretting hand)
//   +z      across the neck toward string 1 (high E)
// Millimetres in data, metres in transforms. Strings are numbered 1 (high E) to 6 (low E).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>The neck's geometry in neck-local millimetres. Plain data: fill the fields, then call <see cref="Validate"/>.</summary>
    [Serializable]
    public sealed class NeckLayout
    {
        public const int StringCount = 6;

        /// <summary>Nut to saddle, in mm. Every string's saddle end is at x = scaleLengthMm.</summary>
        public float scaleLengthMm;

        /// <summary>x of the nut (index 0, always 0) and of frets 1..N (index n), in mm from the nut, strictly increasing.</summary>
        public float[] fretXMm;

        /// <summary>z of each string at the nut (x = 0), in mm. Index 0 is string 1 (high E, the +z side), index 5 is string 6 (low E).</summary>
        public float[] stringZAtNutMm;

        /// <summary>z of each string at the saddle (x = scaleLengthMm), same indexing. Strings run straight between the two.</summary>
        public float[] stringZAtSaddleMm;

        /// <summary>Fretboard width at the nut, in mm.</summary>
        public float fretboardWidthAtNutMm;

        /// <summary>Fretboard width at the last fret in <see cref="fretXMm"/>, in mm.</summary>
        public float fretboardWidthAtLastFretMm;

        /// <summary>Height of the strings above the fretboard's playing surface (the y of every string), in mm.</summary>
        public float stringHeightMm;

        /// <summary>Nut to the centre of the soundhole, in mm. 0 = unknown (not measured yet).</summary>
        public float nutToSoundholeMm;

        /// <summary>The highest fret number in <see cref="fretXMm"/> (0 when there are no frets).</summary>
        public int LastFret
        {
            get { return fretXMm == null ? 0 : Math.Max(0, fretXMm.Length - 1); }
        }

        /// <summary>x of the last fret in mm (0 when there are no frets).</summary>
        public float LastFretXMm
        {
            get { return fretXMm == null || fretXMm.Length == 0 ? 0f : fretXMm[fretXMm.Length - 1]; }
        }

        /// <summary>A deep copy, so the owner of the original can keep changing it.</summary>
        public NeckLayout Clone()
        {
            return new NeckLayout
            {
                scaleLengthMm = scaleLengthMm,
                fretXMm = Copy(fretXMm),
                stringZAtNutMm = Copy(stringZAtNutMm),
                stringZAtSaddleMm = Copy(stringZAtSaddleMm),
                fretboardWidthAtNutMm = fretboardWidthAtNutMm,
                fretboardWidthAtLastFretMm = fretboardWidthAtLastFretMm,
                stringHeightMm = stringHeightMm,
                nutToSoundholeMm = nutToSoundholeMm,
            };
        }

        /// <summary>
        /// Checks the layout and returns one readable sentence per problem (an empty list means it is usable). It catches the
        /// mistakes that are easy to make at the seam: metres instead of millimetres, strings listed 6 to 1, z pointing at the
        /// low E string, frets out of order.
        /// </summary>
        public List<string> Validate()
        {
            var errors = new List<string>();

            bool scaleOk = IsFinite(scaleLengthMm) && scaleLengthMm >= 300f && scaleLengthMm <= 900f;
            if (!IsFinite(scaleLengthMm))
                errors.Add("scaleLengthMm is not a number.");
            else if (scaleLengthMm > 0f && scaleLengthMm < 10f)
                errors.Add("scaleLengthMm is " + F(scaleLengthMm) + ": that looks like metres. This layout is in millimetres (a guitar is about 648).");
            else if (!scaleOk)
                errors.Add("scaleLengthMm is " + F(scaleLengthMm) + "; expected 300 to 900 mm (a guitar is about 648).");

            bool fretsOk = fretXMm != null && fretXMm.Length >= 2;
            if (!fretsOk)
            {
                errors.Add("fretXMm must hold the nut (0) and at least one fret; it has " + (fretXMm == null ? 0 : fretXMm.Length) + " value(s).");
            }
            else
            {
                if (!IsFinite(fretXMm[0]) || Math.Abs(fretXMm[0]) > 0.001f)
                {
                    errors.Add("fretXMm[0] is the nut and must be 0; it is " + F(fretXMm[0]) + ".");
                    fretsOk = false;
                }
                for (int n = 1; n < fretXMm.Length; n++)
                {
                    if (!IsFinite(fretXMm[n]))
                    {
                        errors.Add("fretXMm[" + n + "] is not a number.");
                        fretsOk = false;
                    }
                    else if (!(fretXMm[n] > fretXMm[n - 1]))
                    {
                        errors.Add("fretXMm must increase toward the bridge: fret " + n + " at " + F(fretXMm[n]) + " mm is not beyond fret " +
                                   (n - 1) + " at " + F(fretXMm[n - 1]) + " mm.");
                        fretsOk = false;
                    }
                }
                if (fretsOk && scaleOk && fretXMm[fretXMm.Length - 1] >= scaleLengthMm)
                    errors.Add("fret " + (fretXMm.Length - 1) + " at " + F(fretXMm[fretXMm.Length - 1]) + " mm is at or beyond the saddle (scale length " +
                               F(scaleLengthMm) + " mm).");
            }

            bool nutStringsOk = CheckStrings("stringZAtNutMm", stringZAtNutMm, errors);
            bool saddleStringsOk = CheckStrings("stringZAtSaddleMm", stringZAtSaddleMm, errors);

            bool nutWidthOk = CheckPositive("fretboardWidthAtNutMm", fretboardWidthAtNutMm, 200f, errors);
            bool lastWidthOk = CheckPositive("fretboardWidthAtLastFretMm", fretboardWidthAtLastFretMm, 200f, errors);

            if (nutStringsOk && nutWidthOk)
                CheckInsideBoard("at the nut", stringZAtNutMm[0], stringZAtNutMm[StringCount - 1], fretboardWidthAtNutMm, errors);
            if (nutStringsOk && saddleStringsOk && lastWidthOk && fretsOk && scaleOk)
            {
                float t = fretXMm[fretXMm.Length - 1] / scaleLengthMm;
                float z1 = stringZAtNutMm[0] + (stringZAtSaddleMm[0] - stringZAtNutMm[0]) * t;
                float z6 = stringZAtNutMm[StringCount - 1] + (stringZAtSaddleMm[StringCount - 1] - stringZAtNutMm[StringCount - 1]) * t;
                CheckInsideBoard("at fret " + (fretXMm.Length - 1), z1, z6, fretboardWidthAtLastFretMm, errors);
            }

            if (!IsFinite(stringHeightMm) || stringHeightMm < 0f || stringHeightMm > 15f)
                errors.Add("stringHeightMm is " + F(stringHeightMm) + "; expected 0 to 15 mm above the fretboard (about 2).");

            if (!IsFinite(nutToSoundholeMm) || nutToSoundholeMm < 0f)
                errors.Add("nutToSoundholeMm is " + F(nutToSoundholeMm) + "; use 0 for unknown, or the measured distance in mm.");
            else if (nutToSoundholeMm > 0f && fretsOk && scaleOk &&
                     (nutToSoundholeMm <= fretXMm[fretXMm.Length - 1] || nutToSoundholeMm >= scaleLengthMm))
                errors.Add("nutToSoundholeMm is " + F(nutToSoundholeMm) + "; the soundhole lies between the last fret (" +
                           F(fretXMm[fretXMm.Length - 1]) + " mm) and the saddle (" + F(scaleLengthMm) + " mm). Use 0 for unknown.");

            return errors;
        }

        static bool CheckStrings(string name, float[] z, List<string> errors)
        {
            if (z == null || z.Length != StringCount)
            {
                errors.Add(name + " must have " + StringCount + " values (string 1 first); it has " + (z == null ? 0 : z.Length) + ".");
                return false;
            }
            bool ok = true;
            for (int i = 0; i < StringCount; i++)
            {
                if (!IsFinite(z[i]))
                {
                    errors.Add(name + "[" + i + "] (string " + (i + 1) + ") is not a number.");
                    ok = false;
                }
                else if (i > 0 && IsFinite(z[i - 1]) && !(z[i] < z[i - 1]))
                {
                    errors.Add(name + " must go from string 1 (high E, the largest z) down to string 6 (low E): string " + (i + 1) + " at z = " +
                               F(z[i]) + " is not below string " + i + " at z = " + F(z[i - 1]) + ". +z points toward string 1.");
                    ok = false;
                }
            }
            return ok;
        }

        static bool CheckPositive(string name, float value, float max, List<string> errors)
        {
            if (IsFinite(value) && value > 0f && value <= max) return true;
            errors.Add(name + " is " + F(value) + "; expected a width in mm between 0 and " + F(max) + ".");
            return false;
        }

        static void CheckInsideBoard(string where, float zString1, float zString6, float boardWidthMm, List<string> errors)
        {
            float half = boardWidthMm * 0.5f;
            if (zString1 > half + 0.001f)
                errors.Add("string 1 " + where + " (z = " + F(zString1) + ") lies outside the fretboard (half width " + F(half) + " mm).");
            if (zString6 < -half - 0.001f)
                errors.Add("string 6 " + where + " (z = " + F(zString6) + ") lies outside the fretboard (half width " + F(half) + " mm).");
        }

        static float[] Copy(float[] source)
        {
            return source == null ? null : (float[])source.Clone();
        }

        static bool IsFinite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static string F(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
