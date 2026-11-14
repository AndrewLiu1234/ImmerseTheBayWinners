// GhostHandChecks: relational checks between the ghost hands and the neck they are drawn on (element against element, never
// one element alone), for previews and tests. Pure maths on neck-frame millimetres (origin at the nut on the fretboard,
// +x to the bridge, +y up, +z toward string 1). Editor/GhostHandPreview.cs reports them per frame, including a planted
// case that must fire; Tests/EditMode/GhostHandsDirectorTests.cs shows each one firing on a planted overlap.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public static class GhostHandChecks
    {
        /// <summary>
        /// True when the strumming hand's pick point (neck mm) is over the drawn strings: between stringsFromXMm and
        /// stringsToXMm along the neck, and across the neck between string 6 (zString6Mm) and string 1 (zString1Mm) at that x,
        /// widened by marginMm on both sides. 'why' says which side it misses on (null when it passes).
        /// </summary>
        public static bool PickOverStrings(Vector3 pickMm, float stringsFromXMm, float stringsToXMm, float zString1Mm, float zString6Mm,
                                           float marginMm, out string why)
        {
            why = null;
            float lo = Mathf.Min(zString1Mm, zString6Mm) - marginMm, hi = Mathf.Max(zString1Mm, zString6Mm) + marginMm;
            if (!(pickMm.x >= stringsFromXMm)) why = "the pick is " + Mm(stringsFromXMm - pickMm.x) + " mm toward the nut from where the strings start";
            else if (!(pickMm.x <= stringsToXMm)) why = "the pick is " + Mm(pickMm.x - stringsToXMm) + " mm beyond the end of the drawn strings";
            else if (!(pickMm.z >= lo)) why = "the pick is " + Mm(lo - pickMm.z) + " mm outside string 6";
            else if (!(pickMm.z <= hi)) why = "the pick is " + Mm(pickMm.z - hi) + " mm outside string 1";
            return why == null;
        }

        /// <summary>
        /// The smallest distance (mm) between any point of a and any point of b (the first countA / countB entries);
        /// infinity when either is empty. The two hands are apart when it is at least the gap you need.
        /// </summary>
        public static float MinDistanceMm(Vector3[] a, int countA, Vector3[] b, int countB)
        {
            float best = float.PositiveInfinity;
            if (a == null || b == null) return best;
            int na = Mathf.Min(countA, a.Length), nb = Mathf.Min(countB, b.Length);
            for (int i = 0; i < na; i++)
                for (int j = 0; j < nb; j++)
                {
                    float d = (a[i] - b[j]).magnitude;
                    if (d < best) best = d;
                }
            return best;
        }

        static string Mm(float v)
        {
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
