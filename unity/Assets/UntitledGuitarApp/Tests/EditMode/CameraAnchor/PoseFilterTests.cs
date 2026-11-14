// PoseFilter tests (SL12): pure math, runs offline (kit/mono-tests.sh) and in EditMode.
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.CameraAnchor;

namespace UntitledGuitarApp.Tests.EditMode.CameraAnchor
{
    public class PoseFilterTests
    {
        static CameraPose Pose(float x, float y, float z)
        {
            return new CameraPose { Pos = new Vector3(x, y, z), Rot = Quaternion.identity };
        }

        [Test]
        public void FirstSampleSnaps_ThenSmooths()
        {
            // maxJumpMm 5000: the 2 m move is allowed (the outlier tests below use the real 50 mm threshold)
            var f = new PoseFilter(posAlpha: 0.5f, rotAlpha: 0.5f, maxJumpMm: 5000f, maxJumpDeg: 90f, lostTimeoutS: 1f);
            Assert.IsTrue(f.Feed(Pose(1f, 0f, 0f), 1.0), "first sample accepted");
            CameraPose p;
            Assert.IsTrue(f.TryGetSmoothed(out p), "smoothed available");
            Assert.AreEqual(1f, p.Pos.x, 1e-4f, "first sample snaps, no easing");

            f.Feed(Pose(3f, 0f, 0f), 1.1);
            Assert.IsTrue(f.TryGetSmoothed(out p));
            Assert.AreEqual(2f, p.Pos.x, 1e-4f, "alpha 0.5 halfway between 1 and 3");
        }

        [Test]
        public void OutlierJumpIsRejectedAndOutputUnchanged()
        {
            var f = new PoseFilter(posAlpha: 1f, rotAlpha: 1f, maxJumpMm: 50f, maxJumpDeg: 20f, lostTimeoutS: 1f);
            f.Feed(Pose(0f, 0f, 1f), 1.0);
            bool accepted = f.Feed(Pose(0f, 10f, 1f), 1.1);   // 10 m jump
            Assert.IsFalse(accepted, "outlier rejected");
            Assert.AreEqual(1, f.Rejected, "one rejection counted");
            CameraPose p;
            f.TryGetSmoothed(out p);
            Assert.AreEqual(0f, p.Pos.y, 1e-4f, "output unchanged after a rejected sample");
        }

        [Test]
        public void RotationJumpIsRejected()
        {
            var f = new PoseFilter(posAlpha: 1f, rotAlpha: 1f, maxJumpMm: 50f, maxJumpDeg: 20f, lostTimeoutS: 1f);
            var a = new CameraPose { Pos = Vector3.zero, Rot = Quaternion.identity };
            var b = new CameraPose { Pos = Vector3.zero, Rot = Quaternion.Euler(0f, 90f, 0f) };
            f.Feed(a, 1.0);
            Assert.IsFalse(f.Feed(b, 1.1), "90-degree rotation jump rejected");
            Assert.AreEqual(1, f.Rejected);
        }

        [Test]
        public void SmallMovesAreAccepted()
        {
            var f = new PoseFilter(posAlpha: 0.5f, rotAlpha: 0.5f, maxJumpMm: 50f, maxJumpDeg: 20f, lostTimeoutS: 1f);
            f.Feed(Pose(0f, 0f, 1f), 1.0);
            Assert.IsTrue(f.Feed(Pose(0.01f, 0f, 1f), 1.03), "1 cm move accepted");
            Assert.IsTrue(f.Feed(Pose(0.01f, 0.02f, 1f), 1.06), "another small move accepted");
            Assert.AreEqual(0, f.Rejected);
        }

        [Test]
        public void LostAfterTimeout_RecoversBySnappingOnNextSample()
        {
            var f = new PoseFilter(posAlpha: 0.5f, rotAlpha: 0.5f, maxJumpMm: 100f, maxJumpDeg: 90f, lostTimeoutS: 0.5f);
            f.Feed(Pose(0f, 0f, 1f), 1.0);
            Assert.IsFalse(f.Lost(1.3), "still fresh at 0.3 s");
            Assert.IsTrue(f.Lost(1.6), "lost after the timeout with no feed");
            // the marker comes back somewhere else: snap, don't ease from a stale pose
            f.Feed(Pose(2f, 0f, 1f), 2.0);
            Assert.IsFalse(f.Lost(2.1), "recovered");
            CameraPose p;
            f.TryGetSmoothed(out p);
            Assert.AreEqual(2f, p.Pos.x, 1e-4f, "first sample after a loss snaps");
        }

        [Test]
        public void QuaternionSmoothingStaysUnitLength()
        {
            var f = new PoseFilter(posAlpha: 0.3f, rotAlpha: 0.3f, maxJumpMm: 50f, maxJumpDeg: 20f, lostTimeoutS: 1f);
            var start = new CameraPose { Pos = Vector3.zero, Rot = Quaternion.identity };
            f.Feed(start, 0.0);
            var rnd = new System.Random(7);
            for (int i = 0; i < 500; i++)
            {
                var sample = new CameraPose
                {
                    Pos = Vector3.zero,
                    Rot = Quaternion.Euler((float)rnd.NextDouble() * 0.5f, (float)rnd.NextDouble() * 0.5f, 0f)
                };
                f.Feed(sample, i * 0.033);
            }
            CameraPose p;
            f.TryGetSmoothed(out p);
            float n = p.Rot.x * p.Rot.x + p.Rot.y * p.Rot.y + p.Rot.z * p.Rot.z + p.Rot.w * p.Rot.w;
            Assert.AreEqual(1f, n, 1e-3f, "smoothed quaternion stays unit length");
        }

        [Test]
        public void ZeroAllocationsPerFeed()
        {
            var f = new PoseFilter();
            f.Feed(Pose(0f, 0f, 1f), 0.0);
            long before = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            for (int i = 0; i < 1000; i++) f.Feed(Pose(i * 0.0001f, 0f, 1f), i * 0.033);
            long after = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            Assert.AreEqual(before, after, "no allocations across 1000 feeds");
        }
    }
}
