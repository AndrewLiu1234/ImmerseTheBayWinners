// GhostPoseDriver: puts an authored ghost-hand pose (Pose JSON v1) on an imported, skinned hand rig (the Blender FBX).
// GhostRigSolver (same file) holds the maths with no Transform in it, so it can be tested on its own.
//
//     var driver = new GhostPoseDriver();
//     driver.Bind(modelRoot);            // modelRoot's parent is the neck frame (FloatingNeck.Socket): metres, pose axes
//     driver.Apply(pose);                // or Blend(a, b, t), or MorphTo(next, 0.28f) + Tick(dt) every frame
//
// WHAT A POSE FILE GIVES (unity/Assets/StreamingAssets/poses/README.md sections 1 and 4; art/blender/export_poses.py):
//   H        the hand's model space: the Blender armature's own space with the axis rule (x, y, z) = (X, Z, Y), in mm.
//            bind[b] = bone b's head in H (bind.joints).
//   D[b]     bone b's total turn: D[b] = P[b] B[b]^-1, B[b] = its bind orientation in H, P[b] = its posed orientation in
//            the neck frame (export_poses.py read_pose: total[name] = posed @ rest.transposed()). Every vector v fixed to
//            bone b (a child joint, a skinned vertex) goes from v (bind, in H) to D[b] v (posed, in the neck frame).
//   q[b]     what the file stores, Unity axes: q[wrist] = D[wrist], q[b] = D[parent]^-1 D[b] (export_poses.py: rho =
//            total[parent]^T @ total[b], then (x, y, z, w) = (-x, -z, -y, w), which is C rho C with C the axis swap).
//            Hence D[b] = D[parent] q[b] (GhostPose.AccumulateRotations).
//   root     rootPositionMm = the wrist joint (head of 'wrist') in the neck frame.
//
// WHAT THE RIG HAS (read by Bind in the model root's own space M; the root's parent is the neck frame, 1 unit = 1 m):
//   W[x]     the rotation of transform x in M in the bind pose = the product of the localRotations below the root down to x.
//   L[b]     bone b's bind localRotation. T(b) = its actual parent transform: the canonical parent, a helper transform below
//            it, or (for the wrist) anything between the root and the wrist. W[b] = W[T(b)] L[b].
//   J[b]     bone b's origin in M in the bind pose.
//
// THE FIXED TURN (README section 4: an FBX import may add a fixed turn; "compare the rig's bone positions in its bind pose
// with bind.joints"): GhostRigSolver.Fit finds the similarity J[b] ~= s F bind[b] + t over the 16 joints (Horn's closed
// form). F is the rotation from H to M (Blender's FBX axis options and Unity's import flip; both are mirrors, so F is a
// proper rotation), s = rig units per mm. A joint farther than FitToleranceMm from the fit means the rig is not the one the
// poses were made on, or the FBX was not exported in its bind pose: the rig is refused instead of drawn wrongly.
//
// THE FORMULA. The root is turned by F^-1 and scaled by k = 0.001 / s, so a vector fixed to bone b, which is s F v in M,
// must end up as 0.001 D[b] v in the neck frame: bone b has to turn in M by F D[b] F^-1, i.e. W'[b] = F D[b] F^-1 W[b].
// A helper transform rides rigidly on its driven ancestor: W'[T(b)] = F D[parent] F^-1 W[T(b)] (D = identity above the
// wrist). Therefore
//     localRotation[b] = W'[T(b)]^-1 W'[b] = W[T(b)]^-1 F (D[parent]^-1 D[b]) F^-1 W[b]
//                      = A[b]^-1 q[b] A[b] L[b]        with A[b] = F^-1 W[T(b)], precomputed by Fit:
// the stored turn q[b], measured in the hand's model axes, re-expressed in the axes of the bone's parent transform and put
// in front of the bind localRotation. With F = identity and T(b) = the canonical parent this is the README's
// "localRotation[b] = Inverse(bindModel[parent]) * q[b] * bindModel[parent] * bindLocal[b]". Bone roll and the FBX's
// bone-axis options cancel out: only the rig's own W and L enter.
// The root: localRotation = F^-1, localScale = k, localPosition = 0.001 rootPositionMm - F^-1 (k J[wrist]), which puts the
// wrist joint on rootPositionMm. Joint b then lands on 0.001 (P[parent] + D[parent] (bind[b] - bind[parent])), the README's
// forward kinematics; Tests/EditMode/GhostPoseDriverTests.cs checks that against Unity's own transform hierarchy.
//
// RULES FOR THE RIG: the 16 bones are found by name below the root (exact name, or a name ending in "<separator><bone>"
// such as "ghost_hand_fretting:wrist" or "Armature|wrist"; separators : / | . _ - and space). Each must be a descendant of
// its canonical parent, the root must be above the wrist, the rig must be in its bind pose when Bind runs, and transforms
// between the bones (helpers) must not be animated by anything else. Scales below the root must be uniform and positive.
//
// APIS (Unity 2021.3 Scripting Reference, local copy Unity.app/Contents/Documentation/en/ScriptReference/<page>.html):
//   Quaternion-operator_multiply  "Combines rotations lhs and rhs. Rotating by the product lhs * rhs is the same as applying
//                                 the two rotations in sequence: lhs first and then rhs, relative to the reference frame
//                                 resulting from lhs rotation." (the standard Hamilton product; also rotates a Vector3)
//   Transform-localRotation       "The rotation of the transform relative to the transform rotation of the parent."
//   Transform.InverseTransformPoint / Transform.TransformPoint   world <-> local positions ("affected by scale")
//   Transform-worldToLocalMatrix / Transform-localToWorldMatrix, Matrix4x4.MultiplyPoint3x4
//   Transform.IsChildOf           "true if this transform is a child, deep child (child of a child) or identical to this transform"
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>
    /// The maths of <see cref="GhostPoseDriver"/> with no Transform: a rig's bind data in (SetBind), the fixed turn between
    /// the pose files' model space and the rig (Fit), and per frame the 16 local rotations plus the model root's placement
    /// (Solve). Plain C#. Solve allocates nothing; Fit allocates two small scratch arrays, so call it at set-up time.
    /// </summary>
    public sealed class GhostRigSolver
    {
        public const int BoneCount = GhostPose.BoneCount;
        /// <summary>A rig joint may be this far (pose millimetres) from the fitted bind joint; farther and the rig is refused.</summary>
        public const float FitToleranceMm = 1f;

        readonly Quaternion[] _bindLocal = new Quaternion[BoneCount];      // L[b]
        readonly Quaternion[] _parentInRoot = new Quaternion[BoneCount];   // W[T(b)]
        readonly Vector3[] _positionInRoot = new Vector3[BoneCount];       // J[b]
        readonly bool[] _set = new bool[BoneCount];
        readonly Quaternion[] _toParentAxes = new Quaternion[BoneCount];   // A[b] = F^-1 W[T(b)]
        readonly Vector3[] _fitFrom = new Vector3[BoneCount];
        readonly Vector3[] _fitTo = new Vector3[BoneCount];
        Quaternion _modelToRig = Quaternion.identity;                      // F
        float _rigUnitsPerMm = 0.001f;                                     // s
        Vector3 _rigOffset;                                                // t
        bool _fitted;

        /// <summary>True after a successful Fit (and no SetBind since).</summary>
        public bool IsFitted { get { return _fitted; } }
        /// <summary>Largest joint distance of the last Fit, in pose millimetres (infinity before a fit).</summary>
        public float FitErrorMm { get; private set; }
        /// <summary>Why the last Fit failed, or null.</summary>
        public string FitProblem { get; private set; }
        /// <summary>F: the rotation from the pose files' model space H to the rig root's space M.</summary>
        public Quaternion ModelToRig { get { return _modelToRig; } }
        /// <summary>s: rig units per pose millimetre.</summary>
        public float RigUnitsPerMm { get { return _rigUnitsPerMm; } }
        /// <summary>t: where the origin of H is in M, in rig units.</summary>
        public Vector3 RigOffset { get { return _rigOffset; } }

        public GhostRigSolver()
        {
            Reset();
        }

        /// <summary>Forgets the bind data and the fit.</summary>
        public void Reset()
        {
            for (int b = 0; b < BoneCount; b++)
            {
                _set[b] = false;
                _bindLocal[b] = Quaternion.identity;
                _parentInRoot[b] = Quaternion.identity;
                _toParentAxes[b] = Quaternion.identity;
                _positionInRoot[b] = Vector3.zero;
            }
            _fitted = false;
            _modelToRig = Quaternion.identity;
            _rigUnitsPerMm = 0.001f;
            _rigOffset = Vector3.zero;
            FitErrorMm = float.PositiveInfinity;
            FitProblem = null;
        }

        /// <summary>
        /// One bone's bind data: its bind localRotation L, the rotation W of its actual parent transform in the model root's
        /// space (identity when the parent is the root), and its origin J in the root's space. Clears the fit.
        /// </summary>
        public void SetBind(int bone, Quaternion bindLocal, Quaternion parentInRoot, Vector3 positionInRoot)
        {
            if (bone < 0 || bone >= BoneCount) return;
            _bindLocal[bone] = Normalized(bindLocal);
            _parentInRoot[bone] = Normalized(parentInRoot);
            _positionInRoot[bone] = positionInRoot;
            _set[bone] = true;
            _fitted = false;
        }

        /// <summary>True when all 16 bones have bind data.</summary>
        public bool IsComplete
        {
            get
            {
                for (int b = 0; b < BoneCount; b++) if (!_set[b]) return false;
                return true;
            }
        }

        /// <summary>The bind data of one bone, as given to SetBind (for checks and tests).</summary>
        public Vector3 GetBindPosition(int bone) { return bone >= 0 && bone < BoneCount ? _positionInRoot[bone] : Vector3.zero; }

        /// <summary>
        /// Finds the fixed turn between the pose files' model space and the rig from the reference pose's bind joints (every
        /// pose of one hand carries the same ones). False, with FitProblem set, when the data is missing or the rig's joints
        /// do not match within FitToleranceMm.
        /// </summary>
        public bool Fit(GhostPose reference)
        {
            if (reference == null || !reference.HasBind)
            {
                _fitted = false;
                FitErrorMm = float.PositiveInfinity;
                FitProblem = "the reference pose has no bind block (re-export it with art/blender/export_poses.py 1.1.0 or newer)";
                return false;
            }
            for (int b = 0; b < BoneCount; b++) _fitFrom[b] = reference.GetBindPositionMm(b);
            return FitCore();
        }

        /// <summary>As Fit(GhostPose), from the 16 bind joints (bind.joints, mm, canonical bone order) directly.</summary>
        public bool Fit(Vector3[] bindJointsMm)
        {
            if (bindJointsMm == null || bindJointsMm.Length < BoneCount)
            {
                _fitted = false;
                FitErrorMm = float.PositiveInfinity;
                FitProblem = "16 bind joints are needed";
                return false;
            }
            for (int b = 0; b < BoneCount; b++) _fitFrom[b] = bindJointsMm[b];
            return FitCore();
        }

        bool FitCore()
        {
            _fitted = false;
            FitProblem = null;
            FitErrorMm = float.PositiveInfinity;
            if (!IsComplete)
            {
                FitProblem = "the rig's bind data is incomplete";
                return false;
            }
            for (int b = 0; b < BoneCount; b++) _fitTo[b] = _positionInRoot[b];
            Quaternion f;
            float s, worst;
            Vector3 t;
            if (!FitSimilarity(_fitFrom, _fitTo, BoneCount, out f, out s, out t, out worst))
            {
                FitProblem = "the rig's joints are degenerate (all in one place or on one line), so no turn can be fitted";
                return false;
            }
            float worstMm = worst / s;
            FitErrorMm = worstMm;
            if (!(worstMm <= FitToleranceMm))
            {
                FitProblem = "the rig's bind joints differ from the pose files' bind joints by up to " + worstMm.ToString("0.###", CultureInfo.InvariantCulture)
                    + " mm (limit " + FitToleranceMm.ToString("0.###", CultureInfo.InvariantCulture)
                    + " mm): a different rig, or an FBX that was not exported in its bind pose";
                return false;
            }
            _modelToRig = f;
            _rigUnitsPerMm = s;
            _rigOffset = t;
            Quaternion fInverse = Conjugate(f);
            for (int b = 0; b < BoneCount; b++) _toParentAxes[b] = Normalized(fInverse * _parentInRoot[b]);
            _fitted = true;
            return true;
        }

        /// <summary>A point of the pose files' model space (mm) in the rig root's space (rig units), using the fit.</summary>
        public Vector3 PoseToRig(Vector3 modelMm)
        {
            return (_modelToRig * modelMm) * _rigUnitsPerMm + _rigOffset;
        }

        /// <summary>
        /// The rig's local rotations for 16 parent-relative pose rotations (a pose's own, or a GhostPose.Blend result) and
        /// the model root's placement in its parent (the neck frame) for a wrist at rootPositionMm. Allocation-free.
        /// False (nothing written) before a successful Fit or with buffers shorter than 16.
        /// </summary>
        public bool Solve(Quaternion[] rotations, Vector3 rootPositionMm, Quaternion[] localRotations,
            out Vector3 rootLocalPosition, out Quaternion rootLocalRotation, out float rootLocalScale)
        {
            rootLocalPosition = Vector3.zero;
            rootLocalRotation = Quaternion.identity;
            rootLocalScale = 1f;
            if (!_fitted || rotations == null || localRotations == null || rotations.Length < BoneCount || localRotations.Length < BoneCount)
                return false;
            for (int b = 0; b < BoneCount; b++)
            {
                Quaternion a = _toParentAxes[b];
                localRotations[b] = Normalized(Conjugate(a) * rotations[b] * a * _bindLocal[b]);
            }
            rootLocalRotation = Conjugate(_modelToRig);
            rootLocalScale = 0.001f / _rigUnitsPerMm;
            rootLocalPosition = rootPositionMm * 0.001f - rootLocalRotation * (_positionInRoot[GhostPose.Wrist] * rootLocalScale);
            return true;
        }

        // ---- small quaternion helpers (managed code only: no engine call) -----------------------------------------------
        /// <summary>The inverse of a unit quaternion.</summary>
        public static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        /// <summary>Unit length; identity for a zero or non-finite input.</summary>
        public static Quaternion Normalized(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(length > 1e-12f) || float.IsInfinity(length)) return Quaternion.identity;
            float inv = 1f / length;
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        // ---- the similarity fit (Horn 1987, "Closed-form solution of absolute orientation using unit quaternions") ---------
        /// <summary>
        /// Least-squares similarity between two point sets: to[i] ~= scale * (rotation * from[i]) + translation. The rotation
        /// is the eigenvector of the largest eigenvalue of Horn's symmetric 4x4 matrix built from the cross-covariance, found
        /// by Jacobi sweeps in double precision; scale = sum(to' . R from') / sum(|from'|^2) over the centred points.
        /// maxError = the largest |to[i] - fitted from[i]|, in the units of 'to'. False when the points are degenerate.
        /// Allocates two 16-element scratch arrays.
        /// </summary>
        public static bool FitSimilarity(Vector3[] from, Vector3[] to, int count,
            out Quaternion rotation, out float scale, out Vector3 translation, out float maxError)
        {
            rotation = Quaternion.identity;
            scale = 1f;
            translation = Vector3.zero;
            maxError = float.PositiveInfinity;
            if (from == null || to == null || count < 3 || from.Length < count || to.Length < count) return false;

            double fx = 0.0, fy = 0.0, fz = 0.0, tx = 0.0, ty = 0.0, tz = 0.0;
            for (int i = 0; i < count; i++)
            {
                fx += from[i].x; fy += from[i].y; fz += from[i].z;
                tx += to[i].x; ty += to[i].y; tz += to[i].z;
            }
            double inv = 1.0 / count;
            fx *= inv; fy *= inv; fz *= inv;
            tx *= inv; ty *= inv; tz *= inv;

            double sxx = 0.0, sxy = 0.0, sxz = 0.0, syx = 0.0, syy = 0.0, syz = 0.0, szx = 0.0, szy = 0.0, szz = 0.0;
            double sumFrom = 0.0;
            for (int i = 0; i < count; i++)
            {
                double ax = from[i].x - fx, ay = from[i].y - fy, az = from[i].z - fz;
                double bx = to[i].x - tx, by = to[i].y - ty, bz = to[i].z - tz;
                sxx += ax * bx; sxy += ax * by; sxz += ax * bz;
                syx += ay * bx; syy += ay * by; syz += ay * bz;
                szx += az * bx; szy += az * by; szz += az * bz;
                sumFrom += ax * ax + ay * ay + az * az;
            }
            if (!(sumFrom > 1e-12) || double.IsInfinity(sumFrom)) return false;

            // Horn's N, rows and columns in the order (w, x, y, z)
            double[] a =
            {
                sxx + syy + szz, syz - szy, szx - sxz, sxy - syx,
                syz - szy, sxx - syy - szz, sxy + syx, szx + sxz,
                szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy,
                sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz,
            };
            double[] v = new double[16];
            double qw, qx, qy, qz;
            if (!LargestEigenvector(a, v, out qw, out qx, out qy, out qz)) return false;
            double qn = Math.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
            if (!(qn > 1e-12)) return false;
            if (qw < 0.0) qn = -qn;               // q and -q are one rotation: keep w >= 0
            qw /= qn; qx /= qn; qy /= qn; qz /= qn;

            double dots = 0.0;
            for (int i = 0; i < count; i++)
            {
                double rx, ry, rz;
                Rotate(qw, qx, qy, qz, from[i].x - fx, from[i].y - fy, from[i].z - fz, out rx, out ry, out rz);
                dots += rx * (to[i].x - tx) + ry * (to[i].y - ty) + rz * (to[i].z - tz);
            }
            double s = dots / sumFrom;
            if (!(s > 1e-12) || double.IsInfinity(s)) return false;

            double cx, cy, cz;
            Rotate(qw, qx, qy, qz, fx, fy, fz, out cx, out cy, out cz);
            double ox = tx - s * cx, oy = ty - s * cy, oz = tz - s * cz;

            double worst = 0.0;
            for (int i = 0; i < count; i++)
            {
                double rx, ry, rz;
                Rotate(qw, qx, qy, qz, from[i].x, from[i].y, from[i].z, out rx, out ry, out rz);
                double dx = to[i].x - (s * rx + ox), dy = to[i].y - (s * ry + oy), dz = to[i].z - (s * rz + oz);
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > worst) worst = d;
            }

            rotation = new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
            scale = (float)s;
            translation = new Vector3((float)ox, (float)oy, (float)oz);
            maxError = (float)worst;
            return true;
        }

        // v' = v + 2w (u x v) + 2 u x (u x v) for a unit quaternion (w, u): the rotation UnityEngine.Quaternion * Vector3 does.
        static void Rotate(double w, double x, double y, double z, double px, double py, double pz, out double rx, out double ry, out double rz)
        {
            double cx = y * pz - z * py, cy = z * px - x * pz, cz = x * py - y * px;
            double dx = y * cz - z * cy, dy = z * cx - x * cz, dz = x * cy - y * cx;
            rx = px + 2.0 * (w * cx + dx);
            ry = py + 2.0 * (w * cy + dy);
            rz = pz + 2.0 * (w * cz + dz);
        }

        // Cyclic Jacobi on a symmetric 4x4 matrix (row-major 'a', destroyed); 'v' receives the eigenvectors as columns.
        // Returns the eigenvector of the largest eigenvalue; false when it is not clearly larger than the next one.
        static bool LargestEigenvector(double[] a, double[] v, out double w, out double x, out double y, out double z)
        {
            w = 1.0; x = 0.0; y = 0.0; z = 0.0;
            for (int i = 0; i < 16; i++) v[i] = i % 5 == 0 ? 1.0 : 0.0;
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double off = 0.0, diag = 0.0;
                for (int p = 0; p < 4; p++)
                {
                    diag += a[p * 4 + p] * a[p * 4 + p];
                    for (int q = p + 1; q < 4; q++) off += a[p * 4 + q] * a[p * 4 + q];
                }
                if (double.IsNaN(off) || double.IsInfinity(off)) return false;
                if (off <= 1e-26 * diag || off < 1e-300) break;
                for (int p = 0; p < 3; p++)
                {
                    for (int q = p + 1; q < 4; q++)
                    {
                        double apq = a[p * 4 + q];
                        if (Math.Abs(apq) < 1e-300) continue;
                        double theta = (a[q * 4 + q] - a[p * 4 + p]) / (2.0 * apq);
                        double t = Math.Abs(theta) > 1e100 ? 0.5 / theta
                            : (theta >= 0.0 ? 1.0 : -1.0) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        double c = 1.0 / Math.Sqrt(t * t + 1.0), s = t * c;
                        for (int k = 0; k < 4; k++)           // columns p and q: A P
                        {
                            double akp = a[k * 4 + p], akq = a[k * 4 + q];
                            a[k * 4 + p] = c * akp - s * akq;
                            a[k * 4 + q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 4; k++)           // rows p and q: P^T (A P)
                        {
                            double apk = a[p * 4 + k], aqk = a[q * 4 + k];
                            a[p * 4 + k] = c * apk - s * aqk;
                            a[q * 4 + k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 4; k++)           // V P
                        {
                            double vkp = v[k * 4 + p], vkq = v[k * 4 + q];
                            v[k * 4 + p] = c * vkp - s * vkq;
                            v[k * 4 + q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            int best = 0;
            for (int i = 1; i < 4; i++) if (a[i * 4 + i] > a[best * 4 + best]) best = i;
            double second = double.NegativeInfinity;
            for (int i = 0; i < 4; i++) if (i != best && a[i * 4 + i] > second) second = a[i * 4 + i];
            double top = a[best * 4 + best];
            if (!(top - second > 1e-9 * Math.Max(Math.Abs(top), 1e-30))) return false;
            w = v[0 * 4 + best];
            x = v[1 * 4 + best];
            y = v[2 * 4 + best];
            z = v[3 * 4 + best];
            return true;
        }
    }

    /// <summary>
    /// Drives the 16 bones of an imported hand rig from GhostPose data: Apply (jump), Blend (between two poses), MorphTo +
    /// Tick (eased move to the next pose over a few hundred milliseconds). Plain C#: the owner (PosedGhostHand) calls Tick
    /// from its Update. Writes only the model root's local position, rotation and scale and the 16 bones' localRotation.
    /// Apply, Blend, MorphTo and Tick allocate nothing once calibrated (call Calibrate at set-up; otherwise the first pose
    /// calibrates, which allocates once).
    /// </summary>
    public sealed class GhostPoseDriver
    {
        readonly Transform[] _bones = new Transform[GhostPose.BoneCount];
        readonly Matrix4x4[] _rootToBone = new Matrix4x4[GhostPose.BoneCount];   // root space -> bone's local space, bind pose
        readonly Vector3[] _tipLocal = new Vector3[GhostPose.DigitCount];         // fingertip in its distal bone's local space
        readonly Quaternion[] _current = new Quaternion[GhostPose.BoneCount];     // the pose on screen (parent-relative, H axes)
        readonly Quaternion[] _from = new Quaternion[GhostPose.BoneCount];        // where the running morph started
        readonly Quaternion[] _local = new Quaternion[GhostPose.BoneCount];       // Solve output
        readonly List<string> _missing = new List<string>();
        readonly GhostRigSolver _solver = new GhostRigSolver();
        Transform _root;
        bool _bound, _calibrationFailed, _hasPose, _morphing;
        Vector3 _currentRootMm, _fromRootMm;
        GhostPose _target;
        float _morphSeconds, _morphElapsed;
        string _hand;

        /// <summary>The model root given to Bind (its parent is the pose frame), or null.</summary>
        public Transform Root { get { return _root; } }
        /// <summary>True after a successful Bind (all 16 bones found, hierarchy and scales usable).</summary>
        public bool IsBound { get { return _bound && _root != null; } }
        /// <summary>Canonical names of the bones the last Bind did not find (empty when all were found).</summary>
        public IReadOnlyList<string> MissingBones { get { return _missing; } }
        /// <summary>Why the last Bind or Calibrate failed, or null.</summary>
        public string Problem { get; private set; }
        /// <summary>True when the rig has been matched to the pose files' bind data (Calibrate, or the first Apply).</summary>
        public bool IsCalibrated { get { return _solver.IsFitted; } }
        /// <summary>Largest joint mismatch of the calibration, pose millimetres.</summary>
        public float CalibrationErrorMm { get { return _solver.FitErrorMm; } }
        /// <summary>The hand of the calibration pose; poses of the other hand are refused.</summary>
        public string Hand { get { return _hand; } }
        /// <summary>The maths behind this driver (fit results, for diagnostics and tests).</summary>
        public GhostRigSolver Solver { get { return _solver; } }
        /// <summary>The pose last applied or being morphed to; null after a Blend or before any pose.</summary>
        public GhostPose Target { get { return _target; } }
        /// <summary>True while a MorphTo is running (Tick advances it).</summary>
        public bool IsMorphing { get { return _morphing; } }
        /// <summary>True once any pose has been put on the rig.</summary>
        public bool HasPose { get { return _hasPose; } }
        /// <summary>The wrist position currently shown, neck frame, mm.</summary>
        public Vector3 CurrentRootPositionMm { get { return _currentRootMm; } }

        /// <summary>The transform bound to a canonical bone (GhostPose bone index), or null.</summary>
        public Transform GetBone(int bone)
        {
            return bone >= 0 && bone < GhostPose.BoneCount ? _bones[bone] : null;
        }

        /// <summary>The parent-relative rotation of a bone currently shown (the pose files' convention).</summary>
        public Quaternion GetCurrentRotation(int bone)
        {
            return bone >= 0 && bone < GhostPose.BoneCount ? _current[bone] : Quaternion.identity;
        }

        // ---- binding ---------------------------------------------------------------------------------------------------
        /// <summary>
        /// Finds the 16 canonical bones below <paramref name="root"/> and caches their bind data (the rig must be in its bind
        /// pose now). False when a bone is missing (see MissingBones) or the hierarchy is unusable (see Problem). Forgets any
        /// earlier binding, calibration and pose.
        /// </summary>
        public bool Bind(Transform root)
        {
            _bound = false;
            _calibrationFailed = false;
            _hasPose = false;
            _morphing = false;
            _target = null;
            _hand = null;
            _missing.Clear();
            _solver.Reset();
            Problem = null;
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                _bones[b] = null;
                _current[b] = Quaternion.identity;
            }
            _currentRootMm = Vector3.zero;
            _root = root;
            if (root == null)
            {
                for (int b = 0; b < GhostPose.BoneCount; b++) _missing.Add(GhostPose.BoneNames[b]);
                Problem = "no root transform was given";
                return false;
            }

            List<Transform> order = BreadthFirst(root);
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                _bones[b] = FindBone(order, GhostPose.BoneNames[b]);
                if (_bones[b] == null) _missing.Add(GhostPose.BoneNames[b]);
            }
            if (_missing.Count > 0)
            {
                Problem = "bones not found below " + root.name + ": " + string.Join(", ", _missing.ToArray());
                return false;
            }
            if (_bones[GhostPose.Wrist] == root)
            {
                Problem = "the root must be a transform above the wrist bone, not the wrist itself";
                return false;
            }
            for (int b = 1; b < GhostPose.BoneCount; b++)
            {
                Transform parent = _bones[GhostPose.BoneParents[b]];
                if (_bones[b] == parent || !_bones[b].IsChildOf(parent))
                {
                    Problem = "bone " + _bones[b].name + " is not below " + parent.name + " (the canonical hierarchy, README section 2)";
                    return false;
                }
            }

            Matrix4x4 rootToWorld = root.localToWorldMatrix;
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                Transform bone = _bones[b];
                Matrix4x4 m = bone.worldToLocalMatrix * rootToWorld;
                string scaleProblem = ScaleProblem(m);
                if (scaleProblem != null)
                {
                    Problem = "bone " + bone.name + " " + scaleProblem + " relative to the root: apply the scales before exporting";
                    return false;
                }
                _rootToBone[b] = m;
                _solver.SetBind(b, bone.localRotation, RotationInRoot(root, bone.parent), root.InverseTransformPoint(bone.position));
            }
            _bound = true;
            return true;
        }

        /// <summary>
        /// Matches the rig to the pose files: fits the fixed turn and scale from the reference pose's bind joints, and fixes
        /// the hand (poses of the other hand are then refused). Apply, Blend and MorphTo call it with their first pose when
        /// it has not run. False, with Problem set, when the rig does not match; nothing is then drawn until Bind or
        /// Calibrate succeeds.
        /// </summary>
        public bool Calibrate(GhostPose reference)
        {
            if (!IsBound)
            {
                Problem = Problem ?? "Bind has not succeeded";
                return false;
            }
            if (!_solver.Fit(reference))
            {
                _calibrationFailed = true;
                Problem = _solver.FitProblem;
                return false;
            }
            _calibrationFailed = false;
            _hand = reference.Hand;
            Problem = null;
            for (int d = 0; d < GhostPose.DigitCount; d++)
            {
                Vector3 tipInRoot = _solver.PoseToRig(reference.GetBindPositionMm(GhostPose.BoneCount + d));
                _tipLocal[d] = _rootToBone[GhostPose.DistalBone(d)].MultiplyPoint3x4(tipInRoot);
            }
            return true;
        }

        // ---- posing ----------------------------------------------------------------------------------------------------
        /// <summary>Shows a pose at once (stops a running morph). False, and nothing changes, when it cannot be shown.</summary>
        public bool Apply(GhostPose pose)
        {
            if (!Ready(pose)) return false;
            pose.CopyRotations(_current);
            _currentRootMm = pose.RootPositionMm;
            _target = pose;
            _morphing = false;
            _hasPose = true;
            Write();
            return true;
        }

        /// <summary>
        /// Shows the blend of two poses of this hand, eased like GhostPose.Blend (t = 0 gives a, 1 gives b; smoothstep in
        /// between). Stops a running morph. Allocation-free; safe to call every frame.
        /// </summary>
        public bool Blend(GhostPose a, GhostPose b, float t)
        {
            return BlendLinear(a, b, GhostPose.Ease(t));
        }

        /// <summary>As Blend, but u is used as it is (clamped to 0..1): for callers with their own timing curve.</summary>
        public bool BlendLinear(GhostPose a, GhostPose b, float u)
        {
            if (!Ready(a) || !Ready(b)) return false;
            Vector3 root;
            if (!GhostPose.BlendLinear(a, b, u, _current, out root)) return false;
            _currentRootMm = root;
            _target = null;
            _morphing = false;
            _hasPose = true;
            Write();
            return true;
        }

        /// <summary>
        /// Starts an eased move (smoothstep) from what is on screen now to <paramref name="target"/>, taking
        /// <paramref name="seconds"/>; Tick advances it. The first pose ever, or seconds &lt;= 0, is shown at once. Asking
        /// again for the pose already shown or already being morphed to changes nothing (safe to call every frame).
        /// </summary>
        public bool MorphTo(GhostPose target, float seconds)
        {
            if (!Ready(target)) return false;
            if (!_hasPose || !(seconds > 0f)) return Apply(target);
            if (ReferenceEquals(_target, target)) return true;
            for (int i = 0; i < GhostPose.BoneCount; i++) _from[i] = _current[i];
            _fromRootMm = _currentRootMm;
            _target = target;
            _morphSeconds = seconds;
            _morphElapsed = 0f;
            _morphing = true;
            return true;
        }

        /// <summary>Advances a running morph by dt seconds and writes the bones. Does nothing when no morph runs.</summary>
        public void Tick(float dt)
        {
            if (!_morphing) return;
            if (_target == null || !IsBound)
            {
                _morphing = false;
                return;
            }
            if (dt > 0f && !float.IsInfinity(dt)) _morphElapsed += dt;
            else if (float.IsPositiveInfinity(dt)) _morphElapsed = _morphSeconds;
            float u = _morphElapsed >= _morphSeconds ? 1f : _morphElapsed / _morphSeconds;
            if (u >= 1f)
            {
                _target.CopyRotations(_current);
                _currentRootMm = _target.RootPositionMm;
                _morphing = false;
            }
            else
            {
                float e = GhostPose.Ease(u);
                for (int i = 0; i < GhostPose.BoneCount; i++) _current[i] = GhostPose.Slerp(_from[i], _target.GetRotation(i), e);
                Vector3 a = _fromRootMm, b = _target.RootPositionMm;
                _currentRootMm = new Vector3(a.x + (b.x - a.x) * e, a.y + (b.y - a.y) * e, a.z + (b.z - a.z) * e);
            }
            Write();
        }

        /// <summary>
        /// Where a fingertip of the rig is now, in world space (digit 0 thumb .. 4 pinky): the pose files' bind fingertip
        /// carried by the rig's distal bone. False before calibration.
        /// </summary>
        public bool TryGetFingertip(int digit, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            if (digit < 0 || digit >= GhostPose.DigitCount || !IsBound || !_solver.IsFitted) return false;
            Transform distal = _bones[GhostPose.DistalBone(digit)];
            if (distal == null) return false;
            worldPosition = distal.TransformPoint(_tipLocal[digit]);
            return true;
        }

        bool Ready(GhostPose pose)
        {
            if (pose == null || !_bound) return false;
            if (_root == null)
            {
                _bound = false;
                _morphing = false;
                Problem = "the bound rig has been destroyed";
                return false;
            }
            if (!_solver.IsFitted)
            {
                if (_calibrationFailed || !Calibrate(pose)) return false;
            }
            return string.Equals(pose.Hand, _hand, StringComparison.Ordinal);
        }

        void Write()
        {
            Vector3 position;
            Quaternion rotation;
            float scale;
            if (!_solver.Solve(_current, _currentRootMm, _local, out position, out rotation, out scale)) return;
            _root.localPosition = position;
            _root.localRotation = rotation;
            _root.localScale = new Vector3(scale, scale, scale);
            for (int b = 0; b < GhostPose.BoneCount; b++) _bones[b].localRotation = _local[b];
        }

        // ---- finding the bones -----------------------------------------------------------------------------------------
        /// <summary>
        /// True when a transform name stands for a canonical bone: the same name, or the bone name after a prefix that ends
        /// in a separator (':', '/', '|', '.', '_', '-' or a space), e.g. "ghost_hand_fretting:wrist". Case-sensitive.
        /// </summary>
        public static bool NameMatches(string transformName, string boneName)
        {
            if (string.IsNullOrEmpty(transformName) || string.IsNullOrEmpty(boneName)) return false;
            if (string.Equals(transformName, boneName, StringComparison.Ordinal)) return true;
            int cut = transformName.Length - boneName.Length;
            if (cut < 2 || !transformName.EndsWith(boneName, StringComparison.Ordinal)) return false;
            char sep = transformName[cut - 1];
            return sep == ':' || sep == '/' || sep == '|' || sep == '.' || sep == '_' || sep == '-' || sep == ' ';
        }

        /// <summary>
        /// The transform below <paramref name="root"/> (root excluded) that stands for a canonical bone: an exact name wins
        /// over a prefixed one, and a shallower transform over a deeper one. Null when there is none.
        /// </summary>
        public static Transform FindBone(Transform root, string boneName)
        {
            return root == null ? null : FindBone(BreadthFirst(root), boneName);
        }

        static Transform FindBone(List<Transform> order, string boneName)
        {
            Transform prefixed = null;
            for (int i = 1; i < order.Count; i++)   // order[0] is the root itself
            {
                string n = order[i].name;
                if (string.Equals(n, boneName, StringComparison.Ordinal)) return order[i];
                if (prefixed == null && NameMatches(n, boneName)) prefixed = order[i];
            }
            return prefixed;
        }

        static List<Transform> BreadthFirst(Transform root)
        {
            var order = new List<Transform>();
            order.Add(root);
            for (int i = 0; i < order.Count; i++)
            {
                Transform t = order[i];
                for (int c = 0; c < t.childCount; c++) order.Add(t.GetChild(c));
            }
            return order;
        }

        // The rotation of 'node' in the root's space: the product of the localRotations from below the root down to node.
        static Quaternion RotationInRoot(Transform root, Transform node)
        {
            Quaternion q = Quaternion.identity;
            for (Transform t = node; t != null && t != root; t = t.parent) q = t.localRotation * q;
            return GhostRigSolver.Normalized(q);
        }

        // A bone's frame must be the root's frame turned and uniformly scaled (no mirror, no stretch), or rotations would skew.
        static string ScaleProblem(Matrix4x4 m)
        {
            Vector3 c0 = new Vector3(m.m00, m.m10, m.m20), c1 = new Vector3(m.m01, m.m11, m.m21), c2 = new Vector3(m.m02, m.m12, m.m22);
            float l0 = c0.magnitude, l1 = c1.magnitude, l2 = c2.magnitude;
            float lo = Mathf.Min(l0, Mathf.Min(l1, l2)), hi = Mathf.Max(l0, Mathf.Max(l1, l2));
            if (!(lo > 1e-9f) || float.IsInfinity(hi)) return "has a zero or infinite scale";
            if (hi / lo > 1.001f) return "is scaled unevenly";
            float skew = Mathf.Max(Mathf.Abs(Vector3.Dot(c0, c1)), Mathf.Max(Mathf.Abs(Vector3.Dot(c0, c2)), Mathf.Abs(Vector3.Dot(c1, c2)))) / (lo * lo);
            if (skew > 1e-3f) return "is skewed";
            if (Vector3.Dot(Vector3.Cross(c0, c1), c2) < 0f) return "is mirrored (negative scale)";
            return null;
        }
    }
}
