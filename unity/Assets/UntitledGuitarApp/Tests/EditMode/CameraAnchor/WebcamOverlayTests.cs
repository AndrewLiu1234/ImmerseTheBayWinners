// Webcam overlay tests (webcam AR demo): pure maths, runs offline (kit/mono-tests.sh) and in EditMode.
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Spectator;

namespace UntitledGuitarApp.Tests.EditMode.CameraAnchor
{
    public class WebcamOverlayTests
    {
        [Test]
        public void VerticalFovFromFx_MatchesPinhole()
        {
            // fx=754 px with a 720 px tall image: fovY = 2 * atan(360 / 754) ~= 51.0 deg
            float fov = SpectatorLayout.VerticalFovFromFx(754f, 720);
            Assert.Greater(fov, 50.5f);
            Assert.Less(fov, 51.5f);
        }

        [Test]
        public void VerticalFovFromFx_HigherFx_NarrowerFov()
        {
            float wide = SpectatorLayout.VerticalFovFromFx(500f, 720);
            float narrow = SpectatorLayout.VerticalFovFromFx(1000f, 720);
            Assert.Greater(wide, narrow);
        }

        [Test]
        public void VerticalFovFromFx_BadInputs_ReturnZero()
        {
            Assert.AreEqual(0f, SpectatorLayout.VerticalFovFromFx(0f, 720));
            Assert.AreEqual(0f, SpectatorLayout.VerticalFovFromFx(-100f, 720));
            Assert.AreEqual(0f, SpectatorLayout.VerticalFovFromFx(754f, 0));
        }

        [Test]
        public void HalfExtents_CoverTheFrustum()
        {
            Vector2 h = WebcamBackdrop.HalfExtents(51f, 16f / 9f, 2f);
            float wantHalfH = Mathf.Tan(25.5f * Mathf.Deg2Rad) * 2f;
            Assert.AreEqual(wantHalfH, h.y, 1e-3f);
            Assert.AreEqual(wantHalfH * 16f / 9f, h.x, 1e-3f);
        }

        [Test]
        public void HalfExtents_ScaleLinearlyWithDistance()
        {
            Vector2 near = WebcamBackdrop.HalfExtents(51f, 16f / 9f, 1f);
            Vector2 far = WebcamBackdrop.HalfExtents(51f, 16f / 9f, 4f);
            Assert.AreEqual(near.x * 4f, far.x, 1e-3f);
            Assert.AreEqual(near.y * 4f, far.y, 1e-3f);
        }
    }
}
