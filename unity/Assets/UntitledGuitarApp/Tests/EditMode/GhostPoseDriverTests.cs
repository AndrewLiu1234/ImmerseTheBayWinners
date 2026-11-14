// EditMode tests for GhostPoseDriver, GhostRigSolver and PosedGhostHand (Runtime/GhostHand/GhostPoseDriver.cs, PosedGhostHand.cs).
// Expected values never come from the driver's own formula: a bone's rotation in the neck frame is checked against
// D[b] F^-1 W[b] (the README's total turn composed with the rig's bind rotation) and every joint and fingertip against the
// README's forward kinematics, both computed here; on real transforms Unity itself composes the hierarchy. The synthetic
// rigs have non-identity bind rotations, a name prefix, an "Armature" node with its own turn and scale, a helper transform
// between two bones, a scaled and turned model root, and a fixed turn F (plus scale and offset) between the pose files'
// axes and the rig, as an FBX import adds one. Tests marked "pure" call no engine function (only managed maths).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.GhostHand;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Tests.EditMode
{
    public sealed class GhostPoseDriverTests
    {
        // A hand's bind data (the throwaway test rig of poses/README.md section 5): 16 joints, then 5 fingertips; mm, H axes.
        static readonly Vector3[] Bind =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(32f, -6f, 28f), new Vector3(56.055f, -12.599f, 56.667f), new Vector3(73.532f, -25.277f, 77.496f),
            new Vector3(30f, 0f, 86f), new Vector3(34.241f, -10.87f, 126.347f), new Vector3(36.467f, -25.783f, 147.528f),
            new Vector3(10f, 0f, 88f), new Vector3(10f, -11.906f, 132.433f), new Vector3(10f, -28.539f, 156.188f),
            new Vector3(-10f, 0f, 86f), new Vector3(-13.536f, -10.87f, 126.414f), new Vector3(-15.463f, -26.357f, 148.447f),
            new Vector3(-28f, 0f, 80f), new Vector3(-34.627f, -8.541f, 111.179f), new Vector3(-38.034f, -20.013f, 127.204f),
            new Vector3(86.334f, -41.99f, 92.753f), new Vector3(37.786f, -43.805f, 160.077f), new Vector3(10f, -47.38f, 169.38f),
            new Vector3(-16.563f, -44.378f, 161.018f), new Vector3(-40.299f, -35.576f, 137.864f),
        };
        static readonly Vector3 SomeRootMm = new Vector3(85.049f, 61.008f, 121.828f);
        const string Prefix = "ghost_hand_fretting:";
        const float RealFileToleranceMm = 3f;    // docs/TODO.md SL4: a fingertip within 3 mm of its dot
        const float ExactMm = 0.01f;             // synthetic data: float rounding only
        const double ExactDeg = 0.02;

        readonly List<GameObject> _made = new List<GameObject>();
        readonly List<string> _log = new List<string>();
        Action<LogType, string> _savedOutput;
        string _folder;

        [SetUp]
        public void SetUp()
        {
            _savedOutput = UGALog.Output;
            _log.Clear();
            UGALog.Output = (type, line) => { lock (_log) _log.Add(line); };
            _folder = Path.Combine(Path.GetTempPath(), "uga-pose-driver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            UGALog.Output = _savedOutput;
            for (int i = 0; i < _made.Count; i++)
                if (_made[i] != null) UnityEngine.Object.DestroyImmediate(_made[i]);
            _made.Clear();
            try
            {
                if (_folder != null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
            catch (Exception)
            {
                // a leftover temp folder must not fail a test
            }
        }

        // ================================================================================================ pure maths

        [Test]
        public void NameMatches_AcceptsExactAndPrefixedNamesOnly()   // pure
        {
            Assert.IsTrue(GhostPoseDriver.NameMatches("wrist", "wrist"));
            Assert.IsTrue(GhostPoseDriver.NameMatches("ghost_hand_fretting:wrist", "wrist"));
            Assert.IsTrue(GhostPoseDriver.NameMatches("Armature|index_distal", "index_distal"));
            Assert.IsTrue(GhostPoseDriver.NameMatches("Armature/thumb_proximal", "thumb_proximal"));
            Assert.IsTrue(GhostPoseDriver.NameMatches("L.ring_intermediate", "ring_intermediate"));
            Assert.IsTrue(GhostPoseDriver.NameMatches("left_wrist", "wrist"));
            Assert.IsFalse(GhostPoseDriver.NameMatches("index_distal_end", "index_distal"), "Blender's leaf bones are not the bone");
            Assert.IsFalse(GhostPoseDriver.NameMatches("wristband", "wrist"));
            Assert.IsFalse(GhostPoseDriver.NameMatches("xindex_distal", "index_distal"), "no separator before the name");
            Assert.IsFalse(GhostPoseDriver.NameMatches("Wrist", "wrist"), "names are case-sensitive");
            Assert.IsFalse(GhostPoseDriver.NameMatches(null, "wrist"));
            Assert.IsFalse(GhostPoseDriver.NameMatches("wrist", null));
            for (int a = 0; a < GhostPose.BoneCount; a++)
                for (int b = 0; b < GhostPose.BoneCount; b++)
                    if (a != b)
                        Assert.IsFalse(GhostPoseDriver.NameMatches(GhostPose.BoneNames[a], GhostPose.BoneNames[b]),
                            GhostPose.BoneNames[a] + " must never be taken for " + GhostPose.BoneNames[b]);
        }

        [Test]
        public void FitSimilarity_RecoversAKnownTurnScaleAndOffset()   // pure
        {
            Quaternion turn = AxisAngle(137f, new Vector3(0.3f, -1f, 0.6f));
            const float scale = 0.1f;
            var offset = new Vector3(1.5f, -2f, 0.7f);
            var from = new Vector3[GhostPose.JointCount];
            var to = new Vector3[GhostPose.JointCount];
            for (int i = 0; i < from.Length; i++)
            {
                from[i] = Bind[i];
                to[i] = (turn * Bind[i]) * scale + offset;
            }
            Quaternion r;
            float s, error;
            Vector3 t;
            Assert.IsTrue(GhostRigSolver.FitSimilarity(from, to, from.Length, out r, out s, out t, out error));
            Assert.That(AngleDeg(turn, r), Is.LessThan(1e-3), "rotation");
            Assert.AreEqual(scale, s, scale * 1e-5f, "scale");
            AssertNear(offset, t, 1e-4f, "translation");
            Assert.That(error, Is.LessThan(1e-4f), "residual of exact data");
            Assert.That(r.w, Is.GreaterThanOrEqualTo(0f), "w >= 0");
        }

        [Test]
        public void FitSimilarity_LeavesALargeResidualForAMirroredRig()   // pure
        {
            var from = new Vector3[GhostPose.BoneCount];
            var to = new Vector3[GhostPose.BoneCount];
            for (int i = 0; i < from.Length; i++)
            {
                from[i] = Bind[i];
                to[i] = new Vector3(-Bind[i].x, Bind[i].y, Bind[i].z);   // a left hand where a right hand was expected
            }
            Quaternion r;
            float s, error;
            Vector3 t;
            Assert.IsTrue(GhostRigSolver.FitSimilarity(from, to, from.Length, out r, out s, out t, out error));
            Assert.That(error, Is.GreaterThan(10f), "no rotation turns a hand into its mirror image");
        }

        [Test]
        public void FitSimilarity_RefusesDegeneratePoints()   // pure
        {
            var line = new Vector3[5];
            for (int i = 0; i < line.Length; i++) line[i] = new Vector3(i, 2f * i, 0f);
            var same = new Vector3[5];
            Quaternion r;
            float s, error;
            Vector3 t;
            Assert.IsFalse(GhostRigSolver.FitSimilarity(line, line, line.Length, out r, out s, out t, out error), "collinear points leave a turn about the line free");
            Assert.IsFalse(GhostRigSolver.FitSimilarity(same, same, same.Length, out r, out s, out t, out error), "all in one place");
            Assert.IsFalse(GhostRigSolver.FitSimilarity(line, line, 2, out r, out s, out t, out error), "fewer than three points");
            Assert.IsFalse(GhostRigSolver.FitSimilarity(null, line, 5, out r, out s, out t, out error));
        }

        [Test]
        public void Solver_OnARigWithoutTransforms_MatchesTheTotalTurnsAndTheForwardKinematics()   // pure
        {
            // A rig modelled with plain numbers: every bone's frame W[b] in root space (arbitrary turns), its origin J[b] =
            // s F bind[b] + t, the wrist hanging from a fixed "armature" frame. Children's origins ride on their parent's frame.
            Quaternion f = AxisAngle(180f, Vector3.up) * AxisAngle(-90f, Vector3.right) * AxisAngle(12f, new Vector3(0.2f, 0.4f, 1f));
            const float s = 0.1f;
            var t = new Vector3(1.5f, -2f, 0.7f);
            Quaternion armature = AxisAngle(33f, new Vector3(0.3f, -1f, 0.5f));
            var w = new Quaternion[GhostPose.BoneCount];
            var j = new Vector3[GhostPose.BoneCount];
            var solver = new GhostRigSolver();
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                w[b] = AxisAngle(20f + 11f * b, new Vector3(Mathf.Sin(b), Mathf.Cos(2f * b), 0.5f));
                j[b] = (f * Bind[b]) * s + t;
                Quaternion parent = b == 0 ? armature : w[GhostPose.BoneParents[b]];
                solver.SetBind(b, GhostRigSolver.Conjugate(parent) * w[b], parent, j[b]);
            }
            Assert.IsTrue(solver.IsComplete);
            Assert.IsTrue(solver.Fit(Bind), solver.FitProblem);
            Assert.That(solver.FitErrorMm, Is.LessThan(0.01f));
            Assert.That(AngleDeg(f, solver.ModelToRig), Is.LessThan(1e-3));

            Quaternion[][] cases = { IdentityRotations(), ArbitraryRotations(0), ArbitraryRotations(1) };
            for (int n = 0; n < cases.Length; n++)
            {
                Quaternion[] q = cases[n];
                var local = new Quaternion[GhostPose.BoneCount];
                Vector3 rootPosition;
                Quaternion rootRotation;
                float rootScale;
                Assert.IsTrue(solver.Solve(q, SomeRootMm, local, out rootPosition, out rootRotation, out rootScale));
                Assert.AreEqual(0.001f / s, rootScale, 1e-6f, "root scale: rig units to metres");

                Quaternion[] d = Totals(q);
                Vector3[] fk = Fk(q, SomeRootMm, Bind);
                var posed = new Quaternion[GhostPose.BoneCount];
                var origin = new Vector3[GhostPose.BoneCount];
                for (int b = 0; b < GhostPose.BoneCount; b++)
                {
                    int p = GhostPose.BoneParents[b];
                    posed[b] = (p < 0 ? armature : posed[p]) * local[b];
                    origin[b] = p < 0 ? j[b] : origin[p] + (posed[p] * GhostRigSolver.Conjugate(w[p])) * (j[b] - j[p]);
                    Quaternion inNeck = rootRotation * posed[b];
                    Quaternion expected = d[b] * GhostRigSolver.Conjugate(f) * w[b];
                    Assert.That(AngleDeg(expected, inNeck), Is.LessThan(ExactDeg), "rotation of " + GhostPose.BoneNames[b]);
                    Vector3 neckMm = (rootPosition + rootRotation * (origin[b] * rootScale)) * 1000f;
                    AssertNear(fk[b], neckMm, ExactMm, "joint " + GhostPose.BoneNames[b]);
                }
                if (n == 0)
                    for (int b = 0; b < GhostPose.BoneCount; b++)
                    {
                        Quaternion bindLocal = GhostRigSolver.Conjugate(b == 0 ? armature : w[GhostPose.BoneParents[b]]) * w[b];
                        Assert.That(AngleDeg(bindLocal, local[b]), Is.LessThan(ExactDeg), "identity pose keeps the bind localRotation of " + GhostPose.BoneNames[b]);
                    }
            }
        }

        [Test]
        public void Solver_RefusesARigWhoseJointsDoNotMatchTheBindJoints()   // pure
        {
            var solver = new GhostRigSolver();
            var q = new Quaternion[GhostPose.BoneCount];
            var local = new Quaternion[GhostPose.BoneCount];
            Vector3 position;
            Quaternion rotation;
            float scale;
            Assert.IsFalse(solver.Solve(IdentityRotations(), SomeRootMm, local, out position, out rotation, out scale), "nothing before Fit");
            for (int b = 0; b < GhostPose.BoneCount; b++)
                solver.SetBind(b, Quaternion.identity, Quaternion.identity, Bind[b] * 0.001f + (b == GhostPose.IndexDistal ? new Vector3(0f, 0.005f, 0f) : Vector3.zero));
            Assert.IsFalse(solver.Fit(Bind), "one joint 5 mm off");
            Assert.That(solver.FitErrorMm, Is.GreaterThan(GhostRigSolver.FitToleranceMm));
            StringAssert.Contains("differ", solver.FitProblem);
            Assert.IsFalse(solver.Solve(q, SomeRootMm, local, out position, out rotation, out scale));

            for (int b = 0; b < GhostPose.BoneCount; b++) solver.SetBind(b, Quaternion.identity, Quaternion.identity, Bind[b] * 0.001f);
            Assert.IsTrue(solver.Fit(Bind), "the same rig without the moved joint");
            Assert.IsFalse(new GhostRigSolver().Fit(Bind), "a rig with no bind data");
            Assert.IsFalse(solver.Fit((Vector3[])null));
        }

        [Test]
        public void Solver_Solve_DoesNotAllocate()   // pure
        {
            var solver = new GhostRigSolver();
            for (int b = 0; b < GhostPose.BoneCount; b++)
                solver.SetBind(b, AxisAngle(7f * b, Vector3.up), AxisAngle(5f * b, Vector3.right), Bind[b] * 0.01f);
            Assert.IsTrue(solver.Fit(Bind), solver.FitProblem);
            Quaternion[] a = ArbitraryRotations(0), c = ArbitraryRotations(1);
            var blended = new Quaternion[GhostPose.BoneCount];
            var local = new Quaternion[GhostPose.BoneCount];
            Func<int, bool> frames = n =>
            {
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    for (int b = 0; b < GhostPose.BoneCount; b++) blended[b] = GhostPose.Slerp(a[b], c[b], GhostPose.Ease(i / (float)n));
                    Vector3 p;
                    Quaternion r;
                    float s;
                    ok &= solver.Solve(blended, SomeRootMm, local, out p, out r, out s);
                }
                return ok;
            };
            AssertAllocationFree(frames, "GhostPose.Slerp x16 + GhostRigSolver.Solve");
        }

        // ================================================================================================ the driver on transforms

        [Test]
        public void Driver_Bind_FindsPrefixedBones_IgnoresLeafBonesAndDecoys()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, true);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            Assert.IsTrue(driver.IsBound);
            Assert.AreEqual(0, driver.MissingBones.Count);
            for (int b = 0; b < GhostPose.BoneCount; b++)
                Assert.AreSame(rig.bones[b], driver.GetBone(b), GhostPose.BoneNames[b]);
            Assert.IsFalse(driver.IsCalibrated, "calibration needs a pose");
        }

        [Test]
        public void Driver_Bind_ReportsMissingBonesAndABrokenHierarchy()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, false);
            UnityEngine.Object.DestroyImmediate(rig.bones[GhostPose.RingProximal].gameObject);   // takes the ring finger with it
            var driver = new GhostPoseDriver();
            Assert.IsFalse(driver.Bind(rig.root));
            CollectionAssert.AreEquivalent(new[] { "ring_proximal", "ring_intermediate", "ring_distal" }, driver.MissingBones);
            StringAssert.Contains("ring_intermediate", driver.Problem);
            Assert.IsFalse(driver.Apply(Parse(PoseJson("fretting", "G", SomeRootMm, ArbitraryRotations(0), Bind))), "nothing is drawn unbound");

            Rig moved = MakeRig(Bind, Prefix, Vector3.one, false);
            moved.bones[GhostPose.IndexDistal].SetParent(moved.bones[GhostPose.Wrist], true);
            Assert.IsFalse(driver.Bind(moved.root), "index_distal no longer below index_intermediate");
            StringAssert.Contains("index_distal", driver.Problem);

            Assert.IsFalse(driver.Bind(null));
            Assert.AreEqual(GhostPose.BoneCount, driver.MissingBones.Count);
        }

        [Test]
        public void Driver_IdentityPose_KeepsTheBindLocalRotations_AndPutsTheWristOnTheRoot()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, true);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            var root = new Vector3(10f, 20f, 30f);
            GhostPose identity = Parse(PoseJson("fretting", "rest", root, IdentityRotations(), Bind));
            Assert.IsTrue(driver.Apply(identity), driver.Problem);
            Assert.IsTrue(driver.IsCalibrated);
            Assert.That(driver.CalibrationErrorMm, Is.LessThan(0.01f));
            Assert.That(AngleDeg(rig.F, driver.Solver.ModelToRig), Is.LessThan(1e-3), "the fitted turn is the rig's F");
            Assert.AreEqual(rig.s, driver.Solver.RigUnitsPerMm, rig.s * 1e-5f, "the fitted scale is the rig's s");
            for (int b = 0; b < GhostPose.BoneCount; b++)
                Assert.That(AngleDeg(rig.bindLocal[b], rig.bones[b].localRotation), Is.LessThan(ExactDeg), "bind localRotation of " + GhostPose.BoneNames[b]);
            Assert.That(AngleDeg(GhostRigSolver.Conjugate(rig.F), rig.root.localRotation), Is.LessThan(ExactDeg), "the root carries F^-1");
            AssertNear(new Vector3(0.01f, 0.01f, 0.01f), rig.root.localScale, 1e-6f, "the root scale turns centimetres into metres");
            AssertNear(root, rig.neck.InverseTransformPoint(rig.bones[GhostPose.Wrist].position) * 1000f, ExactMm, "the wrist joint is on rootPositionMm");
            AssertPosed(rig, driver, IdentityRotations(), root, Bind, ExactMm, ExactDeg, "identity pose");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Driver_NonIdentityPose_MatchesAnIndependentComputation(bool mirroredNeck)
        {
            // mirroredNeck: the floating neck of a left-handed player has a negative z scale; the hand must follow it.
            Rig rig = MakeRig(Bind, Prefix, mirroredNeck ? new Vector3(0.8f, 0.8f, -0.8f) : new Vector3(0.8f, 0.8f, 0.8f), true);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            for (int seed = 0; seed < 3; seed++)
            {
                Quaternion[] q = ArbitraryRotations(seed);
                Vector3 root = SomeRootMm + new Vector3(seed * 7f, -seed * 3f, seed * 5f);
                Assert.IsTrue(driver.Apply(Parse(PoseJson("fretting", "G", root, q, Bind))), driver.Problem);
                AssertPosed(rig, driver, q, root, Bind, ExactMm, ExactDeg, "pose " + seed);
            }
        }

        [Test]
        public void Driver_Blend_ShowsTheBlendedPose()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, true);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            GhostPose a = Parse(PoseJson("fretting", "G", SomeRootMm, ArbitraryRotations(0), Bind));
            GhostPose c = Parse(PoseJson("fretting", "C", SomeRootMm + new Vector3(30f, -10f, 5f), ArbitraryRotations(2), Bind));
            var expected = new Quaternion[GhostPose.BoneCount];
            Vector3 root;
            foreach (float t in new[] { 0f, 0.3f, 0.5f, 1f })
            {
                Assert.IsTrue(driver.Blend(a, c, t));
                Assert.IsTrue(GhostPose.Blend(a, c, t, expected, out root));
                AssertPosed(rig, driver, expected, root, Bind, ExactMm, ExactDeg, "Blend t=" + t);
                Assert.IsTrue(driver.BlendLinear(a, c, t));
                Assert.IsTrue(GhostPose.BlendLinear(a, c, t, expected, out root));
                AssertPosed(rig, driver, expected, root, Bind, ExactMm, ExactDeg, "BlendLinear u=" + t);
            }
            Assert.IsNull(driver.Target, "a blend is not one pose");
        }

        [Test]
        public void Driver_MorphTo_EasesOverItsTime_AndRestartsFromWhatIsShown()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, false);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            Quaternion[] qa = ArbitraryRotations(0), qb = ArbitraryRotations(1), qc = ArbitraryRotations(2);
            Vector3 ra = SomeRootMm, rb = SomeRootMm + new Vector3(20f, 0f, -8f), rc = SomeRootMm + new Vector3(-5f, 9f, 3f);
            GhostPose a = Parse(PoseJson("fretting", "G", ra, qa, Bind));
            GhostPose b = Parse(PoseJson("fretting", "C", rb, qb, Bind));
            GhostPose c = Parse(PoseJson("fretting", "D", rc, qc, Bind));

            Assert.IsTrue(driver.MorphTo(a, 0.3f), driver.Problem);
            Assert.IsFalse(driver.IsMorphing, "the first pose ever is shown at once");
            AssertPosed(rig, driver, qa, ra, Bind, ExactMm, ExactDeg, "first pose");

            Assert.IsTrue(driver.MorphTo(b, 0.3f));
            Assert.IsTrue(driver.IsMorphing);
            AssertPosed(rig, driver, qa, ra, Bind, ExactMm, ExactDeg, "nothing moves before Tick");
            driver.Tick(0.15f);
            var half = new Quaternion[GhostPose.BoneCount];
            Vector3 halfRoot;
            Assert.IsTrue(GhostPose.BlendLinear(a, b, GhostPose.Ease(0.5f), half, out halfRoot));
            AssertPosed(rig, driver, half, halfRoot, Bind, ExactMm, ExactDeg, "half-way (smoothstep 0.5)");
            Assert.IsTrue(driver.MorphTo(b, 0.3f), "asking again for the same target");
            driver.Tick(0f);
            AssertPosed(rig, driver, half, halfRoot, Bind, ExactMm, ExactDeg, "the same target does not restart the clock");

            Assert.IsTrue(driver.MorphTo(c, 0.2f), "a new target mid-way");
            driver.Tick(0.1f);
            var expected = new Quaternion[GhostPose.BoneCount];
            float e = GhostPose.Ease(0.5f);
            for (int i = 0; i < GhostPose.BoneCount; i++) expected[i] = GhostPose.Slerp(half[i], qc[i], e);
            AssertPosed(rig, driver, expected, Vector3.Lerp(halfRoot, rc, e), Bind, ExactMm, ExactDeg, "the new morph starts from what was shown");

            driver.Tick(float.NaN);
            driver.Tick(-1f);
            AssertPosed(rig, driver, expected, Vector3.Lerp(halfRoot, rc, e), Bind, ExactMm, ExactDeg, "NaN and negative steps do nothing");
            driver.Tick(5f);
            Assert.IsFalse(driver.IsMorphing);
            Assert.AreSame(c, driver.Target);
            AssertPosed(rig, driver, qc, rc, Bind, ExactMm, ExactDeg, "the end of the morph is exactly the target");

            Assert.IsTrue(driver.MorphTo(a, 0f));
            Assert.IsFalse(driver.IsMorphing, "zero seconds jumps");
            AssertPosed(rig, driver, qa, ra, Bind, ExactMm, ExactDeg, "jump");
        }

        [Test]
        public void Driver_RefusesThePoseOfTheOtherHand_AndARigThatIsNotThePosesRig()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, false);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            GhostPose g = Parse(PoseJson("fretting", "G", SomeRootMm, ArbitraryRotations(0), Bind));
            GhostPose strum = Parse(PoseJson("strumming", "rest", SomeRootMm, ArbitraryRotations(1), Bind));
            Assert.IsTrue(driver.Apply(g));
            Assert.AreEqual("fretting", driver.Hand);
            Assert.IsFalse(driver.Apply(strum), "a strumming pose on the fretting rig");
            Assert.IsFalse(driver.MorphTo(strum, 0.3f));
            Assert.IsFalse(driver.Blend(g, strum, 0.5f));
            AssertPosed(rig, driver, ArbitraryRotations(0), SomeRootMm, Bind, ExactMm, ExactDeg, "still G");

            Rig other = MakeRig(Bind, Prefix, Vector3.one, false);
            // 4 mm longer: bone-local units are 25 mm here (centimetre rig units under the Armature's 2.5 scale)
            other.bones[GhostPose.MiddleDistal].localPosition += other.bones[GhostPose.MiddleDistal].localPosition.normalized * 0.16f;
            var otherDriver = new GhostPoseDriver();
            Assert.IsTrue(otherDriver.Bind(other.root), otherDriver.Problem);
            Quaternion before = other.bones[GhostPose.IndexIntermediate].localRotation;
            Assert.IsFalse(otherDriver.Apply(g), "the rig's bones do not match the poses' bind joints");
            Assert.IsFalse(otherDriver.IsCalibrated);
            StringAssert.Contains("differ", otherDriver.Problem);
            Assert.AreEqual(before, other.bones[GhostPose.IndexIntermediate].localRotation, "nothing is drawn wrongly");
            Assert.IsFalse(otherDriver.Apply(g), "a failed calibration is not retried every frame");
        }

        [Test]
        public void Driver_RealPoseFiles_PutEveryFingertipWithinThreeMillimetresOfTheFile()
        {
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            GhostPoseLibrary.ReadFolder(GhostPoseLibrary.DefaultFolder, poses, errors, warnings);
            if (poses.Count == 0)
                Assert.Inconclusive("No pose file loaded from " + GhostPoseLibrary.DefaultFolder + ", so nothing was checked. This is not a pass.");
            float worst = 0f;
            string worstWhat = null;
            foreach (GhostPose pose in poses)
            {
                Assert.IsTrue(pose.HasBind, pose.Hand + "-" + pose.Name + " has no bind block");
                var bind = new Vector3[GhostPose.JointCount];
                for (int i = 0; i < bind.Length; i++) bind[i] = pose.GetBindPositionMm(i);
                Rig rig = MakeRig(bind, "ghost_hand_" + pose.Hand + ":", Vector3.one, true);
                var driver = new GhostPoseDriver();
                Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
                Assert.IsTrue(driver.Apply(pose), pose.Hand + "-" + pose.Name + ": " + driver.Problem);
                for (int d = 0; d < GhostPose.DigitCount; d++)
                {
                    Vector3 file = pose.GetFingertipMm(d);
                    Vector3 leaf = rig.neck.InverseTransformPoint(rig.tips[d].position) * 1000f;
                    Vector3 world;
                    Assert.IsTrue(driver.TryGetFingertip(d, out world));
                    Vector3 reported = rig.neck.InverseTransformPoint(world) * 1000f;
                    float off = Mathf.Max((leaf - file).magnitude, (reported - file).magnitude);
                    if (off > worst)
                    {
                        worst = off;
                        worstWhat = pose.Hand + "-" + pose.Name + " " + GhostPose.BoneNames[GhostPose.DistalBone(d)];
                    }
                    Assert.That(off, Is.LessThanOrEqualTo(RealFileToleranceMm), pose.Hand + "-" + pose.Name + ": " + GhostPose.BoneNames[GhostPose.DistalBone(d)]
                        + " tip at " + Fmt(leaf) + " (driver reports " + Fmt(reported) + "), file says " + Fmt(file));
                }
            }
            Debug.Log("[GhostPoseDriverTests] " + poses.Count + " real poses: worst fingertip " + worst.ToString("0.000", CultureInfo.InvariantCulture) + " mm from the file (" + worstWhat + ")");
        }

        [Test]
        public void Driver_HotPaths_DoNotAllocate()
        {
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, true);
            var driver = new GhostPoseDriver();
            Assert.IsTrue(driver.Bind(rig.root), driver.Problem);
            GhostPose a = Parse(PoseJson("fretting", "G", SomeRootMm, ArbitraryRotations(0), Bind));
            GhostPose c = Parse(PoseJson("fretting", "C", SomeRootMm, ArbitraryRotations(1), Bind));
            Assert.IsTrue(driver.Apply(a));
            Func<int, bool> frames = n =>
            {
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    ok &= driver.Blend(a, c, i / (float)n);
                    ok &= driver.MorphTo((i & 1) == 0 ? c : a, 0.25f);
                    driver.Tick(0.016f);
                    ok &= driver.Apply(a);
                    Vector3 tip;
                    ok &= driver.TryGetFingertip(i % GhostPose.DigitCount, out tip);
                }
                return ok;
            };
            AssertAllocationFree(frames, "GhostPoseDriver Blend + MorphTo + Tick + Apply + TryGetFingertip");
        }

        // ================================================================================================ PosedGhostHand

        [Test]
        public void PosedGhostHand_WithoutTheFbx_IsUnavailable_WithExactlyOneWarning()
        {
            GhostPoseLibrary library = WriteLibrary();
            Transform socket = Track(new GameObject("Socket")).transform;
            PosedGhostHand hand = PosedGhostHand.Create(socket, "fretting", library, null);
            Assert.IsNotNull(hand, "Create never returns null");
            Assert.IsFalse(hand.IsAvailable);
            Assert.IsNull(hand.Model, "nothing is drawn");
            StringAssert.Contains("ghost_hand_fretting", hand.UnavailableReason);
            AssertOneWarning("ghost_hand_posed_unavailable");
            Assert.IsFalse(hand.SetPose("G", true));
            hand.Tick(0.1f);
            hand.SetFade(0.4f);
            Assert.IsFalse(hand.TryGetFingertip(1, out _));
        }

        [Test]
        public void PosedGhostHand_WithMissingBones_OrAnUnknownHand_IsUnavailable_WithOneWarningEach()
        {
            GhostPoseLibrary library = WriteLibrary();
            Transform socket = Track(new GameObject("Socket")).transform;
            Rig rig = MakeRig(Bind, Prefix, Vector3.one, false);
            UnityEngine.Object.DestroyImmediate(rig.bones[GhostPose.PinkyProximal].gameObject);
            PosedGhostHand hand = PosedGhostHand.Create(socket, "fretting", library, rig.root.gameObject);
            Assert.IsFalse(hand.IsAvailable);
            Assert.IsNull(hand.Model, "the half-found model is removed, not drawn");
            Assert.AreEqual(0, hand.transform.childCount, "no model instance left under the hand");
            StringAssert.Contains("pinky_proximal", hand.UnavailableReason);
            AssertOneWarning("ghost_hand_posed_unavailable");

            _log.Clear();
            PosedGhostHand unknown = PosedGhostHand.Create(socket, "left", library, rig.root.gameObject);
            Assert.IsFalse(unknown.IsAvailable);
            AssertOneWarning("ghost_hand_posed_unavailable");
        }

        [Test]
        public void PosedGhostHand_WithASyntheticRig_PosesTheFingertips_AndUsesTheGhostLook()
        {
            Shader shader = Resources.Load<Shader>(PosedGhostHand.ShaderResourcePath);
            if (shader == null || !shader.isSupported)
                Assert.Inconclusive("The GhostHand shader is not usable here (no GPU?), so PosedGhostHand reports unavailable by design.");
            GhostPoseLibrary library = WriteLibrary();
            Transform socket = Track(new GameObject("Socket")).transform;
            socket.position = new Vector3(-0.2f, 1.1f, 0.6f);
            socket.rotation = AxisAngle(-25f, new Vector3(1f, 0.3f, 0f));
            Rig model = MakeRig(Bind, Prefix, Vector3.one, true);

            PosedGhostHand hand = PosedGhostHand.Create(socket, "fretting", library, model.root.gameObject);
            Assert.IsTrue(hand.IsAvailable, hand.UnavailableReason);
            Assert.AreEqual(0, CountWarnings(), "no warning when everything is there");
            Assert.AreEqual("rest", hand.CurrentPose, "starts in the rest pose");
            Assert.AreSame(socket, hand.transform.parent);

            Renderer[] renderers = hand.Model.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers.Length, Is.GreaterThan(0));
            foreach (Renderer r in renderers)
            {
                Assert.AreEqual(2, r.sharedMaterials.Length, "every material slot is kept");
                foreach (Material m in r.sharedMaterials) Assert.AreEqual("UntitledGuitarApp/GhostHand", m.shader.name);
                Assert.AreSame(renderers[0].sharedMaterial, r.sharedMaterial, "one shared material");
            }

            GhostPose g;
            Assert.IsTrue(library.TryGet("fretting", "G", out g));
            Assert.IsTrue(hand.SetPose("G", true));
            AssertFingertips(hand, socket, g, ExactMm, "G at once");
            Assert.IsTrue(hand.SetPose("rest", false));
            Assert.AreEqual("rest", hand.CurrentPose);
            hand.Tick(hand.morphSeconds + 0.01f);
            GhostPose rest;
            Assert.IsTrue(library.TryGet("fretting", "rest", out rest));
            AssertFingertips(hand, socket, rest, ExactMm, "rest after the morph");

            Assert.IsFalse(hand.SetPose("Bb", true), "a pose that is not loaded");
            Assert.IsFalse(hand.SetPose("Bb", true));
            AssertOneWarning("ghost_hand_pose_missing");
            Assert.AreEqual("rest", hand.CurrentPose);

            var block = new MaterialPropertyBlock();
            hand.SetFade(0.4f, true);
            renderers[0].GetPropertyBlock(block);
            Assert.AreEqual(0.4f, block.GetFloat("_Fade"), 1e-6f, "fade through the property block");
            hand.SetFade(1f);
            hand.Tick(hand.morphSeconds * 0.5f);
            renderers[0].GetPropertyBlock(block);
            Assert.AreEqual(0.7f, block.GetFloat("_Fade"), 1e-3f, "half-way through the eased fade (smoothstep 0.5)");
            hand.Tick(1f);
            Assert.AreEqual(1f, hand.Fade, 1e-6f);
        }

        [Test]
        public void PosedGhostHand_RealFbx_WhenImported_PutsEveryFingertipWithinThreeMillimetres()
        {
            int checkedHands = 0;
            foreach (string handName in GhostPose.Hands)
            {
                if (Resources.Load<GameObject>(PosedGhostHand.ResourcePathFor(handName)) == null) continue;
                var library = new GhostPoseLibrary();
                Transform socket = Track(new GameObject("Socket_" + handName)).transform;
                PosedGhostHand hand = PosedGhostHand.Create(socket, handName, library);
                Assert.IsTrue(hand.IsAvailable, handName + ": " + hand.UnavailableReason);
                checkedHands++;
                foreach (GhostPose pose in library.Poses)
                {
                    if (pose.Hand != handName) continue;
                    Assert.IsTrue(hand.SetPose(pose.Name, true), pose.Name);
                    AssertFingertips(hand, socket, pose, RealFileToleranceMm, handName + "-" + pose.Name);
                    for (int d = 0; d < GhostPose.DigitCount; d++)
                    {
                        Transform leaf = GhostPoseDriver.FindBone(hand.Model.transform, GhostPose.BoneNames[GhostPose.DistalBone(d)] + "_end");
                        if (leaf == null) continue;   // the FBX was exported without leaf bones
                        AssertNear(pose.GetFingertipMm(d), socket.InverseTransformPoint(leaf.position) * 1000f, RealFileToleranceMm,
                            handName + "-" + pose.Name + ": the FBX's own leaf bone at the fingertip");
                    }
                }
            }
            if (checkedHands == 0)
                Assert.Inconclusive("Resources/" + PosedGhostHand.ResourcePathFor("fretting") + " (and _strumming) not imported yet, so the real FBX was not checked. This is not a pass.");
        }

        // ================================================================================================ helpers

        sealed class Rig
        {
            public Transform neck, root, armature, helper;
            public readonly Transform[] bones = new Transform[GhostPose.BoneCount];
            public readonly Transform[] tips = new Transform[GhostPose.DigitCount];
            public readonly Quaternion[] bindLocal = new Quaternion[GhostPose.BoneCount];     // L[b]
            public readonly Quaternion[] bindInRoot = new Quaternion[GhostPose.BoneCount];    // W[b]
            public Quaternion F;
            public float s;
            public Vector3 t;
        }

        // A hand rig the way an FBX import could deliver it: model root (turned, scaled 100x) > Armature (turned, scaled) >
        // prefixed bones with arbitrary bind rotations, a helper between the wrist and middle_proximal, leaf bones at the
        // fingertips, a decoy, a renderer. Its joints sit at s F bind + t in the root's space (rig units = centimetres).
        Rig MakeRig(Vector3[] bind21, string prefix, Vector3 neckScale, bool withHelper)
        {
            var rig = new Rig();
            rig.F = AxisAngle(180f, Vector3.up) * AxisAngle(-90f, Vector3.right) * AxisAngle(12f, new Vector3(0.2f, 0.4f, 1f));
            rig.s = 0.1f;
            rig.t = new Vector3(1.5f, -2f, 0.7f);
            rig.neck = Track(new GameObject("Neck")).transform;
            rig.neck.position = new Vector3(0.3f, 1.2f, 0.5f);
            rig.neck.rotation = AxisAngle(35f, new Vector3(0.2f, 1f, 0.3f));
            rig.neck.localScale = neckScale;
            rig.root = NewChild("ghost_hand_model", rig.neck);
            rig.root.localPosition = new Vector3(0.05f, 0.02f, -0.1f);
            rig.root.localRotation = AxisAngle(-90f, Vector3.right);
            rig.root.localScale = new Vector3(100f, 100f, 100f);
            rig.armature = NewChild("Armature", rig.root);
            rig.armature.localPosition = new Vector3(0.4f, -0.3f, 0.2f);
            rig.armature.localRotation = AxisAngle(33f, new Vector3(0.3f, -1f, 0.5f));
            rig.armature.localScale = new Vector3(2.5f, 2.5f, 2.5f);
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                Transform parent = b == GhostPose.Wrist ? rig.armature : rig.bones[GhostPose.BoneParents[b]];
                if (withHelper && b == GhostPose.MiddleProximal)
                {
                    rig.helper = NewChild("palm_helper", rig.bones[GhostPose.Wrist]);
                    rig.helper.localPosition = new Vector3(0.3f, -0.2f, 0.1f);
                    rig.helper.localRotation = AxisAngle(25f, new Vector3(0f, 1f, 1f));
                    parent = rig.helper;
                }
                Transform bone = NewChild(prefix + GhostPose.BoneNames[b], parent);
                bone.localPosition = parent.InverseTransformPoint(rig.root.TransformPoint(RigPoint(rig, bind21[b])));
                bone.localRotation = AxisAngle(20f + 11f * b, new Vector3(Mathf.Sin(b), Mathf.Cos(2f * b), 0.5f));
                rig.bones[b] = bone;
            }
            for (int d = 0; d < GhostPose.DigitCount; d++)
            {
                Transform distal = rig.bones[GhostPose.DistalBone(d)];
                Transform tip = NewChild(prefix + GhostPose.BoneNames[GhostPose.DistalBone(d)] + "_end", distal);
                tip.localPosition = distal.InverseTransformPoint(rig.root.TransformPoint(RigPoint(rig, bind21[GhostPose.BoneCount + d])));
                rig.tips[d] = tip;
            }
            NewChild("decoy|index_proximal", rig.tips[4]);    // matches by prefix too, but deeper than the real bone
            GameObject mesh = NewChild(prefix + "Mesh", rig.root).gameObject;
            mesh.AddComponent<MeshFilter>();
            mesh.AddComponent<MeshRenderer>().sharedMaterials = new Material[2];
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                rig.bindLocal[b] = rig.bones[b].localRotation;
                rig.bindInRoot[b] = InRoot(rig.root, rig.bones[b]);
            }
            return rig;
        }

        static Vector3 RigPoint(Rig rig, Vector3 mm)
        {
            return (rig.F * mm) * rig.s + rig.t;
        }

        static Transform NewChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        GameObject Track(GameObject go)
        {
            _made.Add(go);
            return go;
        }

        // The rotation of t in root's space, composed here from the localRotations (the hierarchy as Unity stores it).
        static Quaternion InRoot(Transform root, Transform t)
        {
            Quaternion q = Quaternion.identity;
            for (Transform x = t; x != null && x != root; x = x.parent) q = x.localRotation * q;
            return q;
        }

        // Every bone's rotation in the neck frame is D[b] F^-1 W[b]; every joint, leaf bone and reported fingertip is where
        // the README's forward kinematics puts it.
        static void AssertPosed(Rig rig, GhostPoseDriver driver, Quaternion[] q, Vector3 rootMm, Vector3[] bind21, float tolMm, double tolDeg, string what)
        {
            Quaternion[] d = Totals(q);
            Vector3[] fk = Fk(q, rootMm, bind21);
            Quaternion fInverse = GhostRigSolver.Conjugate(rig.F);
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                Quaternion actual = rig.root.localRotation * InRoot(rig.root, rig.bones[b]);
                Quaternion expected = d[b] * fInverse * rig.bindInRoot[b];
                Assert.That(AngleDeg(expected, actual), Is.LessThan(tolDeg), what + ": rotation of " + GhostPose.BoneNames[b] + " in the neck frame");
                AssertNear(fk[b], rig.neck.InverseTransformPoint(rig.bones[b].position) * 1000f, tolMm, what + ": joint " + GhostPose.BoneNames[b]);
            }
            for (int k = 0; k < GhostPose.DigitCount; k++)
            {
                AssertNear(fk[GhostPose.BoneCount + k], rig.neck.InverseTransformPoint(rig.tips[k].position) * 1000f, tolMm, what + ": leaf bone of digit " + k);
                Vector3 world;
                Assert.IsTrue(driver.TryGetFingertip(k, out world), what + ": TryGetFingertip " + k);
                AssertNear(fk[GhostPose.BoneCount + k], rig.neck.InverseTransformPoint(world) * 1000f, tolMm, what + ": reported fingertip of digit " + k);
            }
        }

        static void AssertFingertips(PosedGhostHand hand, Transform socket, GhostPose pose, float tolMm, string what)
        {
            for (int d = 0; d < GhostPose.DigitCount; d++)
            {
                Vector3 world;
                Assert.IsTrue(hand.TryGetFingertip(d, out world), what + ": fingertip " + d);
                AssertNear(pose.GetFingertipMm(d), socket.InverseTransformPoint(world) * 1000f, tolMm, what + ": " + GhostPose.BoneNames[GhostPose.DistalBone(d)]);
            }
        }

        // README section 4, written out again here: D[b] = D[parent] q[b].
        static Quaternion[] Totals(Quaternion[] q)
        {
            var d = new Quaternion[GhostPose.BoneCount];
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                int p = GhostPose.BoneParents[b];
                d[b] = p < 0 ? q[b] : d[p] * q[b];
            }
            return d;
        }

        // README section 4: P[b] = P[parent] + D[parent] (bind[b] - bind[parent]); tip[d] = P[distal] + D[distal] (bindTip - bind[distal]).
        static Vector3[] Fk(Quaternion[] q, Vector3 rootMm, Vector3[] bind21)
        {
            Quaternion[] d = Totals(q);
            var p = new Vector3[GhostPose.JointCount];
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                int parent = GhostPose.BoneParents[b];
                p[b] = parent < 0 ? rootMm : p[parent] + d[parent] * (bind21[b] - bind21[parent]);
            }
            for (int k = 0; k < GhostPose.DigitCount; k++)
            {
                int distal = GhostPose.DistalBone(k);
                p[GhostPose.BoneCount + k] = p[distal] + d[distal] * (bind21[GhostPose.BoneCount + k] - bind21[distal]);
            }
            return p;
        }

        static Quaternion[] IdentityRotations()
        {
            var q = new Quaternion[GhostPose.BoneCount];
            for (int b = 0; b < q.Length; b++) q[b] = Quaternion.identity;
            return q;
        }

        // Deterministic, clearly non-identity turns (the wrist turns far, the fingers by 9-100 degrees about odd axes).
        static Quaternion[] ArbitraryRotations(int seed)
        {
            var q = new Quaternion[GhostPose.BoneCount];
            for (int b = 0; b < q.Length; b++)
            {
                float angle = b == 0 ? 120f + 13f * seed : 9f + 6f * b + 4f * seed;
                q[b] = AxisAngle(angle, new Vector3(1f + (b + seed) % 3, -0.5f + 0.25f * b, 0.75f - 0.1f * b + 0.3f * seed));
            }
            return q;
        }

        // A Pose JSON v1 text whose fingertips are the forward kinematics of its rotations (so the loader accepts it).
        static string PoseJson(string hand, string name, Vector3 rootMm, Quaternion[] q, Vector3[] bind21)
        {
            Vector3[] fk = Fk(q, rootMm, bind21);
            var sb = new StringBuilder();
            sb.Append("{ \"schema\": 1, \"hand\": \"").Append(hand).Append("\", \"name\": \"").Append(name)
              .Append("\", \"units\": \"mm\", \"space\": \"neck-local-unity\", \"rootPositionMm\": ").Append(Xyz(rootMm)).Append(", \"bones\": [");
            for (int b = 0; b < GhostPose.BoneCount; b++)
                sb.Append(b == 0 ? "" : ", ").Append("{ \"name\": \"").Append(GhostPose.BoneNames[b]).Append("\", \"rotation\": ").Append(Xyzw(q[b])).Append(" }");
            sb.Append("], \"fingertips\": [");
            for (int d = 0; d < GhostPose.DigitCount; d++)
                sb.Append(d == 0 ? "" : ", ").Append("{ \"bone\": \"").Append(GhostPose.BoneNames[GhostPose.DistalBone(d)]).Append("\", \"positionMm\": ").Append(Xyz(fk[GhostPose.BoneCount + d])).Append(" }");
            sb.Append("], \"bind\": { \"joints\": [");
            for (int b = 0; b < GhostPose.BoneCount; b++)
                sb.Append(b == 0 ? "" : ", ").Append("{ \"bone\": \"").Append(GhostPose.BoneNames[b]).Append("\", \"positionMm\": ").Append(Xyz(bind21[b])).Append(" }");
            sb.Append("], \"fingertips\": [");
            for (int d = 0; d < GhostPose.DigitCount; d++)
                sb.Append(d == 0 ? "" : ", ").Append("{ \"bone\": \"").Append(GhostPose.BoneNames[GhostPose.DistalBone(d)]).Append("\", \"positionMm\": ").Append(Xyz(bind21[GhostPose.BoneCount + d])).Append(" }");
            sb.Append("] } }");
            return sb.ToString();
        }

        static string Num(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        static string Xyz(Vector3 v) { return "{ \"x\": " + Num(v.x) + ", \"y\": " + Num(v.y) + ", \"z\": " + Num(v.z) + " }"; }
        static string Xyzw(Quaternion q) { return "{ \"x\": " + Num(q.x) + ", \"y\": " + Num(q.y) + ", \"z\": " + Num(q.z) + ", \"w\": " + Num(q.w) + " }"; }
        static string Fmt(Vector3 v) { return "(" + v.x.ToString("0.000", CultureInfo.InvariantCulture) + ", " + v.y.ToString("0.000", CultureInfo.InvariantCulture) + ", " + v.z.ToString("0.000", CultureInfo.InvariantCulture) + ")"; }

        static GhostPose Parse(string json)
        {
            GhostPose pose;
            string error;
            Assert.IsTrue(GhostPose.TryParse(json, out pose, out error), error);
            return pose;
        }

        // fretting-rest (identity turns: the bind pose standing on the root) and fretting-G (arbitrary turns) in a temp folder.
        GhostPoseLibrary WriteLibrary()
        {
            File.WriteAllText(Path.Combine(_folder, "fretting-rest.pose.json"), PoseJson("fretting", "rest", SomeRootMm, IdentityRotations(), Bind));
            File.WriteAllText(Path.Combine(_folder, "fretting-G.pose.json"), PoseJson("fretting", "G", SomeRootMm + new Vector3(12f, -4f, 3f), ArbitraryRotations(0), Bind));
            var library = new GhostPoseLibrary(_folder);
            Assert.AreEqual(2, library.Reload(), "the synthetic pose files load");
            Assert.AreEqual(0, library.Errors.Count);
            return library;
        }

        int CountWarnings()
        {
            int n = 0;
            lock (_log)
                foreach (string line in _log)
                    if (line.IndexOf("\"lvl\":\"warn\"", StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        void AssertOneWarning(string evt)
        {
            var warnings = new List<string>();
            lock (_log)
                foreach (string line in _log)
                    if (line.IndexOf("\"lvl\":\"warn\"", StringComparison.Ordinal) >= 0) warnings.Add(line);
            Assert.AreEqual(1, warnings.Count, "exactly one warning; got:\n" + string.Join("\n", warnings.ToArray()));
            StringAssert.Contains("\"evt\":\"" + evt + "\"", warnings[0]);
        }

        // The quietest of five rounds must allocate nothing (anything the runtime charges once cannot push all five above
        // zero); a 4 KB array must be seen first, or the runtime does not count allocations and the test is inconclusive.
        static void AssertAllocationFree(Func<int, bool> frames, string what)
        {
            Assert.IsTrue(frames(8), "warm-up");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[4096];
            long counted = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(probe);
            if (counted < 4096) Assert.Inconclusive("This runtime does not report allocations per thread (a 4096-byte array counted as " + counted + "), so nothing was measured.");
            long quietest = long.MaxValue;
            bool ok = true;
            for (int round = 0; round < 5; round++)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                ok &= frames(200);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (allocated < quietest) quietest = allocated;
            }
            Assert.IsTrue(ok, what + " failed");
            Assert.AreEqual(0L, quietest, "bytes allocated by 200 frames of " + what + " (quietest of 5 rounds)");
        }

        static Quaternion AxisAngle(float degrees, Vector3 axis)
        {
            float length = Mathf.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
            float half = degrees * (Mathf.PI / 180f) * 0.5f;
            float s = Mathf.Sin(half) / length;
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(half));
        }

        // The angle of a^-1 b in degrees, from the vector part (accurate near zero, unlike acos of the dot product).
        static double AngleDeg(Quaternion a, Quaternion b)
        {
            double ax = -a.x, ay = -a.y, az = -a.z, aw = a.w;
            double rx = aw * b.x + ax * b.w + ay * b.z - az * b.y;
            double ry = aw * b.y + ay * b.w + az * b.x - ax * b.z;
            double rz = aw * b.z + az * b.w + ax * b.y - ay * b.x;
            double rw = aw * b.w - ax * b.x - ay * b.y - az * b.z;
            return 2.0 * Math.Atan2(Math.Sqrt(rx * rx + ry * ry + rz * rz), Math.Abs(rw)) * 180.0 / Math.PI;
        }

        static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
        {
            float off = (actual - expected).magnitude;
            Assert.That(off, Is.LessThanOrEqualTo(tolerance), what + ": expected " + Fmt(expected) + ", got " + Fmt(actual) + " (" + off.ToString("0.#####", CultureInfo.InvariantCulture) + " apart)");
        }
    }
}
