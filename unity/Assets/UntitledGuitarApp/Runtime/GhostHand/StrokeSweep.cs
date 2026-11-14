// StrokeSweep: where the strumming ghost's pick goes during a stroke, and the translation that puts it there on top of the
// authored pose blend (the hand's shape stays the authored one). Pure maths on neck-frame millimetres; tested in
// Tests/EditMode/GhostHandsDirectorTests.cs.
//
// THE PICK. art/blender/hand/pose_hands.py holds the pick PICK_REACH_MM = 9 mm beyond the thumb-index pinch on the palm
// side: pick = pinch + D[wrist] (0, -9, 0) in the hand's model axes (+y = back of the hand). Rebuilt from the five
// strumming pose files this matches the authored pick of every key pose to 0.0 mm (STRUM_POSES: down-start 12 mm outside
// string 6, down-end 12 mm past string 1, both 1 mm above the strings).
//
// THE SWEEP. The authored strokes always start outside string 6, stay 1 mm above the strings all the way and are the same
// for every chord, so on the glasses they read as a fist sliding sideways. The target instead (StrokeSweep.Target):
//   D  from SweepMarginMm outside the CHORD'S bass string (ChordShape.BassString: G/Em 6, C/Am 5, D 4) to SweepMarginMm
//      past string 1; the strings struck are bass .. 1.
//   U  from SweepMarginMm past string 1 to half-way between strings 3 and 4 (a beginner's upstroke hits the treble strings);
//      the strings struck are 1 .. 3.
// Across the struck strings the pick is PickAboveStringsMm above the string centre line (in the string plane); outside them
// it lifts by up to EndLiftMm (smoothstep over SweepMarginMm), so each stroke dips into the strings and comes out at both
// ends. z goes linearly with the stroke's blend weight (already eased in time), so shape and sweep move together.
// The translation is target - authored pick, applied to the whole hand (GhostPoseDriver.RootOffsetMm); along the neck (x)
// it is zero, so the strum line stays where it was authored.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public static class StrokeSweep
    {
        /// <summary>The pick in the hand's model axes relative to the thumb-index pinch, mm (pose_hands.py PICK_REACH_MM).</summary>
        public static readonly Vector3 PickFromPinchInHandMm = new Vector3(0f, -9f, 0f);
        /// <summary>A stroke starts and ends this far outside its outermost struck string.</summary>
        public const float SweepMarginMm = 5f;
        /// <summary>The pick's height above the string centre line while it crosses the struck strings.</summary>
        public const float PickAboveStringsMm = 1f;
        /// <summary>How much higher the pick is at the ends of a stroke, outside the struck strings.</summary>
        public const float EndLiftMm = 6f;
        /// <summary>The highest string an upstroke strikes (it ends half-way to the next one).</summary>
        public const int UpStrokeLastString = 3;

        /// <summary>The pick of a hand from its thumb tip, index tip and the wrist's total rotation (pose or neck frame).</summary>
        public static Vector3 Pick(Vector3 thumbTipMm, Vector3 indexTipMm, Quaternion wristRotation)
        {
            Vector3 pinch = 0.5f * (thumbTipMm + indexTipMm);
            return pinch + wristRotation * PickFromPinchInHandMm;
        }

        /// <summary>
        /// The strings a stroke strikes, as string numbers: D from the bass string (1-6; anything else = 6) to string 1, U from
        /// string 1 to UpStrokeLastString. fromString is where the stroke begins.
        /// </summary>
        public static void StruckStrings(char kind, int bassString, out int fromString, out int toString)
        {
            if (kind == 'U')
            {
                fromString = 1;
                toString = UpStrokeLastString;
                return;
            }
            fromString = bassString >= 1 && bassString <= 6 ? bassString : 6;
            toString = 1;
        }

        /// <summary>
        /// Where the pick should be at stroke weight u (0..1) of a D or U stroke, neck-frame mm: x = xMm (the authored strum
        /// line), z across the strings as in the file header, y = stringYMm + PickAboveStringsMm inside the struck strings and
        /// up to EndLiftMm higher outside them. stringZMm[s - 1] is string s's z at x (index 0 = string 1, +z toward string 1).
        /// A kind other than 'D' / 'U', or fewer than 6 string positions, gives (xMm, stringYMm + PickAboveStringsMm, 0).
        /// </summary>
        public static Vector3 Target(char kind, int bassString, float u, float xMm, float stringYMm, float[] stringZMm)
        {
            float plane = stringYMm + PickAboveStringsMm;
            if ((kind != 'D' && kind != 'U') || stringZMm == null || stringZMm.Length < 6) return new Vector3(xMm, plane, 0f);
            int from, to;
            StruckStrings(kind, bassString, out from, out to);
            float zFrom = stringZMm[from - 1], zTo = stringZMm[to - 1];
            float sign = zTo >= zFrom ? 1f : -1f;                       // the stroke's direction across the neck
            float zStart = zFrom - sign * SweepMarginMm;
            float zEnd = kind == 'U' ? 0.5f * (stringZMm[UpStrokeLastString - 1] + stringZMm[UpStrokeLastString]) : zTo + sign * SweepMarginMm;
            float w = u > 0f ? (u < 1f ? u : 1f) : 0f;
            float z = zStart + (zEnd - zStart) * w;
            float lo = Mathf.Min(zFrom, zTo), hi = Mathf.Max(zFrom, zTo);
            float outside = Mathf.Max(0f, Mathf.Max(lo - z, z - hi));
            return new Vector3(xMm, plane + EndLiftMm * StrumMath.Ease(outside / SweepMarginMm), z);
        }

        /// <summary>The whole-hand translation that moves an authored pick onto the target: zero along the neck (x).</summary>
        public static Vector3 Offset(Vector3 targetMm, Vector3 authoredPickMm)
        {
            return new Vector3(0f, targetMm.y - authoredPickMm.y, targetMm.z - authoredPickMm.z);
        }
    }
}
