// NeckMath: the pure maths behind the neck visual (no MonoBehaviour, no scene access). Unit-tested in NeckTests.cs.
// It reads positions out of a NeckLayout; it never derives them from music theory.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck
{
    public static class NeckMath
    {
        /// <summary>Data is in millimetres, transforms are in metres.</summary>
        public const float MetresPerMm = 0.001f;

        /// <summary>
        /// Where the strum zone is assumed to be when the nut-to-soundhole distance has not been measured (layout value 0):
        /// this fraction of the scale length. ASSUMPTION, about right for a dreadnought (20th fret at 0.685 of the scale, plus
        /// half a 100 mm soundhole). The measured value replaces it as soon as the layout carries one.
        /// </summary>
        public const float DefaultSoundholeFractionOfScale = 0.75f;

        public static bool IsFinite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        /// <summary>z in mm of a string at x mm from the nut. Strings run straight from their nut z to their saddle z.</summary>
        public static float StringZMm(NeckLayout layout, int stringNumber, float xMm)
        {
            int i = Mathf.Clamp(stringNumber, 1, NeckLayout.StringCount) - 1;
            return StringZMm(layout.stringZAtNutMm[i], layout.stringZAtSaddleMm[i], layout.scaleLengthMm, xMm);
        }

        /// <summary>The same straight-line rule on raw numbers: zNut at x = 0, zSaddle at x = scaleLengthMm.</summary>
        public static float StringZMm(float zNutMm, float zSaddleMm, float scaleLengthMm, float xMm)
        {
            if (!(scaleLengthMm > 0f)) return zNutMm;
            return zNutMm + (zSaddleMm - zNutMm) * (xMm / scaleLengthMm);
        }

        /// <summary>Distance between neighbouring strings at the nut, in mm (always positive for a valid layout).</summary>
        public static float StringPitchAtNutMm(NeckLayout layout)
        {
            return (layout.stringZAtNutMm[0] - layout.stringZAtNutMm[NeckLayout.StringCount - 1]) / (NeckLayout.StringCount - 1);
        }

        /// <summary>
        /// Half the fretboard width at x mm: a straight taper from the nut width to the width at the last fret, continued
        /// beyond the last fret at the same rate.
        /// </summary>
        public static float FretboardHalfWidthMm(NeckLayout layout, float xMm)
        {
            float last = layout.LastFretXMm;
            float t = last > 0f ? xMm / last : 0f;
            return 0.5f * (layout.fretboardWidthAtNutMm + (layout.fretboardWidthAtLastFretMm - layout.fretboardWidthAtNutMm) * t);
        }

        /// <summary>Neck-local millimetres to the neck transform's local metres. The axes are the same; only the unit changes.</summary>
        public static Vector3 LocalMetres(float xMm, float yMm, float zMm)
        {
            return new Vector3(xMm * MetresPerMm, yMm * MetresPerMm, zMm * MetresPerMm);
        }

        /// <summary>
        /// Where a fingertip dot is drawn, in the neck transform's local metres: its own x and z (the data wins; nothing is
        /// recomputed from the string number) at the height of the strings.
        /// </summary>
        public static Vector3 DotLocalPosition(NeckLayout layout, FingertipDot dot)
        {
            return LocalMetres(dot.xMm, layout.stringHeightMm, dot.zMm);
        }

        /// <summary>x in mm of the strum zone on the real guitar: the measured soundhole distance, or the documented default.</summary>
        public static float StrumZoneXMm(NeckLayout layout)
        {
            if (layout.nutToSoundholeMm > 0f) return layout.nutToSoundholeMm;
            return layout.scaleLengthMm * DefaultSoundholeFractionOfScale;
        }

        /// <summary>
        /// x in mm of the strum zone on the floating neck. The real distance (about half a metre) would put the strumming hand
        /// 30 degrees to the right of the fretting hand, at the edge of a 46 degree view, so the floating neck is drawn shortened:
        /// the strum zone sits <paramref name="gapAfterLastFretMm"/> beyond the last fret, never beyond its real place.
        /// A gap of 0 or less means "do not shorten".
        /// </summary>
        public static float FloatingStrumZoneXMm(NeckLayout layout, float gapAfterLastFretMm)
        {
            float real = StrumZoneXMm(layout);
            if (!(gapAfterLastFretMm > 0f)) return real;
            return Mathf.Min(real, layout.LastFretXMm + gapAfterLastFretMm);
        }

        /// <summary>
        /// Size in metres of something that spans <paramref name="degrees"/> of view at <paramref name="distanceM"/> metres:
        /// 2 d tan(angle / 2). 1.2 degrees at 1.2 m is 2.5 cm, the project's minimum text height (MASTER_PLAN section 17).
        /// </summary>
        public static float SizeForAngle(float degrees, float distanceM)
        {
            if (!(degrees > 0f) || !(distanceM > 0f)) return 0f;
            return 2f * distanceM * Mathf.Tan(Mathf.Min(degrees, 170f) * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>Inverse of <see cref="SizeForAngle"/>: the degrees of view that <paramref name="sizeM"/> spans at <paramref name="distanceM"/>.</summary>
        public static float AngleForSize(float sizeM, float distanceM)
        {
            if (!(sizeM > 0f) || !(distanceM > 0f)) return 0f;
            return 2f * Mathf.Atan(sizeM / (2f * distanceM)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Horizontal and vertical field of view of a flat (rectilinear) display from its diagonal field of view and aspect
        /// ratio: tan(h/2) = tan(d/2) * a / sqrt(a*a + 1), tan(v/2) = tan(d/2) / sqrt(a*a + 1). The glasses' 52 degree diagonal
        /// at 16:9 gives about 46 by 27 degrees.
        /// </summary>
        public static void FovFromDiagonal(float diagonalDeg, float aspect, out float horizontalDeg, out float verticalDeg)
        {
            float tanHalfDiagonal = Mathf.Tan(diagonalDeg * 0.5f * Mathf.Deg2Rad);
            float hypotenuse = Mathf.Sqrt(aspect * aspect + 1f);
            horizontalDeg = 2f * Mathf.Atan(tanHalfDiagonal * aspect / hypotenuse) * Mathf.Rad2Deg;
            verticalDeg = 2f * Mathf.Atan(tanHalfDiagonal / hypotenuse) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Display pixels per degree at the centre of a flat display that shows <paramref name="fovDeg"/> across
        /// <paramref name="pixels"/> pixels (there are slightly more per degree toward the edges).
        /// </summary>
        public static float PixelsPerDegreeAtCentre(float fovDeg, int pixels)
        {
            float tanHalf = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            if (!(tanHalf > 0f)) return 0f;
            return (pixels * 0.5f / tanHalf) * Mathf.Tan(Mathf.Deg2Rad);
        }

        /// <summary>
        /// Moves <paramref name="current"/> toward <paramref name="target"/> by at most ratePerSecond * dt. Brightness changes
        /// go through this, so however fast a caller toggles a value, a full swing takes at least 1 / ratePerSecond seconds and
        /// nothing can flash faster than the 3 Hz limit.
        /// </summary>
        public static float Slew(float current, float target, float ratePerSecond, float dt)
        {
            if (!(dt > 0f) || !(ratePerSecond > 0f)) return current;
            float step = ratePerSecond * dt;
            float delta = target - current;
            if (delta > step) return current + step;
            if (delta < -step) return current - step;
            return target;
        }
    }
}
