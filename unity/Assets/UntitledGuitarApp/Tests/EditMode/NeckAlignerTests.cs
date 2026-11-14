using System;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Core.Geometry;
using UntitledGuitarApp.Runtime.Head;
using UntitledGuitarApp.Runtime.Neck;
using CoreNeckPose = UntitledGuitarApp.Core.Geometry.NeckPose;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>
    /// SL8 neck alignment: the frame round trip (Unity world <-> Core's NeckSolver <-> the neck frame), the steadiness detector,
    /// the step machine, the failure gates, the re-anchor and the anchoring policy. Everything here is pure arithmetic except the
    /// tests named *_Engine (they create GameObjects; only Unity runs them).
    /// </summary>
    public class NeckAlignerTests
    {
        const float H = CoreNeckLayout.StringHeightMm;

        // ------------------------------------------------------------------ synthetic seated players

        /// <summary>A real neck pose: nut relative to the head, the nut-to-bridge direction, and a hint for the playing-surface normal.</summary>
        struct Scene
        {
            public string name;
            public Vector3 head;
            public AlignedPose truth;
            public bool leftHanded;
        }

        static AlignedPose Pose(Vector3 nut, Vector3 along, Vector3 normalHint)
        {
            Vector3 x = along.normalized;
            Vector3 y = (normalHint - x * Vector3.Dot(normalHint, x)).normalized;
            return new AlignedPose(nut, NeckPlacement.RotationFromAxes(x, y));
        }

        static Vector3 TiltedNormal(float tiltDeg)
        {
            float t = tiltDeg * Mathf.Deg2Rad;
            return new Vector3(0f, Mathf.Sin(t), Mathf.Cos(t));   // forward (away from the player), tipped up by the tilt
        }

        static Scene[] Scenes()
        {
            Vector3 eye = new Vector3(0f, 1.15f, 0f);
            return new[]
            {
                // Guitar on the lap, neck to the left, fretboard tipped back 45 degrees so the player sees it: nut ~0.6 m away.
                new Scene { name = "lap", head = eye, truth = Pose(eye + new Vector3(-0.42f, -0.32f, 0.30f), new Vector3(1f, -0.25f, -0.30f), TiltedNormal(45f)) },
                // A real player's tilted neck: headstock raised ~30 degrees, fretboard tipped back 30 degrees.
                new Scene { name = "tilted", head = new Vector3(0.05f, 1.10f, -0.03f), truth = Pose(new Vector3(-0.38f, 0.92f, 0.32f), new Vector3(1f, -0.58f, -0.20f), TiltedNormal(30f)) },
                // Near (45 cm) and steep.
                new Scene { name = "near", head = new Vector3(-0.1f, 1.2f, 0.02f), truth = Pose(new Vector3(-0.40f, 0.95f, 0.22f), new Vector3(1f, -0.1f, -0.35f), TiltedNormal(60f)) },
                // Far (~0.8 m), fretboard nearly edge-on (tipped back only 20 degrees).
                new Scene { name = "far", head = eye, truth = Pose(eye + new Vector3(-0.55f, -0.45f, 0.38f), new Vector3(1f, -0.2f, -0.25f), TiltedNormal(20f)) },
                // Left-handed: the neck to the right; string 6 still on top. The neck frame is mirrored (scale z = -1).
                new Scene { name = "lefty", head = eye, leftHanded = true, truth = Pose(eye + new Vector3(0.42f, -0.32f, 0.30f), new Vector3(-1f, -0.25f, -0.30f), TiltedNormal(45f)) },
            };
        }

        static Vector3 TargetWorld(Scene s, NeckSolver solver, AlignTarget t)
        {
            return AlignMath.NeckToWorld(s.truth, s.leftHanded, AlignMath.NeckLocalMetres(solver.ModelPoint(t), H));
        }

        static GazeRay[] Rays(Scene s, NeckSolver solver, Vector3 reference, float noiseDeg, int seed)
        {
            var random = new System.Random(seed);
            var rays = new GazeRay[NeckSolver.TargetCount];
            for (int i = 0; i < rays.Length; i++)
            {
                Vector3 d = (TargetWorld(s, solver, (AlignTarget)i) - s.head).normalized;
                rays[i] = AlignMath.ToCoreRay(s.head, Perturb(d, noiseDeg, random), reference);
            }
            return rays;
        }

        /// <summary>The direction turned by exactly <paramref name="degrees"/> toward a random side.</summary>
        static Vector3 Perturb(Vector3 d, float degrees, System.Random random)
        {
            if (degrees <= 0f) return d;
            Vector3 a = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 b = Vector3.Cross(d, a).normalized;
            float phi = (float)(random.NextDouble() * 2.0 * Math.PI);
            Vector3 side = a * Mathf.Cos(phi) + b * Mathf.Sin(phi);
            return AlignMath.AxisAngle(Vector3.Cross(d, side), degrees) * d;
        }

        static Vector3 U(NVector3 v)
        {
            return new Vector3(v.X, v.Y, v.Z);
        }

        // ------------------------------------------------------------------ conventions

        [Test]
        public void QuaternionConventionsAgree()
        {
            // The same (x, y, z, w) must turn a vector the same way in UnityEngine and System.Numerics (AlignMath relies on it).
            Quaternion q = NeckPlacement.RotationFromAxes(new Vector3(0.3f, -0.5f, 0.8f), new Vector3(0.9f, 0.4f, 0.1f));
            var nq = new NQuaternion(q.x, q.y, q.z, q.w);
            Vector3[] vs = { new Vector3(1f, 2f, 3f), Vector3.right, Vector3.up, Vector3.forward };
            foreach (Vector3 v in vs)
            {
                Vector3 unity = q * v;
                Vector3 numerics = U(NVector3.Transform(new NVector3(v.x, v.y, v.z), nq));
                Assert.Less((unity - numerics).magnitude, 1e-5f, "v = " + v);
            }
        }

        [Test]
        public void CoreNormalIsAlongCrossAcross_InUnityCoordinates()
        {
            Quaternion q = NeckPlacement.RotationFromAxes(new Vector3(1f, -0.2f, -0.3f), new Vector3(0f, 0.6f, 0.8f));
            var pose = new CoreNeckPose(NVector3.Zero, new NQuaternion(q.x, q.y, q.z, q.w));
            Vector3 cross = Vector3.Cross(U(pose.AlongAxis), U(pose.AcrossAxis));
            Assert.Less((cross - U(pose.NormalAxis)).magnitude, 1e-5f);
        }

        // ------------------------------------------------------------------ the round trip (the test that matters)

        [Test]
        public void RoundTrip_NoNoise_TargetsLandOnTheSolversPointsAndOnTheTruth()
        {
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            foreach (Scene s in Scenes())
            {
                Vector3 reference = s.head;
                NeckSolveResult result = solver.Solve(Rays(s, solver, reference, 0f, 1));
                Assert.IsTrue(result.Success, s.name + ": " + result.Failure);
                AlignedPose placed = AlignMath.FromCore(result.Pose, reference, s.leftHanded, H);
                for (int i = 0; i < NeckSolver.TargetCount; i++)
                {
                    var t = (AlignTarget)i;
                    Vector3 local = AlignMath.NeckLocalMetres(solver.ModelPoint(t), H);
                    Vector3 drawn = AlignMath.NeckToWorld(placed, s.leftHanded, local);
                    Vector3 solverPoint = AlignMath.ToWorldMetres(result.Pose.ToWorld(solver.ModelPoint(t)), reference);
                    Assert.Less((drawn - solverPoint).magnitude * 1000f, 1f, s.name + " " + t + " vs the solver's point (mm)");
                    Assert.Less((drawn - TargetWorld(s, solver, t)).magnitude * 1000f, 1f, s.name + " " + t + " vs the truth (mm)");
                }
                Assert.Less((placed.nut - s.truth.nut).magnitude * 1000f, 1f, s.name + ": nut (mm)");
                Assert.Greater(Vector3.Dot(placed.UpAxis, s.truth.UpAxis), 0.9999f, s.name + ": +y is the real playing-surface normal");
                Assert.Greater(Vector3.Dot(placed.AlongAxis, s.truth.AlongAxis), 0.9999f, s.name + ": +x nut to bridge");
            }
        }

        [Test]
        public void RoundTrip_StringOneIsOnPlusZ_StringSixOnMinusZ()
        {
            // Independent of the solver: the neck frame's +z must point from the low E (string 6) to the high E (string 1).
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            foreach (Scene s in Scenes())
            {
                NeckSolveResult result = solver.Solve(Rays(s, solver, s.head, 0f, 1));
                AlignedPose placed = AlignMath.FromCore(result.Pose, s.head, s.leftHanded, H);
                Vector3 lowE = TargetWorld(s, solver, AlignTarget.NutLowE);
                Vector3 highE = TargetWorld(s, solver, AlignTarget.NutHighE);
                Vector3 zWorld = AlignMath.NeckToWorld(placed, s.leftHanded, new Vector3(0f, 0f, 1f)) - placed.nut;
                Assert.Greater(Vector3.Dot(zWorld, highE - lowE), 0f, s.name);
            }
        }

        [Test]
        public void ToCore_IsTheInverseOfFromCore()
        {
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            foreach (Scene s in Scenes())
            {
                CoreNeckPose core = AlignMath.ToCore(s.truth, s.head, s.leftHanded, H);
                for (int i = 0; i < NeckSolver.TargetCount; i++)
                {
                    var t = (AlignTarget)i;
                    Vector3 viaCore = AlignMath.ToWorldMetres(core.ToWorld(solver.ModelPoint(t)), s.head);
                    Assert.Less((viaCore - TargetWorld(s, solver, t)).magnitude * 1000f, 0.1f, s.name + " " + t);
                }
                AlignedPose back = AlignMath.FromCore(core, s.head, s.leftHanded, H);
                Assert.Less((back.nut - s.truth.nut).magnitude * 1000f, 0.1f, s.name);
                Assert.Greater(Quaternion.Dot(back.rotation, s.truth.rotation) * Quaternion.Dot(back.rotation, s.truth.rotation), 0.99999f, s.name);
            }
        }

        [Test]
        public void ViewerIsOnThePlayingSide_ForEverySeatedScene()
        {
            foreach (Scene s in Scenes())
                Assert.Greater(AlignMath.ViewerElevationDeg(s.truth, s.head), NeckAlignFlow.MinViewerElevationDeg, s.name);
        }

        [Test]
        public void HalfDegreeNoise_SolvesWithASmallResidual_AndReportsTheNutError()
        {
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            Scene s = Scenes()[0];
            double worstRms = 0, worstNutMm = 0;
            for (int seed = 1; seed <= 20; seed++)
            {
                NeckSolveResult result = solver.Solve(Rays(s, solver, s.head, 0.5f, seed));
                Assert.IsTrue(result.Success, "seed " + seed + ": " + result.Failure);
                AlignedPose placed = AlignMath.FromCore(result.Pose, s.head, false, H);
                worstRms = Math.Max(worstRms, result.ResidualRmsMm);
                worstNutMm = Math.Max(worstNutMm, (placed.nut - s.truth.nut).magnitude * 1000.0);
            }
            Console.WriteLine("0.5 deg noise, 20 seeds, lap scene: worst RMS " + worstRms.ToString("0.0") + " mm, worst nut error " + worstNutMm.ToString("0.0") + " mm");
            Assert.Less(worstRms, NeckAlignFlow.MaxResidualRmsMm, "every fit passes G1's residual gate");
            Assert.Less(worstNutMm, 60.0, "sanity: the nut is not wildly off (the real G1 check is a physical marker)");
        }

        // ------------------------------------------------------------------ steadiness

        static void Feed(GazeSteadiness g, float fromSec, float toSec, float fps, Func<int, Vector3> direction)
        {
            int n = Mathf.RoundToInt((toSec - fromSec) * fps);
            for (int i = 0; i <= n; i++) g.Add(fromSec + i / fps, direction(i));
        }

        static Vector3 Tilted(float degrees)
        {
            return AlignMath.AxisAngle(Vector3.up, degrees) * Vector3.forward;
        }

        [Test]
        public void Steadiness_JitterJustUnderTheThreshold_IsSteadyAfterTheWindow()
        {
            var g = new GazeSteadiness(0.5f, 1.2f);
            Feed(g, 0f, 1.1f, 60f, i => Tilted(i % 2 == 0 ? 0.49f : -0.49f));
            Assert.IsFalse(g.IsSteady, "not before the window has passed");
            Feed(g, 1.1f + 1f / 60f, 1.4f, 60f, i => Tilted(i % 2 == 0 ? 0.49f : -0.49f));
            Assert.IsTrue(g.IsSteady);
            Assert.AreEqual(0.49f, g.JitterDeg, 0.02f);
            Assert.Less(AlignMath.AngleDeg(g.MeanDirection, Vector3.forward), 0.05f, "the mean cancels symmetric jitter");
        }

        [Test]
        public void Steadiness_JitterJustOverTheThreshold_IsNeverSteady()
        {
            var g = new GazeSteadiness(0.5f, 1.2f);
            Feed(g, 0f, 5f, 60f, i => Tilted(i % 2 == 0 ? 0.51f : -0.51f));
            Assert.IsFalse(g.IsSteady);
            Assert.Less(g.Progress, 0.1f);
        }

        [Test]
        public void Steadiness_AJumpRestartsTheRun_AndTheNewSpotIsCaptured()
        {
            var g = new GazeSteadiness(0.5f, 1.2f);
            Feed(g, 0f, 1.0f, 60f, i => Tilted(0f));
            Assert.Greater(g.Progress, 0.7f);
            g.Add(1.02f, Tilted(5f));
            Assert.Less(g.Progress, 0.05f, "a 5 degree jump starts over");
            Feed(g, 1.04f, 2.4f, 60f, i => Tilted(5f));
            Assert.IsTrue(g.IsSteady);
            Assert.Less(AlignMath.AngleDeg(g.MeanDirection, Tilted(5f)), 0.01f);
        }

        [Test]
        public void Steadiness_SlowDrift_IsNotSteady()
        {
            // 2 degrees per second: over 1.2 s that is 2.4 degrees of travel, well over a 0.5 degree radius.
            var g = new GazeSteadiness(0.5f, 1.2f);
            Feed(g, 0f, 4f, 60f, i => Tilted(i / 60f * 2f));
            Assert.IsFalse(g.IsSteady);
        }

        [Test]
        public void Steadiness_AStallOrTimeGoingBack_StartsOver()
        {
            var g = new GazeSteadiness(0.5f, 1.2f);
            Feed(g, 0f, 1.0f, 60f, i => Tilted(0f));
            g.Add(1.5f, Tilted(0f));   // 0.5 s gap > MaxGapSeconds
            Assert.AreEqual(1, g.Count);
            Feed(g, 2f, 2.5f, 60f, i => Tilted(0f));   // the gap from 1.5 to 2.0 also restarts
            g.Add(1.0f, Tilted(0f));   // time went back
            Assert.AreEqual(1, g.Count);
        }

        [Test]
        public void Steadiness_IgnoresZeroAndNaNDirections()
        {
            var g = new GazeSteadiness(0.5f, 1.2f);
            g.Add(0f, Vector3.zero);
            g.Add(0.01f, new Vector3(float.NaN, 0f, 1f));
            Assert.AreEqual(0, g.Count);
        }

        // ------------------------------------------------------------------ the step machine

        /// <summary>Drives a flow like the head would: turn to each target (with a fixed aim error), hold with tiny tremor.</summary>
        static float LookAndHold(NeckAlignFlow flow, Vector3 head, Vector3 target, float aimNoiseDeg, System.Random random, float startSec, float holdSec)
        {
            Vector3 aim = Perturb((target - head).normalized, aimNoiseDeg, random);
            float t = startSec;
            for (int i = 0; i < Mathf.RoundToInt(holdSec * 60f); i++)
            {
                t = startSec + i / 60f;
                Vector3 tremor = Perturb(aim, 0.1f, random);
                if (flow.Tick(t, head, tremor, false)) return t + 1f / 60f;
            }
            return t + 1f / 60f;
        }

        static NeckAlignFlow NewFlow()
        {
            return new NeckAlignFlow(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default, NeckAligner.SteadyThresholdDeg, NeckAligner.SteadyWindowSeconds);
        }

        [Test]
        public void Flow_FourSteadyGlances_Succeed_AndPlaceTheNeckOnTheTruth()
        {
            Scene s = Scenes()[0];
            NeckAlignFlow flow = NewFlow();
            var random = new System.Random(7);
            flow.Begin(false, H);
            Assert.AreEqual(AlignPhase.Capturing, flow.Phase);
            float t = 0f;
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual((AlignTarget)i, flow.CurrentTarget);
                Assert.AreEqual(i, flow.Step);
                t = LookAndHold(flow, s.head, TargetWorld(s, flow.Solver, (AlignTarget)i), 0f, random, t, 3f);
            }
            Assert.AreEqual(AlignPhase.Succeeded, flow.Phase, flow.Outcome.Failure);
            AlignOutcome o = flow.Outcome;
            Assert.IsTrue(o.Success);
            Assert.Less(o.ResidualRmsMm, 5.0);
            Assert.Less((o.Pose.nut - s.truth.nut).magnitude * 1000f, 10f, "0.1 degree tremor only: nut within 1 cm");
            Assert.AreEqual(0, o.ConfirmedCount);
            Assert.AreEqual(4, o.Directions.Length);
            Assert.AreEqual(0.6f, o.NutDistanceM, 0.05f);
            Assert.LessOrEqual(o.MaxJitterDeg, NeckAligner.SteadyThresholdDeg);
        }

        [Test]
        public void Flow_StayingOnTheSameSpot_DoesNotCaptureTheNextTargetThere()
        {
            Scene s = Scenes()[0];
            NeckAlignFlow flow = NewFlow();
            var random = new System.Random(3);
            flow.Begin(false, H);
            float t = LookAndHold(flow, s.head, TargetWorld(s, flow.Solver, AlignTarget.NutLowE), 0f, random, 0f, 3f);
            Assert.AreEqual(1, flow.Step);
            LookAndHold(flow, s.head, TargetWorld(s, flow.Solver, AlignTarget.NutLowE), 0f, random, t, 5f);
            Assert.AreEqual(1, flow.Step, "still waiting for the high E: the gaze never left the previous capture");
            Assert.AreEqual(0f, flow.Progress);
        }

        [Test]
        public void Flow_SpaceCapturesAtOnce()
        {
            Scene s = Scenes()[0];
            NeckAlignFlow flow = NewFlow();
            flow.Begin(false, H);
            float t = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 d = (TargetWorld(s, flow.Solver, (AlignTarget)i) - s.head).normalized;
                Assert.IsFalse(flow.Tick(t, s.head, d, false));
                Assert.IsTrue(flow.Tick(t + 0.02f, s.head, d, true), "step " + i);
                t += 0.5f;
            }
            Assert.AreEqual(AlignPhase.Succeeded, flow.Phase, flow.Outcome.Failure);
            Assert.AreEqual(4, flow.Outcome.ConfirmedCount);
        }

        [Test]
        public void Flow_AllFourGlancesAtOnePoint_FailsWithAReason()
        {
            Scene s = Scenes()[0];
            NeckAlignFlow flow = NewFlow();
            flow.Begin(false, H);
            Vector3 d = (TargetWorld(s, flow.Solver, AlignTarget.NutLowE) - s.head).normalized;
            for (int i = 0; i < 4; i++) flow.Tick(i * 0.1f, s.head, d, true);
            Assert.AreEqual(AlignPhase.Failed, flow.Phase);
            Assert.IsFalse(flow.Outcome.Success);
            Assert.IsFalse(string.IsNullOrEmpty(flow.Outcome.Failure));
            Console.WriteLine("all at one point: " + flow.Outcome.Failure);
        }

        static AlignOutcome RunWithGlances(Scene s, AlignTarget[] lookedAt, float noiseDeg, int seed)
        {
            NeckAlignFlow flow = NewFlow();
            var random = new System.Random(seed);
            flow.Begin(s.leftHanded, H);
            for (int i = 0; i < 4; i++)
                flow.Tick(i * 0.1f, s.head, Perturb((TargetWorld(s, flow.Solver, lookedAt[i]) - s.head).normalized, noiseDeg, random), true);
            return flow.Outcome;
        }

        static readonly AlignTarget[] InOrder = { AlignTarget.NutLowE, AlignTarget.NutHighE, AlignTarget.Fret5LowE, AlignTarget.Fret12LowE };
        static readonly AlignTarget[] NutsSwapped = { AlignTarget.NutHighE, AlignTarget.NutLowE, AlignTarget.Fret5LowE, AlignTarget.Fret12LowE };

        [Test]
        public void Flow_SwappedNutGlances_AreRejected_InEveryScene_WithAndWithoutNoise()
        {
            // The most likely beginner mistake: "low E" and "high E" confused at the nut. It fits well but turns the neck over.
            foreach (Scene s in Scenes())
            {
                for (int seed = 0; seed <= 10; seed++)
                {
                    AlignOutcome o = RunWithGlances(s, NutsSwapped, seed == 0 ? 0f : 0.5f, seed);
                    if (seed <= 1)
                        Console.WriteLine(s.name + " swapped (noise " + (seed == 0 ? "0" : "0.5") + "): rms=" + o.ResidualRmsMm.ToString("0.0") + " elev=" +
                                          o.ViewerElevationDeg.ToString("0.0") + " lowE.up=" +
                                          (o.HasPose ? Vector3.Dot(AlignMath.TowardString6(o.Pose, s.leftHanded), Vector3.up).ToString("0.00") : "-") +
                                          " -> " + (o.Success ? "PLACED" : o.Failure));
                    Assert.IsFalse(o.Success, s.name + " seed " + seed + ": a swapped fit must not be placed");
                }
            }
        }

        [Test]
        public void Flow_CorrectGlancesWithHalfDegreeNoise_AreNotMistakenForMirrored()
        {
            foreach (Scene s in Scenes())
            {
                Console.WriteLine(s.name + " truth: elev=" + AlignMath.ViewerElevationDeg(s.truth, s.head).ToString("0.0") + " lowE.up=" +
                                  Vector3.Dot(AlignMath.TowardString6(s.truth, s.leftHanded), Vector3.up).ToString("0.00"));
                for (int seed = 1; seed <= 10; seed++)
                {
                    AlignOutcome o = RunWithGlances(s, InOrder, 0.5f, seed);
                    Assert.IsTrue(o.Success, s.name + " seed " + seed + ": " + o.Failure);
                }
            }
        }

        [Test]
        public void LooksMirrored_FiresOnATurnedOverNeck_AndNotOnTheTruth()
        {
            foreach (Scene s in Scenes())
            {
                float elevation = AlignMath.ViewerElevationDeg(s.truth, s.head);
                Assert.IsFalse(NeckAlignFlow.LooksMirrored(s.truth, elevation, s.leftHanded), s.name + " truth");
                // Planted: the same neck turned over about its own length (what a swapped-nut fit looks like).
                AlignedPose over = new AlignedPose(s.truth.nut, AlignMath.Normalized(AlignMath.AxisAngle(s.truth.AlongAxis, 180f) * s.truth.rotation));
                Assert.IsTrue(NeckAlignFlow.LooksMirrored(over, AlignMath.ViewerElevationDeg(over, s.head), s.leftHanded), s.name + " turned over");
            }
        }

        [Test]
        public void Flow_WrongOrderForTheFrets_IsRejected()
        {
            Scene s = Scenes()[0];
            NeckAlignFlow flow = NewFlow();
            flow.Begin(false, H);
            AlignTarget[] lookedAt = { AlignTarget.NutLowE, AlignTarget.NutHighE, AlignTarget.Fret12LowE, AlignTarget.Fret5LowE };
            for (int i = 0; i < 4; i++)
                flow.Tick(i * 0.1f, s.head, (TargetWorld(s, flow.Solver, lookedAt[i]) - s.head).normalized, true);
            Console.WriteLine("frets swapped: success=" + flow.Outcome.Success + " rms=" + flow.Outcome.ResidualRmsMm.ToString("0.0") + " failure=" + flow.Outcome.Failure);
            Assert.IsFalse(flow.Outcome.Success);
        }

        [Test]
        public void Flow_CancelStops_AndBeginRestarts()
        {
            NeckAlignFlow flow = NewFlow();
            flow.Begin(false, H);
            Assert.IsTrue(flow.Cancel("test"));
            Assert.AreEqual(AlignPhase.Cancelled, flow.Phase);
            Assert.AreEqual("test", flow.Outcome.Failure);
            Assert.IsFalse(flow.Tick(0f, Vector3.zero, Vector3.forward, true), "no capture after cancel");
            Assert.IsFalse(flow.Cancel("again"), "nothing to cancel");
            flow.Begin(false, H);
            Assert.AreEqual(AlignPhase.Capturing, flow.Phase);
            Assert.AreEqual(0, flow.Step);
        }

        [Test]
        public void Flow_Reanchor_OneGlance_PutsTheNutBackOnTheMovedGuitar()
        {
            // The guitar (or the IMU's yaw) moved 3 degrees about the head: one glance at the nut fixes all of it.
            Scene s = Scenes()[0];
            Quaternion yaw = AlignMath.AxisAngle(Vector3.up, 3f);
            Scene moved = s;
            moved.truth = AlignMath.RotateAbout(s.truth, s.head, yaw);
            NeckAlignFlow flow = NewFlow();
            flow.BeginReanchor(s.truth, false, H);
            Assert.AreEqual(1, flow.StepCount);
            Assert.AreEqual(AlignTarget.NutLowE, flow.CurrentTarget);
            Vector3 d = (TargetWorld(moved, flow.Solver, AlignTarget.NutLowE) - s.head).normalized;
            Assert.IsTrue(flow.Tick(0f, s.head, d, true));
            AlignOutcome o = flow.Outcome;
            Assert.IsTrue(o.Success, o.Failure);
            Assert.IsTrue(o.Reanchor);
            // Core's Reanchor turns the neck about the head by the smallest rotation that puts the nut on the new ray: the nut is
            // exact; a yaw about gravity also rolls the far end a little (about 3 x sin(nut elevation) degrees), which a
            // one-glance re-anchor cannot see.
            for (int i = 0; i < 4; i++)
            {
                Vector3 drawn = AlignMath.NeckToWorld(o.Pose, false, AlignMath.NeckLocalMetres(flow.Solver.ModelPoint((AlignTarget)i), H));
                float errorMm = (drawn - TargetWorld(moved, flow.Solver, (AlignTarget)i)).magnitude * 1000f;
                Console.WriteLine("re-anchor after a 3 degree yaw: " + (AlignTarget)i + " off by " + errorMm.ToString("0.0") + " mm");
                Assert.Less(errorMm, i == 0 ? 1f : 15f, ((AlignTarget)i).ToString());
            }
            Assert.Greater(o.NutShiftMm, 20f, "3 degrees at 0.6 m is about 31 mm");
        }

        [Test]
        public void RotateAbout_KeepsTheNeckWhereTheHeadSeesIt()
        {
            // A recenter turns the virtual world by a yaw about the head: the aligned pose must turn with it.
            Scene s = Scenes()[1];
            Quaternion headBefore = NeckPlacement.RotationFromAxes(new Vector3(0.9f, 0f, 0.4f), Vector3.up);
            Quaternion delta = AlignMath.AxisAngle(Vector3.up, -25f);
            Quaternion headAfter = AlignMath.Normalized(delta * headBefore);
            AlignedPose after = AlignMath.RotateAbout(s.truth, s.head, delta);
            Vector3 seenBefore = AlignMath.Conjugate(headBefore) * (s.truth.nut - s.head);
            Vector3 seenAfter = AlignMath.Conjugate(headAfter) * (after.nut - s.head);
            Assert.Less((seenBefore - seenAfter).magnitude, 1e-5f);
        }

        // ------------------------------------------------------------------ the anchoring policy (state x anchorMode x aligned)

        [Test]
        public void Policy_EveryCombination()
        {
            // headLockedMode, tracked, aligned -> placement
            Assert.AreEqual(NeckAnchorPlacement.FollowHead, NeckAnchorPolicy.Decide(true, true, true));
            Assert.AreEqual(NeckAnchorPlacement.FollowHead, NeckAnchorPolicy.Decide(true, true, false));
            Assert.AreEqual(NeckAnchorPlacement.FollowHead, NeckAnchorPolicy.Decide(true, false, true));
            Assert.AreEqual(NeckAnchorPlacement.FollowHead, NeckAnchorPolicy.Decide(true, false, false));
            Assert.AreEqual(NeckAnchorPlacement.FollowHead, NeckAnchorPolicy.Decide(false, false, true), "tracking lost: follow the head, keep the alignment");
            Assert.AreEqual(NeckAnchorPlacement.FollowHead, NeckAnchorPolicy.Decide(false, false, false));
            Assert.AreEqual(NeckAnchorPlacement.Aligned, NeckAnchorPolicy.Decide(false, true, true));
            Assert.AreEqual(NeckAnchorPlacement.World, NeckAnchorPolicy.Decide(false, true, false));
        }

        [Test]
        public void Policy_TrackedMeansFreshImuOrASimulatedHead()
        {
            Assert.IsTrue(NeckAnchorPolicy.IsTracked(true, HeadTrackingState.Tracking, false));
            Assert.IsFalse(NeckAnchorPolicy.IsTracked(true, HeadTrackingState.HeadLocked, false));
            Assert.IsTrue(NeckAnchorPolicy.IsTracked(true, HeadTrackingState.HeadLocked, true), "the editor's mouse-look");
            Assert.IsTrue(NeckAnchorPolicy.IsTracked(false, HeadTrackingState.HeadLocked, false), "no ImuHeadPose at all (previews)");
        }

        [Test]
        public void Policy_HeadLockedModeIsCaseInsensitive_LikeImuHeadPoseCore()
        {
            Assert.IsTrue(NeckAnchorPolicy.IsHeadLockedMode("headLocked"));
            Assert.IsTrue(NeckAnchorPolicy.IsHeadLockedMode("HEADLOCKED"));
            Assert.IsFalse(NeckAnchorPolicy.IsHeadLockedMode("aligned"));
            Assert.IsFalse(NeckAnchorPolicy.IsHeadLockedMode(null));
        }

        // ------------------------------------------------------------------ HUD layout vs the neck (relational, from the estimates)

        static Quaternion Look(Vector3 direction)
        {
            Vector3 f = direction.normalized;
            Vector3 x = Vector3.Cross(Vector3.up, f).normalized;
            return NeckPlacement.RotationFromAxes(x, Vector3.Cross(f, x));
        }

        static bool Tangent(Vector3 head, Quaternion look, Vector3 point, out Vector2 t)
        {
            Vector3 v = AlignMath.Conjugate(look) * (point - head);
            t = default(Vector2);
            if (v.z <= 1e-4f) return false;
            t = new Vector2(v.x / v.z, v.y / v.z);
            return true;
        }

        /// <summary>The real neck as seen: both outer strings from the X / O row (30 mm beyond the nut) to fret 12, the nut, the targets.</summary>
        static void NeckFeatures(Scene s, NeckSolver solver, System.Collections.Generic.List<Vector3> points, System.Collections.Generic.List<Vector3> targets)
        {
            var g = CoreNeckLayout.DefaultGeometry;
            for (float along = -30f; along <= (float)g.FretDistanceMm(12); along += 5f)
                foreach (int stringNumber in new[] { 6, 1 })
                    points.Add(AlignMath.NeckToWorld(s.truth, s.leftHanded, AlignMath.NeckLocalMetres(new UntitledGuitarApp.Core.Theory.NeckPoint(along, g.StringAcrossMm(stringNumber, along)), H)));
            for (int i = 0; i < NeckSolver.TargetCount; i++) targets.Add(TargetWorld(s, solver, (AlignTarget)i));
        }

        /// <summary>Every overlap between the HUD block (worst-case line lengths) and the neck, the targets or the reticle.</summary>
        static System.Collections.Generic.List<string> HudOverlaps(Scene s, Quaternion look, string frame, Rect? planted)
        {
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            var points = new System.Collections.Generic.List<Vector3>();
            var targets = new System.Collections.Generic.List<Vector3>();
            NeckFeatures(s, solver, points, targets);
            var rects = new System.Collections.Generic.List<Rect>();
            for (int line = 0; line < NeckAligner.HudLineCount; line++)
                rects.Add(NeckAligner.EstimatedLineRect(line, line == 0 ? NeckAligner.LineAMaxChars : NeckAligner.LineMaxChars, s.leftHanded));
            if (planted.HasValue) rects.Add(planted.Value);

            float parallax = Mathf.Tan(2.0f * Mathf.Deg2Rad);    // 1.5 deg eye parallax (text at 1.2 m, neck at ~0.6 m) + 0.5 clearance
            float vertical = Mathf.Tan(0.5f * Mathf.Deg2Rad);
            float targetExtra = Mathf.Tan(1.0f * Mathf.Deg2Rad);
            var problems = new System.Collections.Generic.List<string>();
            for (int r = 0; r < rects.Count; r++)
            {
                Rect near = new Rect(rects[r].xMin - parallax, rects[r].yMin - vertical, rects[r].width + 2f * parallax, rects[r].height + 2f * vertical);
                Rect nearTarget = new Rect(near.xMin - targetExtra, near.yMin - targetExtra, near.width + 2f * targetExtra, near.height + 2f * targetExtra);
                Vector2 t;
                foreach (Vector3 p in points)
                    if (Tangent(s.head, look, p, out t) && near.Contains(t)) { problems.Add(frame + ": line " + r + " covers the neck"); break; }
                for (int i = 0; i < targets.Count; i++)
                    if (Tangent(s.head, look, targets[i], out t) && nearTarget.Contains(t)) problems.Add(frame + ": line " + r + " covers target " + (AlignTarget)i);
                Rect reticle = new Rect(near.xMin - Mathf.Tan(2f * Mathf.Deg2Rad), near.yMin - Mathf.Tan(2f * Mathf.Deg2Rad), near.width + 2f * Mathf.Tan(2f * Mathf.Deg2Rad), near.height + 2f * Mathf.Tan(2f * Mathf.Deg2Rad));
                if (reticle.Contains(Vector2.zero)) problems.Add(frame + ": line " + r + " covers the reticle");
            }
            return problems;
        }

        [Test]
        public void HudBlock_ClearsTheNeckTargetsAndReticle_InEverySceneStepAndResult()
        {
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            var all = new System.Collections.Generic.List<string>();
            foreach (Scene s in Scenes())
            {
                for (int i = 0; i < NeckSolver.TargetCount; i++)
                    all.AddRange(HudOverlaps(s, Look(TargetWorld(s, solver, (AlignTarget)i) - s.head), s.name + " step " + (i + 1), null));
                Vector3 middle = 0.5f * (TargetWorld(s, solver, AlignTarget.NutLowE) + TargetWorld(s, solver, AlignTarget.Fret5LowE));
                all.AddRange(HudOverlaps(s, Look(middle - s.head), s.name + " result", null));
            }
            foreach (string p in all) Console.WriteLine("HUD overlap: " + p);
            Assert.IsEmpty(all);
        }

        [Test]
        public void HudBlock_Check_FiresOnAPlantedOverlap()
        {
            // Planted: a text line laid over the nut must be reported (proves the check can fail).
            Scene s = Scenes()[0];
            var solver = new NeckSolver(CoreNeckLayout.DefaultGeometry, NeckSolverConfig.Default);
            Vector3 middle = 0.5f * (TargetWorld(s, solver, AlignTarget.NutLowE) + TargetWorld(s, solver, AlignTarget.Fret5LowE));
            Quaternion look = Look(middle - s.head);
            Vector2 nut;
            Assert.IsTrue(Tangent(s.head, look, TargetWorld(s, solver, AlignTarget.NutLowE), out nut));
            var planted = new Rect(nut.x - 0.1f, nut.y - 0.01f, 0.2f, 0.02f);
            var problems = HudOverlaps(s, look, "planted", planted);
            Assert.IsTrue(problems.Exists(p => p.Contains("line " + NeckAligner.HudLineCount + " covers target NutLowE")), string.Join(" | ", problems));
            Assert.IsTrue(problems.Exists(p => p.Contains("line " + NeckAligner.HudLineCount + " covers the neck")));
        }

        [Test]
        public void HudStrings_FitTheirLines()
        {
            foreach (string a in NeckAligner.StepLineA) Assert.LessOrEqual(a.Length, NeckAligner.LineAMaxChars, a);
            Assert.LessOrEqual(NeckAligner.ReanchorLineA.Length, NeckAligner.LineAMaxChars);
            foreach (string b in NeckAligner.StepLineB) Assert.LessOrEqual(b.Length, NeckAligner.LineMaxChars, b);
            Assert.LessOrEqual(NeckAligner.HoldHint.Length, NeckAligner.LineMaxChars);
            // Results, worst-case numbers (built the same way as NeckAligner.ShowResult).
            Assert.LessOrEqual(("fit 15 mm · nut 90 cm").Length, NeckAligner.LineMaxChars);
            Assert.LessOrEqual(("Off the nut? Press Z").Length, NeckAligner.LineMaxChars);
            Assert.LessOrEqual(("nut moved 999 mm").Length, NeckAligner.LineMaxChars);
            Assert.LessOrEqual(("Space: try again").Length, NeckAligner.LineMaxChars);
            // Failure reasons from the flow's gates and Core's own failure.
            Scene s = Scenes()[0];
            var reasons = new System.Collections.Generic.List<string>
            {
                RunWithGlances(s, NutsSwapped, 0f, 0).ShortReason,
                RunWithGlances(s, new[] { AlignTarget.NutLowE, AlignTarget.NutHighE, AlignTarget.Fret12LowE, AlignTarget.Fret5LowE }, 0f, 0).ShortReason,
                RunWithGlances(s, new[] { AlignTarget.NutLowE, AlignTarget.NutLowE, AlignTarget.NutLowE, AlignTarget.NutLowE }, 0f, 0).ShortReason,
            };
            foreach (string reason in reasons)
            {
                string first, second;
                NeckAligner.SplitReason(reason, out first, out second);
                Assert.LessOrEqual(first.Length, NeckAligner.LineMaxChars, reason);
                Assert.LessOrEqual(second.Length, NeckAligner.LineMaxChars, reason);
            }
        }

        // ------------------------------------------------------------------ HUD text helper

        [Test]
        public void SplitInTwo_BreaksLongSentencesNearTheMiddle()
        {
            string a, b;
            NeckAligner.SplitInTwo("short", 34, out a, out b);
            Assert.AreEqual("short", a);
            Assert.AreEqual(string.Empty, b);
            NeckAligner.SplitInTwo("best fit residual 22 mm is not the neck - redo the 4 glances", 34, out a, out b);
            Assert.LessOrEqual(a.Length, 34);
            Assert.LessOrEqual(b.Length, 34);
            Assert.AreEqual("best fit residual 22 mm is not the neck - redo the 4 glances", a + " " + b);
        }

        // ------------------------------------------------------------------ engine (only Unity runs these)

        [Test]
        public void FloatingNeck_FlagChange_KeepsAWorldAnchoredNeckWhereItIs_Engine()
        {
            var head = new GameObject("TestHead");
            FloatingNeck neck = FloatingNeck.Create(null);
            try
            {
                neck.UseOptions(new NeckOptions());
                Assert.IsTrue(neck.Build(CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry)));
                head.transform.rotation = AlignMath.AxisAngle(Vector3.right, 30f);   // looking 30 degrees down
                neck.PlaceFloating(head.transform, false);
                Vector3 position = neck.transform.position;
                Quaternion rotation = neck.transform.rotation;

                head.transform.rotation = Quaternion.identity;                    // tracking lost: the head snaps to identity
                neck.UseOptions(new NeckOptions { debugOverlay = true });         // a flag change re-places the neck
                Assert.Less((neck.transform.position - position).magnitude, 1e-5f, "the neck must not jump to the head's new pose");
                Assert.Greater(Mathf.Abs(Quaternion.Dot(neck.transform.rotation, rotation)), 0.99999f);
                Assert.IsFalse(neck.FollowsHead);

                neck.UseOptions(new NeckOptions { leftHanded = true });           // mirroring is still re-applied
                Assert.Less(neck.transform.localScale.z, 0f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(neck.gameObject);
                UnityEngine.Object.DestroyImmediate(head);
            }
        }

        [Test]
        public void NeckAnchor_PlacesWorld_ThenAligned_ThenFollowsTheHeadWhenTrackingIsLost_Engine()
        {
            var head = new GameObject("TestHead");
            FloatingNeck neck = FloatingNeck.Create(null);
            NeckAnchor anchor = null;
            try
            {
                neck.UseOptions(new NeckOptions());
                neck.Build(CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry));
                anchor = NeckAnchor.Create(null, neck, head.transform, null);     // no ImuHeadPose: tracked
                Assert.AreEqual(NeckAnchorPlacement.World, anchor.Placement);
                Assert.IsFalse(neck.FollowsHead);

                var pose = new AlignedPose(new Vector3(-0.4f, -0.3f, 0.3f), NeckPlacement.RotationFromAxes(new Vector3(1f, -0.2f, -0.3f), new Vector3(0f, 0.7f, 0.7f)));
                anchor.SetAlignment(pose);
                Assert.AreEqual(NeckAnchorPlacement.Aligned, anchor.Placement);
                Assert.AreEqual(NeckMode.Aligned, neck.Mode);
                Assert.Less((neck.transform.position - pose.nut).magnitude, 1e-5f);

                anchor.SimulatedHead = false;
                var imu = head.AddComponent<ImuHeadPose>();   // never enabled in Edit Mode: State is HeadLocked
                UnityEngine.Object.DestroyImmediate(anchor.gameObject);
                anchor = NeckAnchor.Create(null, neck, head.transform, imu);
                Assert.AreEqual(NeckAnchorPlacement.FollowHead, anchor.Placement);
                Assert.IsTrue(neck.FollowsHead);
            }
            finally
            {
                if (anchor != null) UnityEngine.Object.DestroyImmediate(anchor.gameObject);
                if (neck != null) UnityEngine.Object.DestroyImmediate(neck.gameObject);
                UnityEngine.Object.DestroyImmediate(head);
            }
        }
    }
}
