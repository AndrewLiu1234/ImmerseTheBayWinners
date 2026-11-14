// FingerIk: analytic inverse kinematics for ONE finger (three phalanges), in the hand's local space.
// Plain C#: no MonoBehaviour, no Unity object, no static state, no allocation (value types only), deterministic.
//
// THE MODEL
//   * The finger hangs off a knuckle (its first joint: MCP for a finger, CMC for the thumb).
//   * "forward" is the direction of the finger when it is straight; "curl" is the direction it curls toward (the palm side).
//     forward + curl are the CURL PLANE; side = cross(curl, forward) completes the frame.
//   * The first joint can also SWING sideways (abduction): a rotation about "curl" that carries "forward" toward "side".
//   * All three joints then flex inside the swung plane. Angles are measured from the swung forward direction toward "curl":
//       mcp  = flexion of joint 1, pip = flexion of joint 2, dip = flexion of joint 3.
//   * Joint 3 follows joint 2: dip = dipCoupling * pip (the textbook ~2/3 tendon coupling). That removes the redundancy of a
//     3-link chain, so every fingertip position has exactly one pose and the result is smooth and natural.
//
// WHY IT IS CLOSED FORM
//   With dip = k * pip the fingertip, expressed in the swung plane, is  tip = Rot(mcp) * w(pip)  where
//       w(pip) = ( l1 + l2 cos(pip) + l3 cos((1+k) pip) ,  l2 sin(pip) + l3 sin((1+k) pip) ).
//   |w| does not depend on mcp, and it decreases monotonically for 0 <= pip <= pi/(1+k) (every derivative term is a negative
//   sine). So: reach |target| -> pip (one monotone scalar inversion, a fixed 36-step bisection, no iteration tolerance to
//   tune), then direction -> mcp = atan2(target) - atan2(w). The sideways offset of the target gives the swing: tan(swing) =
//   (offset along side) / (offset along forward). Every step is deterministic and takes a fixed amount of work.
//
// UNREACHABLE TARGETS
//   Targets beyond the reach (straight finger), inside the tightest curl, past the swing limit or past the mcp limits are
//   clamped to the nearest pose the limits allow (the returned tip is then not the target; see FingerIkResult.error).
//   The reach clamp is exact (radial projection). If the mcp limit is what stops the finger, mcp is pinned at the limit and
//   pip slides along its range to the closest point (coarse scan + golden section). Never NaN: non-finite input returns a
//   neutral pose with error = float.MaxValue.
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>Joint limits for one finger chain. Angles in radians; flexion is positive toward the curl direction.</summary>
    public struct FingerIkLimits
    {
        public float mcpMin;        // joint 1 flexion lower limit (negative = hyperextension)
        public float mcpMax;        // joint 1 flexion upper limit
        public float pipMax;        // joint 2 flexion upper limit (lower limit is 0: fingers do not bend backwards there)
        public float dipCoupling;   // joint 3 flexion = dipCoupling * joint 2 flexion
        public float swingMax;      // |sideways swing| limit of joint 1

        /// <summary>Human finger (index to little): about -20..90 deg at the knuckle, 100 deg at the middle joint, and a
        /// sideways swing of 28 deg (a little more than a real hand, so that wide chord shapes stay reachable).</summary>
        public static FingerIkLimits Finger
        {
            get
            {
                FingerIkLimits l;
                l.mcpMin = -0.35f; l.mcpMax = 1.5708f; l.pipMax = 1.75f; l.dipCoupling = 0.667f; l.swingMax = 0.50f;
                return l;
            }
        }

        /// <summary>Thumb: a much freer first joint (the saddle joint at the wrist), a shorter bend at the middle joint.</summary>
        public static FingerIkLimits Thumb
        {
            get
            {
                FingerIkLimits l;
                l.mcpMin = -0.60f; l.mcpMax = 1.70f; l.pipMax = 1.20f; l.dipCoupling = 0.80f; l.swingMax = 1.10f;
                return l;
            }
        }
    }

    /// <summary>One finger: where it hangs from, its curl plane, its bone lengths (metres) and its joint limits.</summary>
    public struct FingerIkChain
    {
        public Vector3 knuckle;     // first joint position, hand-local
        public Vector3 forward;     // direction of the straight, un-swung finger (need not be unit)
        public Vector3 curl;        // direction the finger curls toward; made perpendicular to forward (need not be unit)
        public float length1;       // knuckle -> second joint
        public float length2;       // second joint -> third joint
        public float length3;       // third joint -> fingertip centre
        public FingerIkLimits limits;
    }

    /// <summary>Joint positions (hand-local) and angles (radians) of a solved finger.</summary>
    public struct FingerIkResult
    {
        public Vector3 knuckle;     // joint 1
        public Vector3 pip;         // joint 2
        public Vector3 dip;         // joint 3
        public Vector3 tip;         // fingertip centre (end of the last bone)
        public float swing;         // sideways swing at joint 1, positive toward "side" = cross(curl, forward)
        public float mcp;           // joint 1 flexion
        public float pipFlexion;    // joint 2 flexion
        public float dipFlexion;    // joint 3 flexion
        public float error;         // |tip - target| in metres (float.MaxValue when the input was not finite)
        public bool reachable;      // true when the tip landed on the target (error <= FingerIk.ReachTolerance)
    }

    public static class FingerIk
    {
        /// <summary>A pose counts as reaching its target when the tip is within 0.1 mm.</summary>
        public const float ReachTolerance = 1e-4f;

        const double Pi = Math.PI;
        const double MinBone = 1e-4;

        /// <summary>Length of the fully straight finger: the farthest a target can be from the knuckle.</summary>
        public static float MaxReach(in FingerIkChain chain)
        {
            return Mathf.Max(chain.length1, 0f) + Mathf.Max(chain.length2, 0f) + Mathf.Max(chain.length3, 0f);
        }

        /// <summary>Forward kinematics: the pose for given joint angles (dip follows pip by the coupling). Angles are not clamped.</summary>
        public static void Pose(in FingerIkChain chain, float swing, float mcp, float pip, out FingerIkResult result)
        {
            Prepared p;
            if (!Prepare(chain, out p)) { Invalid(chain, out result); return; }
            Forward(p, swing, mcp, pip, out result);
            result.error = 0f;
            result.reachable = true;
        }

        /// <summary>Inverse kinematics: the pose whose fingertip is at <paramref name="target"/>, or the nearest reachable pose.</summary>
        public static void Solve(in FingerIkChain chain, Vector3 target, out FingerIkResult result)
        {
            Prepared p;
            if (!Prepare(chain, out p) || !Finite(target)) { Invalid(chain, out result); return; }

            Vector3 d = target - p.K;
            double a = Dot(d, p.f);     // along the straight finger
            double b = Dot(d, p.c);     // toward the palm side (the curl direction)
            double s = Dot(d, p.g);     // sideways

            // Swing: rotate the curl plane about "curl" until it contains the target. The plane also contains the opposite
            // half-line, so keep the swing in [-90, 90] deg; a target behind the knuckle then simply has x < 0 in the plane.
            double psi = 0.0;
            if (a * a + s * s > 1e-12)
            {
                psi = Math.Atan2(s, a);
                if (psi > Pi * 0.5) psi -= Pi;
                else if (psi < -Pi * 0.5) psi += Pi;
            }

            if (Math.Abs(psi) <= p.S)
            {
                SolveAtSwing(p, a, b, s, psi, target, out result);
            }
            else
            {   // The swing limit stops the finger. Which side is closer depends on the pose (a finger curled behind the
                // knuckle swings the other way round), so try both limits and keep the nearer pose.
                FingerIkResult plus, minus;
                SolveAtSwing(p, a, b, s, p.S, target, out plus);
                SolveAtSwing(p, a, b, s, -p.S, target, out minus);
                result = plus.error <= minus.error ? plus : minus;
            }
            result.reachable = result.error <= ReachTolerance;
        }

        static void SolveAtSwing(in Prepared p, double a, double b, double s, double psi, Vector3 target, out FingerIkResult r)
        {
            double x = a * Math.Cos(psi) + s * Math.Sin(psi);   // the target seen in the swung plane
            double th1, th2;
            PlanarSolve(p, x, b, out th1, out th2);
            Forward(p, psi, th1, th2, out r);
            r.error = (r.tip - target).magnitude;
        }

        // ---------------------------------------------------------------------------------------------------------------

        struct Prepared
        {
            public Vector3 K, f, c, g;          // knuckle and the orthonormal frame (forward, curl, side)
            public double l1, l2, l3;
            public double k, A, B, M, S;        // coupling, mcp min/max, pip max, swing max
        }

        static bool Prepare(in FingerIkChain ch, out Prepared p)
        {
            p = default(Prepared);
            if (!Finite(ch.knuckle) || !Finite(ch.forward) || !Finite(ch.curl)) return false;
            if (!Finite(ch.length1) || !Finite(ch.length2) || !Finite(ch.length3)) return false;

            Vector3 f = ch.forward;
            float fm = f.magnitude;
            if (fm < 1e-6f) f = Vector3.forward; else f /= fm;

            Vector3 c = ch.curl - f * Vector3.Dot(ch.curl, f);
            float cm = c.magnitude;
            if (cm < 1e-6f)
            {   // curl missing or parallel to forward: any perpendicular will do
                Vector3 axis = Mathf.Abs(f.x) < 0.9f ? Vector3.right : Vector3.up;
                c = axis - f * Vector3.Dot(axis, f);
                cm = c.magnitude;
            }
            c /= cm;

            p.K = ch.knuckle;
            p.f = f;
            p.c = c;
            p.g = Vector3.Cross(c, f);
            p.l1 = Math.Max(ch.length1, MinBone);
            p.l2 = Math.Max(ch.length2, MinBone);
            p.l3 = Math.Max(ch.length3, MinBone);

            FingerIkLimits lim = ch.limits;
            if (!Finite(lim.mcpMin) || !Finite(lim.mcpMax) || !Finite(lim.pipMax) ||
                !Finite(lim.dipCoupling) || !Finite(lim.swingMax)) lim = FingerIkLimits.Finger;
            p.k = Math.Max(0.0, Math.Min(2.0, lim.dipCoupling));
            p.A = Math.Min(lim.mcpMin, lim.mcpMax);
            p.B = Math.Max(lim.mcpMin, lim.mcpMax);
            p.M = Math.Max(0.1, Math.Min(lim.pipMax, Pi / (1.0 + p.k) - 0.02));   // keep |w| strictly monotone
            p.S = Math.Max(0.0, Math.Min(lim.swingMax, Pi * 0.5));
            return true;
        }

        static void Invalid(in FingerIkChain ch, out FingerIkResult r)
        {
            r = default(FingerIkResult);
            Vector3 k = Finite(ch.knuckle) ? ch.knuckle : Vector3.zero;
            r.knuckle = k; r.pip = k; r.dip = k; r.tip = k;
            r.error = float.MaxValue;
            r.reachable = false;
        }

        // The fingertip in the swung plane, with the first joint at 0 flexion: (wx, wy) as a function of the pip flexion.
        static void W(in Prepared p, double th2, out double wx, out double wy)
        {
            double t3 = (1.0 + p.k) * th2;
            wx = p.l1 + p.l2 * Math.Cos(th2) + p.l3 * Math.Cos(t3);
            wy = p.l2 * Math.Sin(th2) + p.l3 * Math.Sin(t3);
        }

        static void PlanarSolve(in Prepared p, double x, double y, out double th1, out double th2)
        {
            double wx, wy;
            double rmax = p.l1 + p.l2 + p.l3;
            W(p, p.M, out wx, out wy);
            double rmin = Math.Sqrt(wx * wx + wy * wy);
            double r = Math.Sqrt(x * x + y * y);

            if (r >= rmax) th2 = 0.0;                   // beyond the reach: straight finger pointing at the target
            else if (r <= rmin) th2 = p.M;              // inside the tightest curl: fully curled
            else
            {
                double lo = 0.0, hi = p.M;              // |w| is above r at lo and below r at hi
                double r2 = r * r;
                for (int i = 0; i < 36; i++)            // 36 halvings of at most 1.75 rad: far below float resolution
                {
                    double mid = 0.5 * (lo + hi);
                    W(p, mid, out wx, out wy);
                    if (wx * wx + wy * wy > r2) lo = mid; else hi = mid;
                }
                th2 = 0.5 * (lo + hi);
            }

            W(p, th2, out wx, out wy);
            double argw = Math.Atan2(wy, wx);
            double alpha = r > 1e-9 ? Math.Atan2(y, x) : argw + 0.5 * (p.A + p.B);   // target at the knuckle: aim for mid-range
            th1 = WrapPi(alpha - argw);
            if (th1 >= p.A && th1 <= p.B) return;

            // The knuckle limit stops the finger: pin joint 1 at one of its limits and slide joint 2 to the closest point.
            // Which limit is closer depends on the bone lengths (not just the angle), so try both and keep the better one.
            double t2A = SlidePip(p, p.A, x, y), t2B = SlidePip(p, p.B, x, y);
            if (TipDist2(p, p.A, t2A, x, y) <= TipDist2(p, p.B, t2B, x, y)) { th1 = p.A; th2 = t2A; }
            else { th1 = p.B; th2 = t2B; }
        }

        // Squared distance from the fingertip of pose (th1, th2) to the planar target (x, y).
        static double TipDist2(in Prepared p, double th1, double th2, double x, double y)
        {
            double wx, wy;
            W(p, th2, out wx, out wy);
            double c = Math.Cos(th1), s = Math.Sin(th1);
            double tx = wx * c - wy * s, ty = wx * s + wy * c;
            return (tx - x) * (tx - x) + (ty - y) * (ty - y);
        }

        static double Dist2(in Prepared p, double th2, double qx, double qy)
        {
            double wx, wy;
            W(p, th2, out wx, out wy);
            return (wx - qx) * (wx - qx) + (wy - qy) * (wy - qy);
        }

        // With joint 1 fixed, find the pip flexion in [0, M] whose fingertip is closest to the target (x, y).
        static double SlidePip(in Prepared p, double th1, double x, double y)
        {
            double c = Math.Cos(th1), s = Math.Sin(th1);
            double qx = x * c + y * s, qy = -x * s + y * c;     // the target as seen from the turned first link

            const int N = 32;
            int bi = 0;
            double bd = double.MaxValue;
            for (int i = 0; i <= N; i++)
            {
                double d = Dist2(p, p.M * i / N, qx, qy);
                if (d < bd) { bd = d; bi = i; }
            }
            double lo = p.M * Math.Max(bi - 1, 0) / N, hi = p.M * Math.Min(bi + 1, N) / N;

            const double g = 0.6180339887498949;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo);
            double f1 = Dist2(p, x1, qx, qy), f2 = Dist2(p, x2, qx, qy);
            for (int i = 0; i < 50; i++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = Dist2(p, x1, qx, qy); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = Dist2(p, x2, qx, qy); }
            }
            return 0.5 * (lo + hi);
        }

        static void Forward(in Prepared p, double swing, double th1, double th2, out FingerIkResult r)
        {
            r = default(FingerIkResult);
            double th3 = p.k * th2;
            Vector3 fs = p.f * (float)Math.Cos(swing) + p.g * (float)Math.Sin(swing);    // the swung forward direction
            double a1 = th1, a2 = th1 + th2, a3 = th1 + th2 + th3;
            Vector3 d1 = fs * (float)Math.Cos(a1) + p.c * (float)Math.Sin(a1);
            Vector3 d2 = fs * (float)Math.Cos(a2) + p.c * (float)Math.Sin(a2);
            Vector3 d3 = fs * (float)Math.Cos(a3) + p.c * (float)Math.Sin(a3);
            r.knuckle = p.K;
            r.pip = p.K + d1 * (float)p.l1;
            r.dip = r.pip + d2 * (float)p.l2;
            r.tip = r.dip + d3 * (float)p.l3;
            r.swing = (float)swing;
            r.mcp = (float)th1;
            r.pipFlexion = (float)th2;
            r.dipFlexion = (float)th3;
        }

        static double WrapPi(double a)
        {
            a -= 2.0 * Pi * Math.Floor((a + Pi) / (2.0 * Pi));
            return a;   // (-pi, pi]
        }

        static double Dot(Vector3 u, Vector3 v)
        {
            return (double)u.x * v.x + (double)u.y * v.y + (double)u.z * v.z;
        }

        // Written out (not float.IsFinite) so it compiles against every .NET profile Unity 2021.3 offers.
        static bool Finite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static bool Finite(Vector3 v)
        {
            return Finite(v.x) && Finite(v.y) && Finite(v.z);
        }
    }
}
