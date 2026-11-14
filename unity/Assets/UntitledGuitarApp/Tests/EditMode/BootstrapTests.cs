using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime;
using UntitledGuitarApp.Runtime.Neck;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>Bootstrap's pure glue: fingertip dots (neck frame, mm) to the capsule hand's targets (socket space, metres).</summary>
    public sealed class BootstrapTests
    {
        [Test]
        public void FingertipTargets_GChord_MapsEachFingerToItsDotInMetres()
        {
            var dots = new[]
            {
                new FingertipDot(5, 62.1f, -10.5f, 1),   // finger 1: string 5, fret 2
                new FingertipDot(6, 95.0f, -17.5f, 2),   // finger 2: string 6, fret 3
                new FingertipDot(1, 95.0f, 17.5f, 3),    // finger 3: string 1, fret 3
            };
            Vector3[] t = Bootstrap.FingertipTargets(dots, dots.Length, new Vector3[4]);
            Assert.AreEqual(0.0621f, t[0].x, 1e-6f); Assert.AreEqual(0f, t[0].y); Assert.AreEqual(-0.0105f, t[0].z, 1e-6f);
            Assert.AreEqual(0.0950f, t[1].x, 1e-6f); Assert.AreEqual(-0.0175f, t[1].z, 1e-6f);
            Assert.AreEqual(0.0950f, t[2].x, 1e-6f); Assert.AreEqual(0.0175f, t[2].z, 1e-6f);
            Assert.IsTrue(float.IsNaN(t[3].x), "the pinky has no dot in G, so it must be marked unused (NaN x)");
        }

        [Test]
        public void FingertipTargets_IgnoresDotsWithoutAFingerAndBeyondTheCount()
        {
            var dots = new[]
            {
                new FingertipDot(3, 10f, 0f, 0),   // no finger: not a target
                new FingertipDot(2, 20f, 5f, 9),   // finger out of range: ignored
                new FingertipDot(4, 30f, -5f, 2),  // beyond count: ignored
            };
            Vector3[] t = Bootstrap.FingertipTargets(dots, 2, new Vector3[4]);
            for (int f = 0; f < 4; f++)
                Assert.IsTrue(float.IsNaN(t[f].x), "finger " + (f + 1) + " should be unused");
        }

        [Test]
        public void FingertipTargets_ClearsStaleTargetsFromAnEarlierChord()
        {
            var result = new Vector3[4];
            Bootstrap.FingertipTargets(new[] { new FingertipDot(1, 50f, 17.5f, 4) }, 1, result);
            Bootstrap.FingertipTargets(new[] { new FingertipDot(2, 40f, 10.5f, 1) }, 1, result);
            Assert.IsTrue(float.IsNaN(result[3].x), "the pinky's old target must not survive into the next chord");
            Assert.AreEqual(0.040f, result[0].x, 1e-6f);
        }
    }
}
