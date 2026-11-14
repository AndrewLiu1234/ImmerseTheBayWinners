// CapsuleHandSolver: decides where the palm goes and where every fingertip goes, for a set of fingertip targets.
// Plain C#: no Unity object, no allocation after construction, deterministic. CapsuleHand (the MonoBehaviour) only
// eases between the poses this class produces and applies them.
//
// COORDINATES
//   "parent" = the local space of the transform the hand hangs under (for a fretting hand: the neck frame), metres.
//   "palm"   = the palm's own frame at scale 1 (see CapsuleHandDimensions); the wrist transform carries the scale.
//   "neck"   = (along, out, side): a point's components along u, n and t below. All the fitting is done in these.
//
// THE NECK FRAME (pinned for the project)
//   origin on the neck's centre line at the nut, on the fretboard's playing surface
//   u = neckAxis    (+x) along the neck from the nut toward the bridge
//   n = neckNormal  (+y) up out of the fretboard
//   t = the palm side: cross(u, n) for a left hand (= +z, toward string 1, the treble side) and the opposite for a
//       right hand.
//   The palm hangs on the t side of the neck, below its edge, with the fingers curling up and over the fretboard, the
//   index finger nearest the nut, and the thumb resting behind the neck.
//
// LEFT AND RIGHT: every fretting pose is solved as a LEFT hand in neck coordinates and, for a right hand, mirrored at the
// very end. A right hand is therefore exactly the mirror image (through the plane t = 0) of the left hand with mirrored
// targets, by construction.
//
// A HAND POSE (HandPose) is the palm's position and rotation in parent space plus one fingertip-centre target per finger
// in PALM space. Blending two poses therefore moves the palm and curls the fingers smoothly, and every in-between pose is
// again solved by FingerIk, so nothing pops.
//
// FINGERTIP TARGETS given by the caller are where the fingertip PAD should touch (a fingertip dot). The fingertip centre is
// placed fingertipHover + the tip radius above the target along n, so the ghost hovers just above the strings.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>Quaternion and vector helpers made of plain arithmetic (no engine calls), so they work and test anywhere.</summary>
    public static class CapsuleHandMath
    {
        /// <summary>The rotation whose local x, y, z axes land on the given orthonormal (Unity, left-handed) basis vectors.</summary>
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
            return Normalized(new Quaternion(qx, qy, qz, qw));
        }

        /// <summary>Like Quaternion.LookRotation (z along forward, y as close to upHint as possible), but never fails and never calls the engine.</summary>
        public static Quaternion LookRotationSafe(Vector3 forward, Vector3 upHint)
        {
            Vector3 f = SafeNormalize(forward, Vector3.forward);
            Vector3 right = Vector3.Cross(upHint, f);
            if (!(right.sqrMagnitude > 1e-10f)) right = Vector3.Cross(Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right, f);
            right = SafeNormalize(right, Vector3.right);
            Vector3 up = Vector3.Cross(f, right);
            return BasisToQuaternion(right, up, f);
        }

        /// <summary>The rotation by |w| radians about the axis w (a rotation vector).</summary>
        public static Quaternion FromRotationVector(Vector3 w)
        {
            float angle = w.magnitude;
            if (!(angle > 1e-9f)) return Quaternion.identity;
            float h = 0.5f * angle;
            float s = Mathf.Sin(h) / angle;
            return new Quaternion(w.x * s, w.y * s, w.z * s, Mathf.Cos(h));
        }

        /// <summary>The inverse of a unit quaternion.</summary>
        public static Quaternion Conjugate(Quaternion q) { return new Quaternion(-q.x, -q.y, -q.z, q.w); }

        public static Quaternion Normalized(Quaternion q)
        {
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(len > 1e-8f) || float.IsInfinity(len)) return Quaternion.identity;
            return new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
        }

        /// <summary>Spherical interpolation along the shorter arc, t in 0..1.</summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f) { b = new Quaternion(-b.x, -b.y, -b.z, -b.w); dot = -dot; }
            float wa, wb;
            if (dot > 0.9995f) { wa = 1f - t; wb = t; }                 // nearly the same rotation: a straight blend is exact enough
            else
            {
                float theta = Mathf.Acos(Mathf.Min(dot, 1f));
                float sin = Mathf.Sin(theta);
                wa = Mathf.Sin((1f - t) * theta) / sin;
                wb = Mathf.Sin(t * theta) / sin;
            }
            return Normalized(new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb));
        }

        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float m = v.magnitude;
            if (!(m > 1e-6f) || float.IsInfinity(m)) return fallback;
            return v / m;
        }

        public static bool IsFinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        public static bool IsFinite(Vector3 v) { return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z); }

        public static bool IsFinite(Quaternion q) { return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w); }
    }

    /// <summary>Where the palm is (parent space) and where each fingertip centre should be (palm space, scale 1).</summary>
    public struct HandPose
    {
        public Vector3 palmPosition;
        public Quaternion palmRotation;
        public Vector3 tip0, tip1, tip2, tip3, tip4;    // index, middle, ring, little, thumb

        public Vector3 GetTip(int finger)
        {
            switch (finger)
            {
                case 0: return tip0;
                case 1: return tip1;
                case 2: return tip2;
                case 3: return tip3;
                default: return tip4;
            }
        }

        public void SetTip(int finger, Vector3 value)
        {
            switch (finger)
            {
                case 0: tip0 = value; break;
                case 1: tip1 = value; break;
                case 2: tip2 = value; break;
                case 3: tip3 = value; break;
                default: tip4 = value; break;
            }
        }

        /// <summary>The pose a fraction t (0..1) of the way from a to b.</summary>
        public static void Lerp(in HandPose a, in HandPose b, float t, out HandPose result)
        {
            result.palmPosition = Vector3.LerpUnclamped(a.palmPosition, b.palmPosition, t);
            result.palmRotation = CapsuleHandMath.Slerp(a.palmRotation, b.palmRotation, t);
            result.tip0 = Vector3.LerpUnclamped(a.tip0, b.tip0, t);
            result.tip1 = Vector3.LerpUnclamped(a.tip1, b.tip1, t);
            result.tip2 = Vector3.LerpUnclamped(a.tip2, b.tip2, t);
            result.tip3 = Vector3.LerpUnclamped(a.tip3, b.tip3, t);
            result.tip4 = Vector3.LerpUnclamped(a.tip4, b.tip4, t);
        }
    }

    public sealed class CapsuleHandSolver
    {
        public const int FingerCount = CapsuleHandDimensions.FingerCount;
        public const int ThumbIndex = CapsuleHandDimensions.ThumbIndex;

        public readonly bool leftHand;
        /// <summary>The proportions of this hand (left or right).</summary>
        public readonly CapsuleHandDimensions dims;

        // ---- placement tunables (public so the integrator can fit them to the real guitar) ----
        public float scale = 1f;                    // uniform hand size; 1 = adult
        public Vector3 neckAxis = Vector3.right;    // parent-space u: nut -> bridge
        public Vector3 neckNormal = Vector3.up;     // parent-space n: out of the fretboard
        public float palmTiltDegrees = 25f;         // the palm leans this far over the neck (0 = it stands straight up beside it)
        public float palmHeight = 0.004f;           // knuckles this far above the fingertip centres (metres, times scale)
        public float palmSideOffset = 0.040f;       // knuckles this far toward the palm side of the fingertips (times scale)
        public float neckDepth = 0.022f;            // fretboard surface to the back of the neck (not scaled)
        public float neckHalfWidth = 0.023f;        // half the width of the neck where the hand is (the palm stays outside it)
        public float fingertipHover = 0.006f;       // the pad hovers this far above its target (not scaled)
        public float hoverRaise = 0.016f;           // a finger that is not used hovers this far above the fretting height
        public float thumbAlongNeck = -0.010f;      // the thumb sits this far along +u from the fingertips (negative = toward the nut)
        public float thumbAcrossNeck = 0f;          // the thumb's place across the back of the neck (+ = toward the palm side)
        public Vector3 restCentroid = new Vector3(0.062f, 0f, 0f);   // where the fingers hover while nothing is fretted (parent space)

        /// <summary>Steps of the palm fit (see FitPalm). 0 = keep the nominal posture: cheaper, but fingertips can miss by a centimetre.</summary>
        public int fitIterations = 14;

        // ---- facts about the last SolveFretting ----
        /// <summary>Per finger (index, middle, ring, little, thumb): did the caller give it a target?</summary>
        public readonly bool[] used = new bool[FingerCount];
        /// <summary>Per finger: how far (metres) the fingertip ends from where it was asked to be. 0 = on the target.</summary>
        public readonly float[] reachError = new float[FingerCount];

        // ---- the palm fit: 6 unknowns, damped least squares, fixed work, no allocation ----
        const int ParamCount = 6;               // knuckle-centre offset along, out, side (metres); rotation vector about those axes (radians)
        const int PerFinger = 6;                // residuals per finger: reach error x y z, swing, first-joint comfort, second-joint comfort
        const int ResidualCount = 4 * PerFinger + 3 + ParamCount + 2;
        const double ReachWeight = 100.0;       // per metre: 1 mm off a target outweighs every comfort term together
        const double ThumbWeight = 12.0;        // the thumb behind the neck matters less than the fretting fingers
        const double SwingWeight = 0.05;        // per radian of sideways swing
        const double McpWeight = 0.03, PipWeight = 0.03;
        const double McpComfort = 0.45, PipComfort = 1.40;      // the curl a fretting finger likes (radians)
        const double PositionWeight = 1.0;      // per metre away from the nominal knuckle position
        const double RotationWeight = 0.05;     // per radian away from the nominal palm rotation
        const double YawComfort = 0.10;         // the forearm comes from the bridge side: a small positive turn about n
        const double BackwardYawWeight = 0.30;  // extra cost per radian of turning the other way (elbow beyond the headstock)
        const double ClearanceWeight = 25.0;    // per metre of the knuckles entering the neck
        const double FitTolerance = 0.0005;     // a fit whose worst fingertip is within 0.5 mm needs no second start

        static readonly double[] ParamMin = { -0.06, -0.06, -0.06, -0.8, -0.35, -0.8 };
        static readonly double[] ParamMax = { 0.06, 0.06, 0.06, 0.8, 0.8, 0.8 };

        // other starting points tried (in this order) only when the first fit leaves a fingertip off its target
        static readonly double[][] Restarts =
        {
            new[] { 0.0, 0.0, 0.0, 0.0, 0.30, 0.0 },
            new[] { 0.0, 0.012, 0.0, 0.0, 0.0, 0.0 },
            new[] { 0.0, -0.008, 0.012, 0.0, 0.0, 0.0 },
            new[] { 0.0, 0.0, 0.0, 0.25, 0.0, 0.0 },
            new[] { 0.0, 0.0, 0.0, -0.25, 0.0, 0.0 },
            new[] { 0.0, 0.0, 0.0, 0.0, -0.25, 0.0 },
        };

        readonly CapsuleHandDimensions m_left;  // the fit always works on a left hand (see LEFT AND RIGHT above)
        readonly double[] m_x = new double[ParamCount];
        readonly double[] m_xTry = new double[ParamCount];
        readonly double[] m_xBest = new double[ParamCount];
        readonly double[] m_res = new double[ResidualCount];
        readonly double[] m_resTry = new double[ResidualCount];
        readonly double[] m_jac = new double[ResidualCount * ParamCount];
        readonly double[] m_a = new double[ParamCount * ParamCount];
        readonly double[] m_l = new double[ParamCount * ParamCount];
        readonly double[] m_g = new double[ParamCount];
        readonly double[] m_d = new double[ParamCount];

        readonly Vector3[] m_centre = new Vector3[FingerCount];     // fingertip-centre targets, neck coordinates
        Vector3 m_u = Vector3.right, m_n = Vector3.up, m_t = Vector3.forward;
        Vector3 m_knuckleNominal, m_knuckleMean, m_thumbTarget;     // neck, palm (left), neck
        Quaternion m_rotNominal = Quaternion.identity;
        float m_s = 1f;

        public CapsuleHandSolver(bool leftHand)
        {
            this.leftHand = leftHand;
            dims = CapsuleHandDimensions.CreateDefault(leftHand);
            m_left = leftHand ? dims : CapsuleHandDimensions.CreateDefault(true);
        }

        /// <summary>The neck normal as used by the last solve (unit, parent space).</summary>
        public Vector3 Normal { get { return m_n; } }

        /// <summary>For diagnostics and tests: what the last fit did to the nominal posture. index 0-2 = knuckle-centre shift
        /// (metres) along the neck, out of the board and toward the palm side; 3-5 = palm turn (radians) about those axes.</summary>
        public double FitParameter(int index) { return index >= 0 && index < ParamCount ? m_xBest[index] : 0.0; }

        /// <summary>How far above its target a fingertip centre is placed (metres, parent space).</summary>
        public float TipLift(int finger)
        {
            return fingertipHover + dims.TipRadius(finger) * Mathf.Max(scale, 0.05f);
        }

        void UpdateNeckFrame()
        {
            Vector3 u = CapsuleHandMath.SafeNormalize(neckAxis, Vector3.right);
            Vector3 n = neckNormal - u * Vector3.Dot(neckNormal, u);
            if (!(n.sqrMagnitude > 1e-8f) || !CapsuleHandMath.IsFinite(n))
            {
                Vector3 alt = Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.forward;
                n = alt - u * Vector3.Dot(alt, u);
            }
            n = CapsuleHandMath.SafeNormalize(n, Vector3.up);
            m_u = u; m_n = n; m_t = Vector3.Cross(u, n) * (leftHand ? 1f : -1f);
        }

        Vector3 ToNeck(Vector3 parentPoint)
        {
            return new Vector3(Vector3.Dot(parentPoint, m_u), Vector3.Dot(parentPoint, m_n), Vector3.Dot(parentPoint, m_t));
        }

        Vector3 FromNeck(Vector3 neckPoint)
        {
            return m_u * neckPoint.x + m_n * neckPoint.y + m_t * neckPoint.z;
        }

        /// <summary>
        /// The pose of a fretting hand. targets[0..3] = fingers 1-4 (index..little) in parent space, where each fingertip pad
        /// should touch; a target whose x is NaN (or a missing one) means "finger not used": it hovers over the strings.
        /// The thumb rests behind the neck. The array is read, never kept.
        /// </summary>
        public void SolveFretting(Vector3[] targets, out HandPose pose)
        {
            UpdateNeckFrame();
            float s = Mathf.Max(scale, 0.05f);
            m_s = s;

            // ---- which fingers are used, and where their fingertip centres go (neck coordinates from here on) ----
            int count = targets == null ? 0 : targets.Length;
            int usedCount = 0;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < FingerCount; i++)
            {
                used[i] = false;
                reachError[i] = 0f;
                if (i < ThumbIndex && i < count && CapsuleHandMath.IsFinite(targets[i]))
                {
                    used[i] = true;
                    m_centre[i] = ToNeck(targets[i]) + new Vector3(0f, TipLift(i), 0f);
                    sum += m_centre[i];
                    usedCount++;
                }
            }
            Vector3 centroid;
            if (usedCount > 0)
            {
                centroid = sum / usedCount;
                restCentroid = FromNeck(centroid - new Vector3(0f, TipLift(1), 0f));    // remember where on the neck the hand is
            }
            else centroid = ToNeck(restCentroid) + new Vector3(0f, TipLift(1), 0f);

            // ---- the nominal fretting posture: the palm hangs beside the neck, leaning over it, knuckles at string height ----
            float phi = palmTiltDegrees * Mathf.Deg2Rad;
            float cp = Mathf.Cos(phi), sp = Mathf.Sin(phi);
            Vector3 axisZ = new Vector3(0f, cp, -sp);       // wrist -> knuckles: up out of the board, leaning over the neck
            Vector3 axisY = new Vector3(0f, sp, cp);        // back of the hand: away from the neck
            Vector3 axisX = new Vector3(-1f, 0f, 0f);       // = cross(axisY, axisZ): a left hand's thumb side points at the nut
            m_rotNominal = CapsuleHandMath.BasisToQuaternion(axisX, axisY, axisZ);

            Vector3 kUsed = Vector3.zero, kAll = Vector3.zero;
            for (int i = 0; i < ThumbIndex; i++)
            {
                kAll += m_left.chains[i].knuckle * 0.25f;
                if (used[i]) kUsed += m_left.chains[i].knuckle;
            }
            m_knuckleMean = usedCount > 0 ? kUsed / usedCount : kAll;
            m_knuckleNominal = centroid + new Vector3(0f, palmHeight * s, palmSideOffset * s);

            // the thumb: behind the neck, roughly opposite the first two fingers
            m_thumbTarget = new Vector3(centroid.x + thumbAlongNeck, -(neckDepth + TipLift(ThumbIndex)), thumbAcrossNeck);

            // ---- fit the palm so that every used fingertip reaches its target ----
            for (int j = 0; j < ParamCount; j++) m_xBest[j] = 0.0;
            m_xBest[4] = YawComfort;
            if (usedCount > 0 && fitIterations > 0) FitPalm();

            Vector3 palmPosition;
            Quaternion palmRotation;
            PoseFromParams(m_xBest, out palmPosition, out palmRotation, out axisX, out axisY, out axisZ);

            // ---- the fingers and the thumb (as a left hand): each pose stores the point its fingertip actually reaches ----
            pose = default(HandPose);
            FingerIkResult r;
            for (int i = 0; i < FingerCount; i++)
            {
                Vector3 goal;
                if (i == ThumbIndex) goal = m_thumbTarget;
                else if (used[i]) goal = m_centre[i];
                else
                {   // not used: hover over the strings, straight ahead of its own knuckle, a little above the fretting height
                    Vector3 k = m_left.chains[i].knuckle;
                    Vector3 knuckle = palmPosition + (axisX * k.x + axisY * k.y + axisZ * k.z) * s;
                    goal = new Vector3(knuckle.x, centroid.y + hoverRaise, centroid.z * 0.5f);
                }
                Vector3 local = ToPalm(goal - palmPosition, axisX, axisY, axisZ) / s;
                FingerIk.Solve(m_left.chains[i], local, out r);
                Vector3 tip = r.tip;
                if (!leftHand) tip.x = -tip.x;
                pose.SetTip(i, tip);
                if (i == ThumbIndex || used[i]) reachError[i] = r.error * s;
            }

            // ---- out of neck coordinates; a right hand is the mirror image, so its palm x axis flips ----
            pose.palmPosition = FromNeck(palmPosition);
            Vector3 outX = FromNeck(axisX), outY = FromNeck(axisY), outZ = FromNeck(axisZ);
            if (!leftHand) outX = -outX;
            pose.palmRotation = CapsuleHandMath.BasisToQuaternion(outX, outY, outZ);
        }

        // The palm pose (neck coordinates, left hand) for a parameter vector: the knuckle centre moves by (x0, x1, x2) and the
        // palm turns about the knuckle centre by the rotation vector (x3, x4, x5).
        void PoseFromParams(double[] x, out Vector3 palmPosition, out Quaternion rotation, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ)
        {
            Vector3 turn = new Vector3((float)x[3], (float)x[4], (float)x[5]);
            rotation = CapsuleHandMath.Normalized(CapsuleHandMath.FromRotationVector(turn) * m_rotNominal);
            axisX = rotation * Vector3.right;
            axisY = rotation * Vector3.up;
            axisZ = rotation * Vector3.forward;
            Vector3 knuckleCentre = m_knuckleNominal + new Vector3((float)x[0], (float)x[1], (float)x[2]);
            palmPosition = knuckleCentre - (axisX * m_knuckleMean.x + axisY * m_knuckleMean.y + axisZ * m_knuckleMean.z) * m_s;
        }

        // Residuals of a parameter vector (what the fit makes small) and their sum of squares.
        double Evaluate(double[] x, double[] res)
        {
            Vector3 p, aX, aY, aZ;
            Quaternion q;
            PoseFromParams(x, out p, out q, out aX, out aY, out aZ);
            FingerIkResult r;
            int k = 0;
            for (int i = 0; i < ThumbIndex; i++)
            {
                if (!used[i])
                {
                    for (int j = 0; j < PerFinger; j++) res[k++] = 0.0;
                    continue;
                }
                Vector3 local = ToPalm(m_centre[i] - p, aX, aY, aZ) / m_s;
                FingerIk.Solve(m_left.chains[i], local, out r);
                Vector3 e = (r.tip - local) * m_s;
                res[k++] = ReachWeight * e.x; res[k++] = ReachWeight * e.y; res[k++] = ReachWeight * e.z;
                res[k++] = SwingWeight * r.swing;
                res[k++] = McpWeight * (r.mcp - McpComfort);
                res[k++] = PipWeight * (r.pipFlexion - PipComfort);
            }
            {
                Vector3 local = ToPalm(m_thumbTarget - p, aX, aY, aZ) / m_s;
                FingerIk.Solve(m_left.chains[ThumbIndex], local, out r);
                Vector3 e = (r.tip - local) * m_s;
                res[k++] = ThumbWeight * e.x; res[k++] = ThumbWeight * e.y; res[k++] = ThumbWeight * e.z;
            }
            for (int j = 0; j < ParamCount; j++)
                res[k++] = (j < 3 ? PositionWeight : RotationWeight) * (j == 4 ? x[j] - YawComfort : x[j]);
            res[k++] = BackwardYawWeight * (x[4] < 0.0 ? x[4] : 0.0);
            {   // keep the knuckles out of the neck: their centre stays a finger's thickness outside its edge
                double side = m_knuckleNominal.z + x[2];
                double need = neckHalfWidth + 0.012 * m_s;
                res[k++] = ClearanceWeight * (side < need ? need - side : 0.0);
            }

            double cost = 0.0;
            for (int m = 0; m < ResidualCount; m++) cost += res[m] * res[m];
            return cost;
        }

        // The worst reach error (metres) among the used fingers, read from a residual vector.
        double WorstReach(double[] res)
        {
            double worst = 0.0;
            for (int i = 0; i < ThumbIndex; i++)
            {
                int k = i * PerFinger;
                double e2 = res[k] * res[k] + res[k + 1] * res[k + 1] + res[k + 2] * res[k + 2];
                if (e2 > worst) worst = e2;
            }
            return System.Math.Sqrt(worst) / ReachWeight;
        }

        // Fit the palm: start from the nominal posture; if a fingertip still misses, try the other starting points and keep
        // the fit with the smallest worst miss (then the smallest cost). Leaves the winner in m_xBest.
        void FitPalm()
        {
            for (int j = 0; j < ParamCount; j++) m_x[j] = m_xBest[j];
            double bestCost = Minimise();
            double bestWorst = WorstReach(m_res);
            for (int j = 0; j < ParamCount; j++) m_xBest[j] = m_x[j];

            for (int s = 0; s < Restarts.Length && bestWorst > FitTolerance; s++)
            {
                for (int j = 0; j < ParamCount; j++) m_x[j] = Restarts[s][j];
                double cost = Minimise();
                double worst = WorstReach(m_res);
                bool better = worst < bestWorst - 1e-5 || (worst <= bestWorst + 1e-5 && cost < bestCost);
                if (!better) continue;
                bestCost = cost; bestWorst = worst;
                for (int j = 0; j < ParamCount; j++) m_xBest[j] = m_x[j];
            }
        }

        // Levenberg-Marquardt from m_x; on return m_x holds the best parameters found and m_res their residuals.
        double Minimise()
        {
            double cost = Evaluate(m_x, m_res);
            double lambda = 1e-2;
            for (int it = 0; it < fitIterations; it++)
            {
                // Jacobian by forward differences
                for (int j = 0; j < ParamCount; j++)
                {
                    double h = j < 3 ? 2e-4 : 2e-3;
                    for (int q = 0; q < ParamCount; q++) m_xTry[q] = m_x[q];
                    m_xTry[j] += h;
                    Evaluate(m_xTry, m_resTry);
                    for (int m = 0; m < ResidualCount; m++) m_jac[m * ParamCount + j] = (m_resTry[m] - m_res[m]) / h;
                }
                // normal equations: A = J^T J, g = J^T r
                for (int a = 0; a < ParamCount; a++)
                {
                    double ga = 0.0;
                    for (int m = 0; m < ResidualCount; m++) ga += m_jac[m * ParamCount + a] * m_res[m];
                    m_g[a] = ga;
                    for (int b = a; b < ParamCount; b++)
                    {
                        double sum = 0.0;
                        for (int m = 0; m < ResidualCount; m++) sum += m_jac[m * ParamCount + a] * m_jac[m * ParamCount + b];
                        m_a[a * ParamCount + b] = sum;
                        m_a[b * ParamCount + a] = sum;
                    }
                }

                bool improved = false;
                for (int attempt = 0; attempt < 4 && !improved; attempt++)
                {
                    if (SolveDamped(lambda))
                    {
                        for (int j = 0; j < ParamCount; j++)
                        {
                            double v = m_x[j] + m_d[j];
                            m_xTry[j] = v < ParamMin[j] ? ParamMin[j] : (v > ParamMax[j] ? ParamMax[j] : v);
                        }
                        double tryCost = Evaluate(m_xTry, m_resTry);
                        if (tryCost < cost)
                        {
                            for (int j = 0; j < ParamCount; j++) m_x[j] = m_xTry[j];
                            for (int m = 0; m < ResidualCount; m++) m_res[m] = m_resTry[m];
                            cost = tryCost;
                            lambda = System.Math.Max(lambda * 0.3, 1e-7);
                            improved = true;
                            break;
                        }
                    }
                    lambda = System.Math.Min(lambda * 6.0, 1e7);
                }
                if (!improved) break;       // no downhill step left: converged
            }
            return cost;
        }

        // Solve (A + lambda * diag(A) + tiny) d = -g by Cholesky. False if the matrix is not positive definite.
        bool SolveDamped(double lambda)
        {
            const int n = ParamCount;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = m_a[i * n + j];
                    if (i == j) sum += lambda * m_a[i * n + i] + 1e-12;
                    for (int k = 0; k < j; k++) sum -= m_l[i * n + k] * m_l[j * n + k];
                    if (i == j)
                    {
                        if (!(sum > 1e-300)) return false;
                        m_l[i * n + i] = System.Math.Sqrt(sum);
                    }
                    else m_l[i * n + j] = sum / m_l[j * n + j];
                }
            }
            // forward substitution L y = -g, then back substitution L^T d = y
            for (int i = 0; i < n; i++)
            {
                double sum = -m_g[i];
                for (int k = 0; k < i; k++) sum -= m_l[i * n + k] * m_d[k];
                m_d[i] = sum / m_l[i * n + i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = m_d[i];
                for (int k = i + 1; k < n; k++) sum -= m_l[k * n + i] * m_d[k];
                m_d[i] = sum / m_l[i * n + i];
            }
            for (int i = 0; i < n; i++)
                if (double.IsNaN(m_d[i]) || double.IsInfinity(m_d[i])) return false;
            return true;
        }

        /// <summary>The pose of a free hand (the strumming hand, or a hand another script places): the wrist exactly where you
        /// say (parent space), every finger in its relaxed curl.</summary>
        public void SolveFree(Vector3 wristPosition, Quaternion wristRotation, out HandPose pose)
        {
            UpdateNeckFrame();
            for (int i = 0; i < FingerCount; i++) { used[i] = false; reachError[i] = 0f; }
            pose.palmPosition = CapsuleHandMath.IsFinite(wristPosition) ? wristPosition : Vector3.zero;
            pose.palmRotation = CapsuleHandMath.IsFinite(wristRotation) ? CapsuleHandMath.Normalized(wristRotation) : Quaternion.identity;
            pose.tip0 = dims.relaxedTip[0];
            pose.tip1 = dims.relaxedTip[1];
            pose.tip2 = dims.relaxedTip[2];
            pose.tip3 = dims.relaxedTip[3];
            pose.tip4 = dims.relaxedTip[4];
        }

        /// <summary>Inverse kinematics for all five fingers of a pose. results must hold FingerCount entries (palm space, scale 1).</summary>
        public void SolveFingers(in HandPose pose, FingerIkResult[] results)
        {
            for (int i = 0; i < FingerCount; i++) FingerIk.Solve(dims.chains[i], pose.GetTip(i), out results[i]);
        }

        /// <summary>A palm-space point (scale 1) of a pose, in parent space.</summary>
        public Vector3 PalmToParent(in HandPose pose, Vector3 palmPoint)
        {
            return pose.palmPosition + pose.palmRotation * (palmPoint * Mathf.Max(scale, 0.05f));
        }

        /// <summary>Where a fingertip pad touches (parent space) when its fingertip centre is at tipCentre (palm space): the
        /// point straight below the centre along the neck normal, with the hover taken out. Equals the target when it is reached.</summary>
        public Vector3 PadPoint(in HandPose pose, int finger, Vector3 tipCentre)
        {
            return PalmToParent(pose, tipCentre) - m_n * TipLift(finger);
        }

        static Vector3 ToPalm(Vector3 offset, Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            return new Vector3(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY), Vector3.Dot(offset, axisZ));
        }
    }
}
