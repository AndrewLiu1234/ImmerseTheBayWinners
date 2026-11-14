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

        /// <summary>
        /// Checks one stroke's pick path (neck mm, in time order, the first <paramref name="count"/> points) against the strings:
        /// every string from <paramref name="fromString"/> to <paramref name="toString"/> must be crossed (its z passed between
        /// two samples, interpolated) while the pick is within maxHeightMm of the string plane (stringYMm); no other string may
        /// be crossed that low (a string the chord does not use); and the path must start and end at least minEndLiftMm above
        /// the plane. stringZMm[s - 1] is string s's z. 'why' lists every failure (null when it passes).
        /// </summary>
        public static bool StrokeStrikes(Vector3[] path, int count, float[] stringZMm, float stringYMm, int fromString, int toString,
                                         float maxHeightMm, float minEndLiftMm, out string why)
        {
            why = null;
            int n = path == null ? 0 : Mathf.Min(count, path.Length);
            if (n < 2 || stringZMm == null || stringZMm.Length < 6)
            {
                why = "no path or no strings";
                return false;
            }
            int lo = Mathf.Min(fromString, toString), hi = Mathf.Max(fromString, toString);
            for (int s = 1; s <= 6; s++)
            {
                bool struck = Struck(path, n, stringZMm[s - 1], stringYMm, maxHeightMm);
                bool wanted = s >= lo && s <= hi;
                if (wanted && !struck) why = Join(why, "string " + s + " is not crossed within " + Mm(maxHeightMm) + " mm of the string plane");
                if (!wanted && struck) why = Join(why, "string " + s + " is struck but the stroke should not reach it");
            }
            float startLift = path[0].y - stringYMm, endLift = path[n - 1].y - stringYMm;
            if (!(startLift >= minEndLiftMm)) why = Join(why, "the stroke starts only " + Mm(startLift) + " mm above the strings (at least " + Mm(minEndLiftMm) + ")");
            if (!(endLift >= minEndLiftMm)) why = Join(why, "the stroke ends only " + Mm(endLift) + " mm above the strings (at least " + Mm(minEndLiftMm) + ")");
            return why == null;
        }

        // True when the path passes string z at a height within maxHeight of the plane at some crossing.
        static bool Struck(Vector3[] path, int n, float z, float planeY, float maxHeight)
        {
            for (int i = 0; i + 1 < n; i++)
            {
                float a = path[i].z - z, b = path[i + 1].z - z;
                if (a == 0f && b == 0f) continue;
                if (a * b > 0f) continue;
                float t = a == b ? 0f : a / (a - b);
                float y = path[i].y + (path[i + 1].y - path[i].y) * t;
                if (Mathf.Abs(y - planeY) <= maxHeight) return true;
            }
            return false;
        }

        static string Join(string a, string b)
        {
            return a == null ? b : a + "; " + b;
        }

        static string Mm(float v)
        {
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
