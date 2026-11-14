// EditMode tests for the ghost hand's pure maths: FingerIk (one finger), CapsuleHandSolver (the whole fretting hand) and
// the finger skin geometry. No scene, no GameObject, no Play Mode, no device.
//
// The slice's acceptance criterion is "each fingertip lands within 3 mm of its dot". The tests assert that 3 mm bound, and
// also the much tighter 0.1 mm the solver actually achieves, so a regression shows up long before it becomes visible.
// Fret and string positions here are literal numbers for a 648 mm scale neck in the pinned neck frame (origin at the nut on
// the fretboard, +x toward the bridge, +y out of the fretboard, +z toward string 1); the app gets them from Core.
using System;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.GhostHand;

namespace UntitledGuitarApp.Tests.EditMode
{
    public class FingerIkTests
    {
        const float Acceptance = 0.003f;        // 3 mm: the slice's acceptance criterion
        const float Tight = 0.0001f;            // 0.1 mm: what the solver delivers (FingerIk.ReachTolerance)

        // ------------------------------------------------------------------------------------------------ helpers

        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }

        static bool Finite(FingerIkResult r)
        {
            return Finite(r.knuckle) && Finite(r.pip) && Finite(r.dip) && Finite(r.tip) &&
                   Finite(r.swing) && Finite(r.mcp) && Finite(r.pipFlexion) && Finite(r.dipFlexion) && Finite(r.error);
        }

        static float Distance(Vector3 a, Vector3 b)
        {
            double dx = (double)a.x - b.x, dy = (double)a.y - b.y, dz = (double)a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static Vector3 MirrorX(Vector3 v) { return new Vector3(-v.x, v.y, v.z); }
        static Vector3 MirrorZ(Vector3 v) { return new Vector3(v.x, v.y, -v.z); }

        static string Text(Vector3 v) { return "(" + v.x.ToString("F5") + ", " + v.y.ToString("F5") + ", " + v.z.ToString("F5") + ")"; }

        // a finger in a tilted frame (not axis-aligned), so nothing passes by accident of alignment
        static FingerIkChain TiltedChain()
        {
            FingerIkChain c;
            c.knuckle = new Vector3(-0.03f, 0.05f, 0.02f);
            c.forward = new Vector3(0.3f, 0.5f, -0.8f);
            c.curl = new Vector3(0.7f, -0.2f, 0.4f);
            c.length1 = 0.045f; c.length2 = 0.027f; c.length3 = 0.020f;
            c.limits = FingerIkLimits.Finger;
            return c;
        }

        // literal neck: 648 mm scale, 35 mm E-to-E at the nut, 53.5 mm at the saddle
        const float ScaleLength = 0.648f;

        static Vector3 Dot(int stringNumber, int fret)
        {
            double d0 = ScaleLength * (1.0 - Math.Pow(2.0, -(fret - 1) / 12.0));
            double d1 = ScaleLength * (1.0 - Math.Pow(2.0, -fret / 12.0));
            float x = (float)(d0 + 0.75 * (d1 - d0));
            float eToE = 0.035f + (0.0535f - 0.035f) * (x / ScaleLength);
            return new Vector3(x, 0f, (3.5f - stringNumber) * eToE / 5f);
        }

        static readonly Vector3 Unused = new Vector3(float.NaN, 0f, 0f);

        // fingers 1-4 of the five MUST chords (string, fret), from the chord table in MASTER_PLAN Appendix B2
        static Vector3[] Chord(string name)
        {
            switch (name)
            {
                case "G": return new[] { Dot(5, 2), Dot(6, 3), Dot(1, 3), Unused };
                case "C": return new[] { Dot(2, 1), Dot(4, 2), Dot(5, 3), Unused };
                case "D": return new[] { Dot(3, 2), Dot(1, 2), Dot(2, 3), Unused };
                case "Em": return new[] { Unused, Dot(5, 2), Dot(4, 2), Unused };
                case "Am": return new[] { Dot(2, 1), Dot(4, 2), Dot(3, 2), Unused };
                default: throw new ArgumentException(name);
            }
        }

        // ------------------------------------------------------------------------------------------------ FingerIk

        [Test]
        public void ReachableGrid_TipLandsOnTarget_ForEveryFingerOfBothHands()
        {
            int solved = 0;
            float worst = 0f;
            foreach (bool left in new[] { true, false })
            {
                CapsuleHandDimensions dims = CapsuleHandDimensions.CreateDefault(left);
                for (int f = 0; f < CapsuleHandDimensions.FingerCount; f++)
                {
                    FingerIkChain chain = dims.chains[f];
                    FingerIkLimits lim = chain.limits;
                    for (int i = 0; i <= 6; i++)
                        for (int j = 0; j <= 8; j++)
                            for (int k = 0; k <= 8; k++)
                            {
                                float swing = Mathf.Lerp(-lim.swingMax, lim.swingMax, i / 6f);
                                float mcp = Mathf.Lerp(lim.mcpMin, lim.mcpMax, j / 8f);
                                float pip = Mathf.Lerp(0f, lim.pipMax, k / 8f);
                                FingerIkResult fk, ik;
                                FingerIk.Pose(chain, swing, mcp, pip, out fk);      // a pose inside the limits: its tip is reachable
                                FingerIk.Solve(chain, fk.tip, out ik);
                                float miss = Distance(ik.tip, fk.tip);
                                worst = Mathf.Max(worst, miss);
                                Assert.IsTrue(Finite(ik), "non-finite result, finger " + f + " target " + Text(fk.tip));
                                Assert.LessOrEqual(miss, Acceptance, "finger " + f + " misses its target by more than 3 mm at " + Text(fk.tip));
                                Assert.LessOrEqual(miss, Tight, "finger " + f + " misses a reachable target by more than 0.1 mm at " + Text(fk.tip));
                                Assert.IsTrue(ik.reachable, "a reachable target was reported unreachable, finger " + f);
                                solved++;
                            }
                }
            }
            Assert.AreEqual(2 * 5 * 7 * 9 * 9, solved);
            Assert.LessOrEqual(worst, Tight);
        }

        [Test]
        public void ReachableGrid_TiltedFrame_JointsKeepTheirBoneLengths()
        {
            FingerIkChain chain = TiltedChain();
            FingerIkLimits lim = chain.limits;
            for (int i = 0; i <= 8; i++)
                for (int j = 0; j <= 10; j++)
                    for (int k = 0; k <= 10; k++)
                    {
                        FingerIkResult fk, ik;
                        FingerIk.Pose(chain, Mathf.Lerp(-lim.swingMax, lim.swingMax, i / 8f), Mathf.Lerp(lim.mcpMin, lim.mcpMax, j / 10f), Mathf.Lerp(0f, lim.pipMax, k / 10f), out fk);
                        FingerIk.Solve(chain, fk.tip, out ik);
                        Assert.LessOrEqual(Distance(ik.tip, fk.tip), Tight);
                        Assert.AreEqual(chain.length1, Distance(ik.knuckle, ik.pip), 1e-5f);
                        Assert.AreEqual(chain.length2, Distance(ik.pip, ik.dip), 1e-5f);
                        Assert.AreEqual(chain.length3, Distance(ik.dip, ik.tip), 1e-5f);
                        Assert.AreEqual(ik.pipFlexion * lim.dipCoupling, ik.dipFlexion, 1e-5f, "the last joint must follow the middle joint");
                    }
        }

        [Test]
        public void UnreachableTargets_ClampToAPoseInsideTheLimits_WithoutNaN()
        {
            FingerIkChain chain = TiltedChain();
            FingerIkLimits lim = chain.limits;
            float reach = FingerIk.MaxReach(chain);
            System.Random random = new System.Random(11);
            int unreachable = 0;
            for (int n = 0; n < 600; n++)
            {
                float box = n < 300 ? 0.30f : 3.0f;       // around the knuckle, and far away
                Vector3 target = chain.knuckle + new Vector3((float)(random.NextDouble() - 0.5) * box, (float)(random.NextDouble() - 0.5) * box, (float)(random.NextDouble() - 0.5) * box);
                FingerIkResult r;
                FingerIk.Solve(chain, target, out r);
                Assert.IsTrue(Finite(r), "non-finite result for target " + Text(target));
                Assert.LessOrEqual(Distance(r.tip, chain.knuckle), reach + 1e-5f, "the fingertip left the finger's reach");
                Assert.AreEqual(Distance(r.tip, target), r.error, 1e-5f, "error must be the distance from tip to target");
                Assert.That(r.swing, Is.InRange(-lim.swingMax - 1e-4f, lim.swingMax + 1e-4f));
                Assert.That(r.mcp, Is.InRange(lim.mcpMin - 1e-4f, lim.mcpMax + 1e-4f));
                Assert.That(r.pipFlexion, Is.InRange(-1e-4f, lim.pipMax + 1e-4f));
                Assert.AreEqual(r.error <= FingerIk.ReachTolerance, r.reachable);
                if (!r.reachable) unreachable++;
            }
            Assert.Greater(unreachable, 300, "the random boxes were meant to be mostly out of reach");
        }

        [Test]
        public void UnreachableTargets_EndAtTheNearestReachablePoint()
        {
            // Brute force: the tips of a dense grid of poses inside the limits. The solver must never be farther from an
            // unreachable target than the best of them (it may be closer: the grid is coarse).
            FingerIkChain chain = TiltedChain();
            FingerIkLimits lim = chain.limits;
            const int A = 16, B = 32, C = 32;
            Vector3[] tips = new Vector3[(A + 1) * (B + 1) * (C + 1)];
            int count = 0;
            for (int i = 0; i <= A; i++)
                for (int j = 0; j <= B; j++)
                    for (int k = 0; k <= C; k++)
                    {
                        FingerIkResult fk;
                        FingerIk.Pose(chain, Mathf.Lerp(-lim.swingMax, lim.swingMax, i / (float)A), Mathf.Lerp(lim.mcpMin, lim.mcpMax, j / (float)B), Mathf.Lerp(0f, lim.pipMax, k / (float)C), out fk);
                        tips[count++] = fk.tip;
                    }

            System.Random random = new System.Random(5);
            for (int n = 0; n < 60; n++)
            {
                Vector3 target = chain.knuckle + new Vector3((float)(random.NextDouble() - 0.5) * 0.24f, (float)(random.NextDouble() - 0.5) * 0.24f, (float)(random.NextDouble() - 0.5) * 0.24f);
                FingerIkResult r;
                FingerIk.Solve(chain, target, out r);
                float best = float.MaxValue;
                for (int q = 0; q < count; q++) best = Mathf.Min(best, Distance(tips[q], target));
                Assert.LessOrEqual(r.error, best + 1e-4f, "a closer reachable pose exists for target " + Text(target));
            }
        }

        [Test]
        public void StraightAhead_BeyondReach_GivesAStraightFingerPointingAtTheTarget()
        {
            FingerIkChain chain = TiltedChain();
            Vector3 forward = chain.forward / chain.forward.magnitude;
            FingerIkResult r;
            FingerIk.Solve(chain, chain.knuckle + forward * 5f, out r);
            Assert.IsFalse(r.reachable);
            Assert.AreEqual(0f, r.pipFlexion, 1e-5f);
            Assert.AreEqual(0f, r.mcp, 1e-4f);
            Assert.LessOrEqual(Distance(r.tip, chain.knuckle + forward * FingerIk.MaxReach(chain)), 1e-5f);
        }

        [Test]
        public void DegenerateInput_NeverProducesNaN()
        {
            FingerIkChain chain = TiltedChain();
            Vector3[] targets =
            {
                chain.knuckle,                                              // on the first joint
                chain.knuckle + new Vector3(1e-9f, 0f, 0f),
                new Vector3(float.NaN, 0f, 0f),
                new Vector3(float.PositiveInfinity, 1f, 1f),
                new Vector3(0f, float.NegativeInfinity, 0f),
                chain.knuckle + new Vector3(0f, 1000f, 0f),
            };
            foreach (Vector3 target in targets)
            {
                FingerIkResult r;
                FingerIk.Solve(chain, target, out r);
                Assert.IsTrue(Finite(r), "non-finite result for target " + Text(target));
            }

            FingerIkResult bad;
            FingerIk.Solve(chain, new Vector3(float.NaN, 0f, 0f), out bad);
            Assert.AreEqual(float.MaxValue, bad.error, "a non-finite target reports the maximum error");
            Assert.IsFalse(bad.reachable);

            FingerIkChain zeroFrame = chain; zeroFrame.forward = Vector3.zero; zeroFrame.curl = Vector3.zero;
            FingerIkChain parallel = chain; parallel.curl = chain.forward;
            FingerIkChain badLengths = chain; badLengths.length1 = 0f; badLengths.length2 = -1f; badLengths.length3 = float.NaN;
            FingerIkChain badLimits = chain; badLimits.limits.mcpMax = float.NaN;
            foreach (FingerIkChain c in new[] { zeroFrame, parallel, badLengths, badLimits })
            {
                FingerIkResult r;
                FingerIk.Solve(c, chain.knuckle + new Vector3(0f, 0.03f, 0.05f), out r);
                Assert.IsTrue(Finite(r));
                FingerIk.Pose(c, 0.1f, 0.5f, 0.7f, out r);
                Assert.IsTrue(Finite(r));
            }
        }

        [Test]
        public void SameInput_GivesBitIdenticalOutput()
        {
            FingerIkChain chain = TiltedChain();
            Vector3 target = chain.knuckle + new Vector3(0.021f, -0.034f, 0.017f);
            FingerIkResult a, b;
            FingerIk.Solve(chain, target, out a);
            FingerIk.Solve(chain, target, out b);
            Assert.IsTrue(a.tip.x == b.tip.x && a.tip.y == b.tip.y && a.tip.z == b.tip.z);
            Assert.IsTrue(a.swing == b.swing && a.mcp == b.mcp && a.pipFlexion == b.pipFlexion && a.dipFlexion == b.dipFlexion && a.error == b.error);
        }

        [Test]
        public void LeftAndRightFingers_AreMirrorImages()
        {
            CapsuleHandDimensions left = CapsuleHandDimensions.CreateDefault(true);
            CapsuleHandDimensions right = CapsuleHandDimensions.CreateDefault(false);
            System.Random random = new System.Random(7);
            for (int f = 0; f < CapsuleHandDimensions.FingerCount; f++)
            {
                Assert.LessOrEqual(Distance(MirrorX(left.chains[f].knuckle), right.chains[f].knuckle), 1e-7f);
                for (int n = 0; n < 200; n++)
                {
                    // reachable and unreachable targets alike
                    Vector3 target = right.chains[f].knuckle + new Vector3((float)(random.NextDouble() - 0.5) * 0.2f, (float)(random.NextDouble() - 0.5) * 0.2f, (float)(random.NextDouble() - 0.5) * 0.2f);
                    FingerIkResult r, l;
                    FingerIk.Solve(right.chains[f], target, out r);
                    FingerIk.Solve(left.chains[f], MirrorX(target), out l);
                    Assert.LessOrEqual(Distance(MirrorX(r.pip), l.pip), 1e-6f, "finger " + f);
                    Assert.LessOrEqual(Distance(MirrorX(r.dip), l.dip), 1e-6f, "finger " + f);
                    Assert.LessOrEqual(Distance(MirrorX(r.tip), l.tip), 1e-6f, "finger " + f);
                    Assert.AreEqual(r.mcp, l.mcp, 1e-5f);
                    Assert.AreEqual(r.pipFlexion, l.pipFlexion, 1e-5f);
                    Assert.AreEqual(r.swing, -l.swing, 1e-5f, "the sideways swing changes sign in the mirror");
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ the whole hand

        [TestCase("G")]
        [TestCase("C")]
        [TestCase("D")]
        [TestCase("Em")]
        [TestCase("Am")]
        public void FrettingHand_EveryUsedFingertipLandsWithin3mmOfItsDot(string chord)
        {
            Vector3[] targets = Chord(chord);
            CapsuleHandSolver solver = new CapsuleHandSolver(true);
            HandPose pose;
            solver.SolveFretting(targets, out pose);
            FingerIkResult[] fingers = new FingerIkResult[CapsuleHandSolver.FingerCount];
            solver.SolveFingers(pose, fingers);

            Assert.IsTrue(Finite(pose.palmPosition), "palm position");
            for (int f = 0; f < CapsuleHandSolver.FingerCount; f++) Assert.IsTrue(Finite(fingers[f]), "finger " + f);
            for (int f = 0; f < 4; f++)
            {
                bool wanted = !float.IsNaN(targets[f].x);
                Assert.AreEqual(wanted, solver.used[f]);
                if (!wanted) continue;
                float miss = Distance(solver.PadPoint(pose, f, fingers[f].tip), targets[f]);
                Assert.LessOrEqual(miss, Acceptance, chord + ": finger " + (f + 1) + " is " + (miss * 1000f).ToString("F2") + " mm from its dot");
                Assert.LessOrEqual(miss, 0.0005f, chord + ": finger " + (f + 1) + " should be within 0.5 mm, is " + (miss * 1000f).ToString("F2") + " mm");
                Assert.LessOrEqual(solver.reachError[f], Acceptance);
                // the pad hovers above the dot, it never sinks below the fretboard
                Assert.Greater(solver.PalmToParent(pose, fingers[f].tip).y, targets[f].y + solver.dims.TipRadius(f) * 0.9f);
            }
        }

        [Test]
        public void FrettingHand_LeftAndRightAreMirrorImages()
        {
            Vector3[] targets = Chord("G");
            Vector3[] mirrored = new Vector3[4];
            for (int i = 0; i < 4; i++) mirrored[i] = MirrorZ(targets[i]);     // NaN x stays NaN

            CapsuleHandSolver left = new CapsuleHandSolver(true), right = new CapsuleHandSolver(false);
            HandPose lp, rp;
            left.SolveFretting(targets, out lp);
            right.SolveFretting(mirrored, out rp);
            FingerIkResult[] lf = new FingerIkResult[5], rf = new FingerIkResult[5];
            left.SolveFingers(lp, lf);
            right.SolveFingers(rp, rf);

            const float tolerance = 0.0005f;        // the two fits run on mirrored numbers and round differently
            Assert.LessOrEqual(Distance(MirrorZ(lp.palmPosition), rp.palmPosition), tolerance, "wrist");
            for (int f = 0; f < 5; f++)
            {
                Assert.LessOrEqual(Distance(MirrorZ(left.PalmToParent(lp, lf[f].knuckle)), right.PalmToParent(rp, rf[f].knuckle)), tolerance, "knuckle " + f);
                Assert.LessOrEqual(Distance(MirrorZ(left.PalmToParent(lp, lf[f].pip)), right.PalmToParent(rp, rf[f].pip)), tolerance, "second joint " + f);
                Assert.LessOrEqual(Distance(MirrorZ(left.PalmToParent(lp, lf[f].dip)), right.PalmToParent(rp, rf[f].dip)), tolerance, "third joint " + f);
                Assert.LessOrEqual(Distance(MirrorZ(left.PalmToParent(lp, lf[f].tip)), right.PalmToParent(rp, rf[f].tip)), tolerance, "fingertip " + f);
            }
        }

        [Test]
        public void FrettingHand_PalmIsOnTheStringOneSide_AndTheThumbIsBehindTheNeck()
        {
            CapsuleHandSolver solver = new CapsuleHandSolver(true);
            HandPose pose;
            solver.SolveFretting(Chord("G"), out pose);
            FingerIkResult[] fingers = new FingerIkResult[5];
            solver.SolveFingers(pose, fingers);
            Assert.Greater(pose.palmPosition.z, 0.03f, "a left fretting hand's wrist is on the +z (string 1) side of the neck");
            Assert.Less(solver.PalmToParent(pose, fingers[CapsuleHandSolver.ThumbIndex].tip).y, 0f, "the thumb tip is behind the fretboard plane");
            // the index finger is the one nearest the nut
            Assert.Less(solver.PalmToParent(pose, fingers[0].knuckle).x, solver.PalmToParent(pose, fingers[3].knuckle).x);
        }

        [Test]
        public void FrettingHand_NoTargets_GivesAFiniteRestPose_AndSameInputSamePose()
        {
            CapsuleHandSolver solver = new CapsuleHandSolver(true);
            HandPose rest;
            solver.SolveFretting(null, out rest);
            FingerIkResult[] fingers = new FingerIkResult[5];
            solver.SolveFingers(rest, fingers);
            for (int f = 0; f < 5; f++) { Assert.IsTrue(Finite(fingers[f])); Assert.IsFalse(f < 4 && solver.used[f]); }

            CapsuleHandSolver a = new CapsuleHandSolver(true), b = new CapsuleHandSolver(true);
            HandPose pa, pb;
            a.SolveFretting(Chord("C"), out pa);
            b.SolveFretting(Chord("C"), out pb);
            Assert.IsTrue(pa.palmPosition.x == pb.palmPosition.x && pa.palmPosition.y == pb.palmPosition.y && pa.palmPosition.z == pb.palmPosition.z);
            Assert.IsTrue(pa.tip0.x == pb.tip0.x && pa.tip1.y == pb.tip1.y && pa.tip2.z == pb.tip2.z);
        }

        [Test]
        public void HandPoseLerp_StartsAtA_EndsAtB_AndStaysFiniteInBetween()
        {
            CapsuleHandSolver solver = new CapsuleHandSolver(true);
            HandPose g, c, mid;
            solver.SolveFretting(Chord("G"), out g);
            solver.SolveFretting(Chord("C"), out c);
            FingerIkResult[] fingers = new FingerIkResult[5];
            for (int step = 0; step <= 10; step++)
            {
                HandPose.Lerp(g, c, step / 10f, out mid);
                solver.SolveFingers(mid, fingers);
                Assert.IsTrue(Finite(mid.palmPosition));
                float q = mid.palmRotation.x * mid.palmRotation.x + mid.palmRotation.y * mid.palmRotation.y + mid.palmRotation.z * mid.palmRotation.z + mid.palmRotation.w * mid.palmRotation.w;
                Assert.AreEqual(1f, q, 1e-4f, "the blended rotation must stay a unit quaternion");
                for (int f = 0; f < 5; f++) Assert.IsTrue(Finite(fingers[f]));
            }
            HandPose.Lerp(g, c, 0f, out mid);
            Assert.LessOrEqual(Distance(mid.palmPosition, g.palmPosition), 1e-6f);
            Assert.LessOrEqual(Distance(mid.tip1, g.tip1), 1e-6f);
            HandPose.Lerp(g, c, 1f, out mid);
            Assert.LessOrEqual(Distance(mid.palmPosition, c.palmPosition), 1e-6f);
            Assert.LessOrEqual(Distance(mid.tip2, c.tip2), 1e-6f);
        }

        // ------------------------------------------------------------------------------------------------ the finger skin

        [Test]
        public void FingerSkin_FacesOutward_HasUnitNormals_AndStaysInsideTheBudget()
        {
            // The ghost shader culls back faces: a skin wound the wrong way round would be invisible.
            CapsuleHandSolver solver = new CapsuleHandSolver(true);
            HandPose pose;
            solver.SolveFretting(Chord("C"), out pose);
            FingerIkResult[] fingers = new FingerIkResult[5];
            solver.SolveFingers(pose, fingers);
            CapsuleHandDimensions dims = solver.dims;

            int triangles = 0;
            Vector3[] points = new Vector3[5];
            float[] radii = new float[5];
            for (int f = 0; f < 5; f++)
            {
                bool thumb = f == CapsuleHandDimensions.ThumbIndex;
                GhostTubeGeometry tube = new GhostTubeGeometry(thumb ? 4 : 5, 20, 4, 4, 3, thumb ? 0.50f : 0.55f, 0.5f, true);
                FingerIkResult r = fingers[f];
                if (thumb)
                {
                    points[0] = r.knuckle; points[1] = r.pip; points[2] = r.dip; points[3] = r.tip;
                    for (int k = 0; k < 4; k++) radii[k] = dims.Radius(f, k);
                }
                else
                {
                    points[0] = dims.skinStart[f]; points[1] = r.knuckle; points[2] = r.pip; points[3] = r.dip; points[4] = r.tip;
                    radii[0] = dims.skinStartRadius[f];
                    for (int k = 0; k < 4; k++) radii[k + 1] = dims.Radius(f, k);
                }
                tube.Update(points, radii, thumb ? -dims.chains[f].curl : Vector3.up);

                for (int v = 0; v < tube.vertexCount; v++)
                {
                    Assert.IsTrue(Finite(tube.vertices[v]) && Finite(tube.normals[v]), "finger " + f + " vertex " + v);
                    Assert.AreEqual(1f, tube.normals[v].magnitude, 1e-3f, "finger " + f + " normal " + v);
                }
                int facing = 0;
                for (int k = 0; k < tube.triangles.Length; k += 3)
                {
                    Vector3 a = tube.vertices[tube.triangles[k]], b = tube.vertices[tube.triangles[k + 1]], c = tube.vertices[tube.triangles[k + 2]];
                    Vector3 face = Vector3.Cross(b - a, c - a);     // Unity: the clockwise (front) side
                    double area = Math.Sqrt((double)face.x * face.x + (double)face.y * face.y + (double)face.z * face.z);
                    if (area < 1e-10) continue;                     // collapsed on the inside of a bend: never drawn
                    Vector3 n = tube.normals[tube.triangles[k]] + tube.normals[tube.triangles[k + 1]] + tube.normals[tube.triangles[k + 2]];
                    Assert.Greater(Vector3.Dot(face, n) / area, 1.5, "finger " + f + ": a triangle faces inward");
                    facing++;
                }
                Assert.Greater(facing, tube.triangleCount / 2);
                triangles += tube.triangleCount;
            }

            Vector3[] pv, pn; Color32[] pc; float[] ph; int[] pt;
            GhostPalmGeometry.Build(dims, 28, out pv, out pn, out pc, out ph, out pt);
            for (int k = 0; k < pt.Length; k += 3)
            {
                Vector3 a = pv[pt[k]], b = pv[pt[k + 1]], c = pv[pt[k + 2]];
                Vector3 face = Vector3.Cross(b - a, c - a);
                Vector3 outward = new Vector3(a.x + b.x + c.x, a.y + b.y + c.y, 0f);    // away from the palm's long axis
                Assert.Greater(Vector3.Dot(face, outward), 0f, "a palm triangle faces inward");
            }
            triangles += pt.Length / 3;
            Assert.Less(triangles, 6000, "the hand must stay under about 6,000 triangles");
        }
    }
}
