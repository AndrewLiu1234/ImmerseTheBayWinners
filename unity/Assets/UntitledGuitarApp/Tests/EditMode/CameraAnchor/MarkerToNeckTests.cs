// MarkerToNeck tests (SL12): pure math, runs offline (kit/mono-tests.sh) and in EditMode.
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.CameraAnchor;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Tests.EditMode.CameraAnchor
{
    public class MarkerToNeckTests
    {
        static CameraAnchorFlags Flags()
        {
            return new CameraAnchorFlags
            {
                camToHeadX = 0f, camToHeadY = 0f, camToHeadZ = 0f,
                markerToNutX = 0f, markerToNutY = 0f, markerToNutZ = 0f,
                markerToNeckDegX = 0f, markerToNeckDegY = 0f, markerToNeckDegZ = 0f,
            };
        }

        [Test]
        public void IdentityCase_PutsTheNeckOnTheMarker()
        {
            // head at origin, identity rotation, zero offsets: the neck pose IS the marker pose with y flipped
            var marker = new CameraPose { Pos = new Vector3(0f, -0.5f, 1f), Rot = Quaternion.identity };
            var aligned = MarkerToNeck.ToAlignedPose(marker, Vector3.zero, Quaternion.identity, Flags());
            Assert.AreEqual(0f, aligned.nut.x, 1e-4f);
            Assert.AreEqual(0.5f, aligned.nut.y, 1e-4f, "cv2 +y (down) becomes Unity +y (up)");
            Assert.AreEqual(1f, aligned.nut.z, 1e-4f);
        }

        [Test]
        public void HeadRotationRotatesTheMarkerPose()
        {
            var marker = new CameraPose { Pos = new Vector3(0f, 0f, 1f), Rot = Quaternion.identity };
            Quaternion headRot = Quaternion.Euler(0f, 90f, 0f);   // yaw +90: Unity maps forward (+z) to +x
            var aligned = MarkerToNeck.ToAlignedPose(marker, Vector3.zero, headRot, Flags());
            Assert.AreEqual(1f, aligned.nut.x, 1e-4f, "forward rotated by yaw +90 lands on +x");
            Assert.AreEqual(0f, aligned.nut.y, 1e-4f);
            Assert.AreEqual(0f, aligned.nut.z, 1e-4f);
        }

        [Test]
        public void HeadPositionOffsetsTheMarker()
        {
            var marker = new CameraPose { Pos = Vector3.zero, Rot = Quaternion.identity };
            var aligned = MarkerToNeck.ToAlignedPose(marker, new Vector3(1f, 2f, 3f), Quaternion.identity, Flags());
            Assert.AreEqual(1f, aligned.nut.x, 1e-4f);
            Assert.AreEqual(2f, aligned.nut.y, 1e-4f);
            Assert.AreEqual(3f, aligned.nut.z, 1e-4f);
        }

        [Test]
        public void MarkerOffsetMovesTheNutTowardTheBridge()
        {
            var f = Flags();
            f.markerToNutX = 300f;   // the marker sits 300 mm from the nut, along the neck toward the bridge
            var marker = new CameraPose { Pos = new Vector3(0f, 0f, 1f), Rot = Quaternion.identity };
            var aligned = MarkerToNeck.ToAlignedPose(marker, Vector3.zero, Quaternion.identity, f);
            Assert.AreEqual(0.3f, aligned.nut.x, 1e-4f, "nut is 300 mm back from the marker along +x");
            Assert.AreEqual(0f, aligned.nut.y, 1e-4f);
            Assert.AreEqual(1f, aligned.nut.z, 1e-4f);
        }

        [Test]
        public void MarkerFaceNormalBecomesTheNeckUp()
        {
            // the printed marker lies flat on the guitar face: the neck's +y (up out of the fretboard) must point
            // along the marker's face normal (its printed +z). Identity marker, no head rotation: that is world +z.
            var f = Flags();
            f.markerToNeckDegX = 90f;   // marker frame -> neck frame: face normal (+z) becomes +y
            var marker = new CameraPose { Pos = Vector3.zero, Rot = Quaternion.identity };
            var aligned = MarkerToNeck.ToAlignedPose(marker, Vector3.zero, Quaternion.identity, f);
            Vector3 up = aligned.rotation * Vector3.up;          // the neck frame's +y in the world
            Assert.AreEqual(0f, up.x, 1e-4f);
            Assert.AreEqual(0f, up.y, 1e-4f);
            Assert.AreEqual(1f, up.z, 1e-4f, "neck up points along the marker face normal (world +z here)");
        }

        [Test]
        public void CameraPoseRotationIsFlippedThroughTheReflection()
        {
            // q_cv2 = 90 deg about the cv2 z axis: R = [[0,-1,0],[1,0,0],[0,0,1]].
            // B R B (negate the y row and column) = [[0,1,0],[-1,0,0],[0,0,1]] = -90 deg about z.
            var marker = new CameraPose { Pos = Vector3.zero, Rot = Quaternion.Euler(0f, 0f, 90f) };
            Quaternion expected = Quaternion.Euler(0f, 0f, -90f);
            var aligned = MarkerToNeck.ToAlignedPose(marker, Vector3.zero, Quaternion.identity, Flags());
            Assert.AreEqual(expected.x, aligned.rotation.x, 1e-4f);
            Assert.AreEqual(expected.y, aligned.rotation.y, 1e-4f);
            Assert.AreEqual(expected.z, aligned.rotation.z, 1e-4f);
            Assert.AreEqual(expected.w, aligned.rotation.w, 1e-4f);
        }
    }
}
