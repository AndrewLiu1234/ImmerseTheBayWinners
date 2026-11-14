// CapsuleHandSolver: places the palm and solves every finger (FingerIk) for a set of fingertip targets.
// Plain C#: no Unity object, no allocation after construction, deterministic. CapsuleHand (the MonoBehaviour) only applies its output.
//
// COORDINATES
//   Targets and outputs named "parent" are in the parent Transform's local space (metres), the space the caller works in.
//   Finger results are in the PALM's local frame at scale 1 (see CapsuleHandDimensions); the palm transform carries the scale.
//
// THE NECK FRAME (how the fretting hand is placed)
//   u = neckAxis    unit, along the neck from the nut toward the body (frets count up along +u)
//   n = neckNormal  unit, out of the fretboard on the string side (toward the player's eyes)
//   t = the palm side: across the neck toward where the palm sits (the treble side for a player fretting with the left hand).
//       t = cross(u, n) for the left hand and -cross(u, n) for the right hand (a mirrored guitar), times -1 when flipPalmSide.
//   The palm hangs below the neck on the t side, its fingers pointing forward over the strings, tilted toward the bass side by
//   palmTiltDegrees; the knuckles float palmHeight above the fingertip plane and palmSideOffset to the t side of the fingertip
//   centroid; the thumb rests behind the neck (neckDepth behind the fretboard, opposite the fingertips). With the default frame
//   (u = +x, n = +y) a left hand's t is +z, so string 1 is at +z.
//   The index finger ends up on the nut side (thumb toward the nut), the little finger toward the body.
//
// FINGER TARGETS
//   A target is the contact point of the fingertip pad (a fingertip dot). The fingertip centre is placed fingertipHover + the tip
//   radius above it along n, so the ghost hovers 5-10 mm above the strings. Each finger has a weight in 0..1: 1 = reach the target,
//   0 = relaxed curl (no target); in between blends smoothly. Weights are what makes a finger easing in or out of use continuous.
//
// MANUAL WRIST
//   SetManualWrist(position, rotation) skips the fretting placement (for the strumming hand, or a hand posed by other code): the
//   palm is exactly where you say, fingers still solve to their targets, the thumb only follows an explicit thumb target.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>Quaternion helpers made of plain arithmetic (no engine calls), so they work and test anywhere.</summary>
    public static class CapsuleHandMath
    {
        /// <summary>The rotation whose local x, y, z axes land on the given orthonormal right-handed (Unity) basis vectors.</summary>
        public static Quaternion BasisToQuaternion(Vector3 right, Vector3 up, Vector3 forward)
        {
            float m00 = right.x, m01 = up.x, m02 = forward.x;
            float m10 = right.y, m11 = up.y, m12 = forward.y;
            float m20 = right.z, m21 = up.z, m22 = forward.z;
            float qx, qy, qz, qw;
            float tr = m00 + m11 + m22;
            if (tr > 0f)
            {
                float s = Mathf.Sqrt(tr + 1f) * 2f;
                qw = 0.25f * s; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                qw = (m21 - m12) / s; qx = 0.25f * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25f * s; qz = (m12 + m21) / s;
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25f * s;
            }
            float len = Mathf.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len < 1e-8f) return Quaternion.identity;
            return new Quaternion(qx / len, qy / len, qz / len, qw / len);
        }

        /// <summary>Like Quaternion.LookRotation (z along forward, y as close to upHint as possible), but never fails and never calls the engine.</summary>
        public static Quaternion LookRotationSafe(Vector3 forward, Vector3 upHint)
        {
            Vector3 f = SafeNormalize(forward, Vector3.forward);
            Vector3 right = Vector3.Cross(upHint, f);
            if (right.sqrMagnitude < 1e-8f) right = Vector3.Cross(Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right, f);
            right = right.normalized;
            Vector3 up = Vector3.Cross(f, right);
            return BasisToQuaternion(right, up, f);
        }

        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float m = v.magnitude;
            if (!(m > 1e-6f) || float.IsInfinity(m)) return fallback;
            return v / m;
        }

        public static bool IsFinite(Vector3 v)
        {
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }
    }

    public sealed class CapsuleHandSolver
    {
        public const int FingerCount = CapsuleHandDimensions.FingerCount;
        public const int ThumbIndex = CapsuleHandDimensions.ThumbIndex;

        public readonly bool leftHand;
        public readonly CapsuleHandDimensions dims;

        // ---- placement tunables (public so the integrator can fit them to the real guitar) ----
        public float scale = 1f;                    // uniform hand size; 1 = adult
        public Vector3 neckAxis = Vector3.right;    // parent-local u: nut -> body
        public Vector3 neckNormal = Vector3.up;     // parent-local n: out of the fretboard toward the player
        public bool flipPalmSide;                   // true if the palm ends up on the wrong side of the neck
        public float palmTiltDegrees = 30f;         // fingers point this far toward the bass side instead of straight out of the board
        public float palmHeight = 0.050f;           // knuckles this far above the fingertip plane (metres, scaled by `scale`)
        public float palmSideOffset = 0.045f;       // knuckles this far toward the palm side of the fingertip centroid (scaled)
        public float neckDepth = 0.024f;            // fretboard surface to the back of the neck (not scaled)
        public float fingertipHover = 0.006f;       // the pad hovers this far above its target (not scaled)
        public float thumbAlongNeck = -0.010f;      // thumb pad sits this far along +u from the fingertip centroid (negative = toward the nut)

        // ---- outputs of the last Solve ----
        public Vector3 palmPosition;                // parent space
        public Quaternion palmRotation = Quaternion.identity;
        public Vector3 axisX = Vector3.right, axisY = Vector3.up, axisZ = Vector3.forward;   // palm axes in parent space (unit)
        public readonly FingerIkResult[] fingers = new FingerIkResult[FingerCount];   // palm-local, scale 1
        public readonly Vector3[] tipCentre = new Vector3[FingerCount];    // parent space
        public readonly Vector3[] contactPoint = new Vector3[FingerCount]; // parent space: where the pad touches (= the target when reachable)
        public readonly float[] fingerWeight = new float[FingerCount];     // effective target weight used for each finger

        bool m_manual;
        Vector3 m_wristPosition;
        Quaternion m_wristRotation = Quaternion.identity;
        Vector3 m_lastCentroid;                     // where the fingertips last were (the palm stays there when no finger is used)
        const float WeightEpsilon = 1e-3f;

        public CapsuleHandSolver(bool leftHand)
        {
            this.leftHand = leftHand;
            dims = CapsuleHandDimensions.CreateDefault(leftHand);
            for (int i = 0; i < FingerCount; i++) fingers[i] = default(FingerIkResult);
        }

        public bool IsManualWrist { get { return m_manual; } }

        public void SetManualWrist(Vector3 position, Quaternion rotation)
        {
            m_manual = true;
            m_wristPosition = position;
            m_wristRotation = rotation;
        }

        public void ClearManualWrist() { m_manual = false; }

        /// <summary>Where the palm waits while no finger has a target (the first targets set this too, so the hand appears in place).</summary>
        public void SetRestCentroid(Vector3 centroid) { m_lastCentroid = centroid; }

        public Vector3 RestCentroid { get { return m_lastCentroid; } }

        /// <summary>
        /// Place the palm and solve all fingers. targets[0..3] = index..little, targets[4] = thumb (optional: pass an array of 4
        /// and the thumb rests behind the neck). weights likewise: 1 = reach, 0 = relaxed. Arrays are read, never kept.
        /// </summary>
        public void Solve(Vector3[] targets, float[] weights)
        {
            float s = Mathf.Max(scale, 0.05f);

            // ---- the neck frame ----
            Vector3 u = CapsuleHandMath.SafeNormalize(neckAxis, Vector3.right);
            Vector3 n = neckNormal - u * Vector3.Dot(neckNormal, u);
            if (!(n.sqrMagnitude > 1e-8f)) n = Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.forward;
            n = CapsuleHandMath.SafeNormalize(n - u * Vector3.Dot(n, u), Vector3.up);

            // ---- which targets count ----
            int count = targets == null ? 0 : targets.Length;
            int wcount = weights == null ? 0 : weights.Length;
            float sumW = 0f;
            Vector3 acc = Vector3.zero;
            for (int i = 0; i < FingerCount; i++)
            {
                float w = 0f;
                if (i < count && i < wcount && CapsuleHandMath.IsFinite(targets[i])) w = Mathf.Clamp01(weights[i]);
                fingerWeight[i] = w;
                if (i < ThumbIndex && w > 0f) { sumW += w; acc += targets[i] * w; }
            }
            Vector3 centroid = (acc + m_lastCentroid * WeightEpsilon) / (sumW + WeightEpsilon);
            if (sumW > 0.5f) m_lastCentroid = centroid;

            // ---- the palm ----
            if (m_manual)
            {
                palmPosition = m_wristPosition;
                palmRotation = m_wristRotation;
                axisX = palmRotation * Vector3.right;
                axisY = palmRotation * Vector3.up;
                axisZ = palmRotation * Vector3.forward;
            }
            else
            {
                float handSign = (leftHand ? 1f : -1f) * (flipPalmSide ? -1f : 1f);
                Vector3 t = Vector3.Cross(u, n) * handSign;
                float phi = palmTiltDegrees * Mathf.Deg2Rad;
                float cp = Mathf.Cos(phi), sp = Mathf.Sin(phi);
                Vector3 f = n * cp - t * sp;            // fingers: forward and tilted toward the bass side
                Vector3 p = -t * cp - n * sp;           // palm normal: toward the neck (and the bass side)
                axisY = -p;                             // dorsal
                axisZ = f;
                axisX = Vector3.Cross(axisY, axisZ);
                palmRotation = CapsuleHandMath.BasisToQuaternion(axisX, axisY, axisZ);

                // knuckle centre: weighted mean of the knuckles of the fingers in use (all four when none is)
                Vector3 kAll = Vector3.zero, kUsed = Vector3.zero;
                for (int i = 0; i < ThumbIndex; i++)
                {
                    kAll += dims.chains[i].knuckle * 0.25f;
                    kUsed += dims.chains[i].knuckle * fingerWeight[i];
                }
                Vector3 kMean = (kUsed + kAll * WeightEpsilon) / (sumW + WeightEpsilon);

                Vector3 knuckleCentre = centroid + t * (palmSideOffset * s) + n * (palmHeight * s);
                palmPosition = knuckleCentre - ToParent(kMean * s);
            }

            // ---- the four fingers ----
            for (int i = 0; i < ThumbIndex; i++)
            {
                float w = fingerWeight[i];
                Vector3 local = dims.relaxedTip[i];
                if (w > 0f)
                {
                    float lift = fingertipHover + dims.TipRadius(i) * s;
                    Vector3 q = ToPalm(targets[i] + n * lift - palmPosition) / s;
                    local = Vector3.Lerp(local, q, w);
                }
                FingerIk.Solve(dims.chains[i], local, out fingers[i]);
            }

            // ---- the thumb ----
            {
                float liftT = fingertipHover + dims.TipRadius(ThumbIndex) * s;
                float wExplicit = fingerWeight[ThumbIndex];
                float wAuto = m_manual ? 0f : Mathf.Clamp01(sumW);
                Vector3 behindNeck = centroid + u * thumbAlongNeck - n * (neckDepth + liftT);
                Vector3 targetParent = behindNeck;
                if (wExplicit > 0f) targetParent = Vector3.Lerp(behindNeck, targets[ThumbIndex] + n * liftT, wExplicit);
                float wThumb = Mathf.Max(wAuto, wExplicit);
                fingerWeight[ThumbIndex] = wThumb;

                Vector3 local = dims.relaxedTip[ThumbIndex];
                if (wThumb > 0f) local = Vector3.Lerp(local, ToPalm(targetParent - palmPosition) / s, wThumb);
                FingerIk.Solve(dims.chains[ThumbIndex], local, out fingers[ThumbIndex]);
            }

            // ---- where everything ended up, in parent space ----
            for (int i = 0; i < FingerCount; i++)
            {
                tipCentre[i] = palmPosition + ToParent(fingers[i].tip * s);
                contactPoint[i] = tipCentre[i] - n * (fingertipHover + dims.TipRadius(i) * s);
            }
        }

        /// <summary>Palm-local vector (scale 1 or already scaled by the caller) to parent space (rotation only).</summary>
        public Vector3 ToParent(Vector3 palmLocal)
        {
            return axisX * palmLocal.x + axisY * palmLocal.y + axisZ * palmLocal.z;
        }

        /// <summary>Parent-space offset to palm-local coordinates (rotation only, not divided by scale).</summary>
        public Vector3 ToPalm(Vector3 parentOffset)
        {
            return new Vector3(Vector3.Dot(parentOffset, axisX), Vector3.Dot(parentOffset, axisY), Vector3.Dot(parentOffset, axisZ));
        }
    }
}
