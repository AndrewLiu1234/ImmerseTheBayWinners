using System;
using System.Numerics;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Geometry
{
    public sealed class NeckSolverConfig
    {
        /// <summary>Nearest plausible nut distance from the head pivot, mm (seated player, MASTER_PLAN §20: 30 cm).</summary>
        public double MinNutDistanceMm { get; }
        /// <summary>Farthest plausible nut distance, mm (§20: 90 cm).</summary>
        public double MaxNutDistanceMm { get; }
        /// <summary>1D search step over the nut distance, mm. 5 mm keeps the coarse fit inside LM's basin; ~120 steps.</summary>
        public double SearchStepMm { get; }
        /// <summary>Levenberg–Marquardt iteration cap. Converges in &lt;15 on synthetic rays; the cap bounds the worst case.</summary>
        public int MaxIterations { get; }
        /// <summary>Fits worse than this RMS (mm) aren't the neck at all (wrong dwell order, far object) → Success = false.
        /// The G1 quality gate (≤15 mm) is the caller's check on <see cref="NeckSolveResult.ResidualRmsMm"/>.</summary>
        public double MaxResidualMm { get; }

        public NeckSolverConfig(double minNutDistanceMm, double maxNutDistanceMm, double searchStepMm, int maxIterations, double maxResidualMm)
        {
            if (!(minNutDistanceMm > 0) || !(maxNutDistanceMm > minNutDistanceMm)) throw new ArgumentOutOfRangeException(nameof(maxNutDistanceMm));
            if (!(searchStepMm > 0)) throw new ArgumentOutOfRangeException(nameof(searchStepMm));
            if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
            if (!(maxResidualMm > 0)) throw new ArgumentOutOfRangeException(nameof(maxResidualMm));
            MinNutDistanceMm = minNutDistanceMm; MaxNutDistanceMm = maxNutDistanceMm; SearchStepMm = searchStepMm;
            MaxIterations = maxIterations; MaxResidualMm = maxResidualMm;
        }

        public static readonly NeckSolverConfig Default = new NeckSolverConfig(300.0, 900.0, 5.0, 100, 50.0);
    }

    /// <summary>
    /// Camera-free neck alignment (MASTER_PLAN §20). Four dwell rays from the head pivot (nut low E, nut high E, fret 5
    /// low E, fret 12 low E) + the known fretboard geometry → the rigid neck pose that best fits them.
    /// Method: 1D search over the nut distance along the first ray (sphere-intersecting the other rays at the known
    /// distances) for a coarse pose, then Levenberg–Marquardt on all 6 DoF minimising point-to-ray distances.
    /// Pure, deterministic, call once per alignment (allocates a little; not per frame).
    /// </summary>
    public sealed class NeckSolver
    {
        public const int TargetCount = 4;

        readonly NeckSolverConfig _cfg;
        readonly V3[] _model = new V3[TargetCount];
        readonly NeckPoint[] _modelPoints = new NeckPoint[TargetCount];

        public NeckSolver(FretboardGeometry geometry, NeckSolverConfig config)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            Set(AlignTarget.NutLowE, 6, 0, geometry);
            Set(AlignTarget.NutHighE, 1, 0, geometry);
            Set(AlignTarget.Fret5LowE, 6, 5, geometry);
            Set(AlignTarget.Fret12LowE, 6, 12, geometry);
        }

        void Set(AlignTarget t, int stringNumber, int fret, FretboardGeometry g)
        {
            double along = g.FretDistanceMm(fret);
            var p = new NeckPoint(along, g.StringAcrossMm(stringNumber, along));
            _modelPoints[(int)t] = p;
            _model[(int)t] = new V3(p.AlongMm, p.AcrossMm, 0);
        }

        /// <summary>Neck-local position of a dwell target (on the fret wire / nut, on the string).</summary>
        public NeckPoint ModelPoint(AlignTarget t) => _modelPoints[(int)t];

        /// <param name="rays">indexed by <see cref="AlignTarget"/>; exactly 4.</param>
        public NeckSolveResult Solve(GazeRay[] rays)
        {
            if (rays == null || rays.Length != TargetCount) return new NeckSolveResult("need exactly 4 gaze rays");
            var o = new V3[TargetCount];
            var d = new V3[TargetCount];
            for (int i = 0; i < TargetCount; i++)
            {
                o[i] = V3.From(rays[i].Origin);
                var dir = V3.From(rays[i].Direction);
                double len = dir.Length;
                if (!o[i].IsFinite || !dir.IsFinite || !(len > 1e-9)) return new NeckSolveResult($"ray {(AlignTarget)i} has no valid direction");
                d[i] = dir / len;
            }

            if (!CoarseSearch(o, d, out var R, out var T)) return new NeckSolveResult("rays are degenerate or the neck is outside 30–90 cm");
            Refine(o, d, ref R, ref T);

            var per = new double[TargetCount];
            double sum = 0, max = 0;
            for (int i = 0; i < TargetCount; i++)
            {
                var p = R * _model[i] + T;
                var v = p - o[i];
                if (V3.Dot(v, d[i]) <= 0) return new NeckSolveResult($"fitted {(AlignTarget)i} is behind the viewer");
                per[i] = PerpDistance(v, d[i]);
                sum += per[i] * per[i];
                max = Math.Max(max, per[i]);
            }
            double rms = Math.Sqrt(sum / TargetCount);
            double nutDist = (R * _model[0] + T - o[0]).Length;
            if (!(rms <= _cfg.MaxResidualMm)) return new NeckSolveResult($"best fit residual {rms:F0} mm is not the neck — redo the 4 glances");
            if (nutDist < _cfg.MinNutDistanceMm * 0.9 || nutDist > _cfg.MaxNutDistanceMm * 1.1)
                return new NeckSolveResult($"fitted nut at {nutDist:F0} mm is outside the seated range");
            return new NeckSolveResult(new NeckPose(T.ToVector3(), R.ToQuaternion()), per, rms, max);
        }

        /// <summary>
        /// One-dwell re-anchor (§20): rotate the whole neck about the ray origin (head pivot) so the nut-low-E point lies on
        /// <paramref name="nutLowERay"/>, keeping its distance. Fixes yaw drift and small guitar shifts. Invalid ray → pose unchanged.
        /// </summary>
        public NeckPose Reanchor(NeckPose pose, GazeRay nutLowERay)
        {
            if (pose == null) throw new ArgumentNullException(nameof(pose));
            var pivot = nutLowERay.Origin;
            var dir = nutLowERay.Direction;
            float dl = dir.Length();
            if (!(dl > 1e-6f) || float.IsNaN(dl) || float.IsInfinity(dl)) return pose;
            var nut = pose.ToWorld(_modelPoints[(int)AlignTarget.NutLowE]) - pivot;
            float nl = nut.Length();
            if (!(nl > 1e-6f)) return pose;
            var q = FromTo(nut / nl, dir / dl);
            return new NeckPose(pivot + Vector3.Transform(pose.Position - pivot, q), Quaternion.Normalize(q * pose.Rotation));
        }

        static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            float c = Vector3.Dot(a, b);
            var axis = Vector3.Cross(a, b);
            if (c < -0.999999f)
            {
                axis = Vector3.Cross(a, Vector3.UnitX);
                if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(a, Vector3.UnitY);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
            }
            return Quaternion.Normalize(new Quaternion(axis, 1f + c));
        }

        // ---------- coarse 1D search ----------

        bool CoarseSearch(V3[] o, V3[] d, out M3 bestR, out V3 bestT)
        {
            bestR = M3.Identity; bestT = default;
            double bestCost = double.MaxValue;
            var a = _model[3] - _model[0]; // nut → fret 12 along low E (neck-local)
            var b = _model[1] - _model[0]; // nut low E → nut high E
            double la = a.Length, lb = b.Length;
            var roots4 = new double[2];
            var roots2 = new double[2];

            for (double t1 = _cfg.MinNutDistanceMm; t1 <= _cfg.MaxNutDistanceMm + 1e-9; t1 += _cfg.SearchStepMm)
            {
                var p1 = o[0] + d[0] * t1;
                int n4 = SphereHits(o[3], d[3], p1, la, roots4);
                int n2 = SphereHits(o[1], d[1], p1, lb, roots2);
                for (int i = 0; i < n4; i++)
                for (int j = 0; j < n2; j++)
                {
                    var p4 = o[3] + d[3] * roots4[i];
                    var p2 = o[1] + d[1] * roots2[j];
                    if (!TwoVectorFrame(a, b, p4 - p1, p2 - p1, out var R)) continue;
                    var T = p1 - R * _model[0];
                    double cost = Cost(o, d, R, T);
                    if (cost < bestCost) { bestCost = cost; bestR = R; bestT = T; }
                }
            }
            return bestCost < double.MaxValue;
        }

        // Positive t with |o + t·d − c| = r (d unit). Returns the count written to roots.
        static int SphereHits(V3 o, V3 d, V3 c, double r, double[] roots)
        {
            var oc = o - c;
            double bb = V3.Dot(d, oc);
            double disc = bb * bb - (oc.LengthSq - r * r);
            if (disc < 0) return 0;
            double s = Math.Sqrt(disc);
            int n = 0;
            if (-bb - s > 0) roots[n++] = -bb - s;
            if (s > 0 && -bb + s > 0) roots[n++] = -bb + s;
            return n;
        }

        // Rotation taking local (a, b) onto world (A, B) in the least-squares-ish two-vector sense (a exact, b's plane).
        static bool TwoVectorFrame(V3 a, V3 b, V3 A, V3 B, out M3 R)
        {
            R = M3.Identity;
            if (!Basis(a, b, out var e1, out var e2, out var e3)) return false;
            if (!Basis(A, B, out var E1, out var E2, out var E3)) return false;
            // R = [E1 E2 E3]·[e1 e2 e3]ᵀ
            R = M3.FromColumns(E1, E2, E3) * M3.FromColumns(e1, e2, e3).Transpose();
            return true;
        }

        static bool Basis(V3 a, V3 b, out V3 e1, out V3 e2, out V3 e3)
        {
            e1 = e2 = e3 = default;
            double la = a.Length;
            if (!(la > 1e-9)) return false;
            e1 = a / la;
            var perp = b - e1 * V3.Dot(b, e1);
            double lp = perp.Length;
            if (!(lp > 1e-3 * b.Length) || !(lp > 1e-9)) return false; // b (nearly) parallel to a → roll undetermined
            e2 = perp / lp;
            e3 = V3.Cross(e1, e2);
            return true;
        }

        double Cost(V3[] o, V3[] d, M3 R, V3 T)
        {
            double c = 0;
            for (int i = 0; i < TargetCount; i++)
            {
                var v = R * _model[i] + T - o[i];
                if (V3.Dot(v, d[i]) <= 0) return double.MaxValue;
                var e = v - d[i] * V3.Dot(v, d[i]);
                c += e.LengthSq;
            }
            return c;
        }

        static double PerpDistance(V3 v, V3 d) => (v - d * V3.Dot(v, d)).Length;

        // ---------- Levenberg–Marquardt on (T, ω) with R ← exp(ω)·R ----------

        void Refine(V3[] o, V3[] d, ref M3 R, ref V3 T)
        {
            double lambda = 1e-3;
            double cost = Cost(o, d, R, T);
            var JtJ = new double[6, 6];
            var Jte = new double[6];
            var J = new double[3, 6];
            var delta = new double[6];

            for (int iter = 0; iter < _cfg.MaxIterations; iter++)
            {
                Array.Clear(JtJ, 0, JtJ.Length);
                Array.Clear(Jte, 0, Jte.Length);
                for (int i = 0; i < TargetCount; i++)
                {
                    var q = R * _model[i];
                    var v = q + T - o[i];
                    var P = M3.Identity - M3.Outer(d[i], d[i]);
                    var e = P * v;
                    var Jw = P * M3.Skew(q) * -1.0; // ∂e/∂ω = −P·[q]×
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 3; c++) { J[r, c] = P[r, c]; J[r, c + 3] = Jw[r, c]; }
                    for (int a = 0; a < 6; a++)
                    {
                        Jte[a] += J[0, a] * e.X + J[1, a] * e.Y + J[2, a] * e.Z;
                        for (int b = 0; b < 6; b++) JtJ[a, b] += J[0, a] * J[0, b] + J[1, a] * J[1, b] + J[2, a] * J[2, b];
                    }
                }

                bool improved = false;
                for (int attempt = 0; attempt < 10 && !improved; attempt++)
                {
                    if (!SolveDamped(JtJ, Jte, lambda, delta)) { lambda *= 10; continue; }
                    var T2 = T + new V3(delta[0], delta[1], delta[2]);
                    var R2 = M3.Rodrigues(new V3(delta[3], delta[4], delta[5])) * R;
                    double c2 = Cost(o, d, R2, T2);
                    if (c2 < cost)
                    {
                        double gain = cost - c2;
                        R = R2.Orthonormalized(); T = T2; cost = c2; improved = true;
                        lambda = Math.Max(lambda * 0.1, 1e-12);
                        if (gain < 1e-12 * (1 + cost)) return; // converged
                    }
                    else lambda *= 10;
                }
                if (!improved) return;
            }
        }

        // (A + λ·diag(A))·x = −g by Gaussian elimination with partial pivoting.
        static bool SolveDamped(double[,] A, double[] g, double lambda, double[] x)
        {
            const int n = 6;
            var m = new double[n, n + 1];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++) m[r, c] = A[r, c];
                m[r, r] += lambda * Math.Max(A[r, r], 1e-9);
                m[r, n] = -g[r];
            }
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-15) return false;
                if (piv != col) for (int c = 0; c <= n; c++) { var t = m[col, c]; m[col, c] = m[piv, c]; m[piv, c] = t; }
                for (int r = col + 1; r < n; r++)
                {
                    double f = m[r, col] / m[col, col];
                    for (int c = col; c <= n; c++) m[r, c] -= f * m[col, c];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double s = m[r, n];
                for (int c = r + 1; c < n; c++) s -= m[r, c] * x[c];
                x[r] = s / m[r, r];
                if (double.IsNaN(x[r]) || double.IsInfinity(x[r])) return false;
            }
            return true;
        }
    }
}
