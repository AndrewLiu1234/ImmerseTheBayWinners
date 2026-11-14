using System;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>
    /// Tests for Runtime/Render/StereoMath.cs. Pure maths: no scene, no Play Mode, no waiting.
    /// The expected numbers were computed independently (python3, double precision) from the derivation at the top of StereoMath.cs.
    /// </summary>
    public class StereoMathTests
    {
        const float DiagonalDeg = 52f;                   // XREAL Air 2 Ultra
        const float Aspect = 1920f / 1080f;              // one eye image
        const float Near = 0.1f;
        const float Far = 20f;
        const float IpdMm = 63f;

        static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual, float relTol, string what)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    float e = expected[row, col];
                    float a = actual[row, col];
                    float tol = relTol * (1f + Mathf.Abs(e));
                    Assert.AreEqual(e, a, tol, what + ": element [" + row + "," + col + "]");
                }
            }
        }

        // ---- 1. diagonal FOV -> horizontal and vertical FOV ---------------------------------------------------------

        [Test]
        public void Diagonal52At16x9_IsAbout46By27Degrees()
        {
            float h, v;
            StereoMath.DiagonalToHorizontalVertical(DiagonalDeg, Aspect, out h, out v);

            Assert.AreEqual(46.06f, h, 0.01f, "horizontal");        // exact: 46.0603
            Assert.AreEqual(26.90f, v, 0.01f, "vertical");          // exact: 26.8957
            Assert.AreEqual(46f, h, 0.1f, "the '46' of docs/HOW_IT_WORKS.md");
            Assert.AreEqual(27f, v, 0.2f, "the '27' of docs/HOW_IT_WORKS.md");
            Assert.AreEqual(h, StereoMath.HorizontalFovDeg(DiagonalDeg, Aspect), 1e-5f);
            Assert.AreEqual(v, StereoMath.VerticalFovDeg(DiagonalDeg, Aspect), 1e-5f);
        }

        [Test]
        public void SquareImage_HasEqualHorizontalAndVerticalFov()
        {
            float h, v;
            StereoMath.DiagonalToHorizontalVertical(52f, 1f, out h, out v);
            Assert.AreEqual(h, v, 1e-4f);
            Assert.AreEqual(38.0565f, h, 0.01f);                    // 2 * atan(tan(26 deg) / sqrt(2))
        }

        [Test]
        public void FlatImageRelation_TangentsAreInTheAspectRatio()
        {
            // The image is flat, so tan(H/2) / tan(V/2) must equal width / height (not the angles themselves).
            float h, v;
            StereoMath.DiagonalToHorizontalVertical(DiagonalDeg, Aspect, out h, out v);
            double ratio = Math.Tan(0.5 * h * Math.PI / 180.0) / Math.Tan(0.5 * v * Math.PI / 180.0);
            Assert.AreEqual(Aspect, (float)ratio, 1e-4f);
            // and angles do not add like Pythagoras: sqrt(46.06^2 + 26.90^2) = 53.3, not 52
            Assert.Greater(Mathf.Abs(DiagonalDeg - Mathf.Sqrt(h * h + v * v)), 1f);
        }

        [TestCase(52f, 1.7778f)]
        [TestCase(52f, 1f)]
        [TestCase(40f, 1.3333f)]
        [TestCase(90f, 2f)]
        [TestCase(70f, 3.5556f)]
        [TestCase(10f, 1.6f)]
        public void Conversion_RoundTripsBackToTheDiagonal(float diagonalDeg, float aspect)
        {
            float h, v;
            StereoMath.DiagonalToHorizontalVertical(diagonalDeg, aspect, out h, out v);
            Assert.AreEqual(diagonalDeg, StereoMath.HorizontalVerticalToDiagonal(h, v), 1e-3f);
        }

        [Test]
        public void WiderAspect_GivesWiderHorizontalAndNarrowerVerticalFov()
        {
            float h169, v169, h219, v219;
            StereoMath.DiagonalToHorizontalVertical(DiagonalDeg, 16f / 9f, out h169, out v169);
            StereoMath.DiagonalToHorizontalVertical(DiagonalDeg, 21f / 9f, out h219, out v219);
            Assert.Greater(h219, h169);
            Assert.Less(v219, v169);
        }

        [Test]
        public void AspectOf_IsWidthOverHeight()
        {
            Assert.AreEqual(16f / 9f, StereoMath.AspectOf(1920, 1080), 1e-6f);
            Assert.AreEqual(32f / 9f, StereoMath.AspectOf(3840, 1080), 1e-6f);
        }

        // ---- 2. eye offsets -----------------------------------------------------------------------------------------

        [Test]
        public void EyeOffsets_AreMinusAndPlusHalfTheIpd_InMetres()
        {
            Assert.AreEqual(-0.0315f, StereoMath.EyeOffsetM(63f, StereoEye.Left), 1e-7f);
            Assert.AreEqual(+0.0315f, StereoMath.EyeOffsetM(63f, StereoEye.Right), 1e-7f);
            Assert.AreEqual(-0.032f, StereoMath.EyeOffsetM(64f, StereoEye.Left), 1e-7f);
            Assert.AreEqual(+0.032f, StereoMath.EyeOffsetM(64f, StereoEye.Right), 1e-7f);
        }

        [Test]
        public void EyeOffsets_AreSymmetricAndSumToTheIpd()
        {
            foreach (float ipd in new[] { 0f, 55f, 63f, 70f, 80f })
            {
                float left = StereoMath.EyeOffsetM(ipd, StereoEye.Left);
                float right = StereoMath.EyeOffsetM(ipd, StereoEye.Right);
                Assert.AreEqual(0f, left + right, 1e-7f, "symmetric about the head centre, ipd " + ipd);
                Assert.AreEqual(ipd * 0.001f, right - left, 1e-6f, "separation equals the IPD, ipd " + ipd);
                Assert.LessOrEqual(left, 0f);
                Assert.GreaterOrEqual(right, 0f);
            }
        }

        // ---- 3 and 4. projection ------------------------------------------------------------------------------------

        [Test]
        public void SymmetricProjection_EqualsMatrix4x4Perspective_ForTheDerivedVerticalFov()
        {
            float vFov = StereoMath.VerticalFovDeg(DiagonalDeg, Aspect);
            Matrix4x4 expected = Matrix4x4.Perspective(vFov, Aspect, Near, Far);

            // Parallel axes (convergence 0): both eyes use the same symmetric frustum, the eye offset only moves the camera.
            AssertMatrixEqual(expected, StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 0f), 1e-5f, "left eye");
            AssertMatrixEqual(expected, StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Right, 0f), 1e-5f, "right eye");
        }

        [Test]
        public void ZeroIpdWithConvergence_IsAlsoTheSymmetricProjection()
        {
            // The 2D fallback uses one camera at the head centre: no eye offset, so no shift whatever the convergence.
            float vFov = StereoMath.VerticalFovDeg(DiagonalDeg, Aspect);
            Matrix4x4 expected = Matrix4x4.Perspective(vFov, Aspect, Near, Far);
            AssertMatrixEqual(expected, StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, 0f, StereoEye.Left, 2f), 1e-5f, "ipd 0");
        }

        [Test]
        public void SymmetricProjection_HasTheExpectedElements()
        {
            // 52 deg at 16:9: x = 0.425096, y = 0.239116 (python3, double precision); n = 0.1, f = 20.
            Matrix4x4 m = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 0f);
            Assert.AreEqual(2.352411f, m.m00, 1e-4f);
            Assert.AreEqual(4.182064f, m.m11, 1e-4f);
            Assert.AreEqual(0f, m.m02, 1e-7f);
            Assert.AreEqual(0f, m.m12, 1e-7f);
            Assert.AreEqual(-1.01005025f, m.m22, 1e-5f);
            Assert.AreEqual(-0.20100503f, m.m23, 1e-5f);
            Assert.AreEqual(-1f, m.m32, 0f);
            Assert.AreEqual(0f, m.m33, 0f);
        }

        // ---- 3. off-axis shift: sign and size ------------------------------------------------------------------------

        [Test]
        public void NearPlaneShift_LeftEyeMovesRight_RightEyeMovesLeft_ZeroWhenParallel()
        {
            float left = StereoMath.EyeOffsetM(IpdMm, StereoEye.Left);
            float right = StereoMath.EyeOffsetM(IpdMm, StereoEye.Right);

            Assert.AreEqual(+0.00315f, StereoMath.NearPlaneShift(left, 0.1f, 1f), 1e-7f);    // s = -e * n / C = 0.0315 * 0.1 / 1
            Assert.AreEqual(-0.00315f, StereoMath.NearPlaneShift(right, 0.1f, 1f), 1e-7f);
            Assert.AreEqual(+0.001575f, StereoMath.NearPlaneShift(left, 0.1f, 2f), 1e-7f);   // half the shift at twice the distance
            Assert.AreEqual(0f, StereoMath.NearPlaneShift(left, 0.1f, 0f), 0f, "convergence 0 = parallel axes = no shift");
            Assert.AreEqual(0f, StereoMath.NearPlaneShift(0f, 0.1f, 1f), 0f, "a camera at the head centre does not shift");
        }

        [Test]
        public void OffAxisProjection_SignAndSizeOfTheShift()
        {
            // m02 = (r + l) / (r - l) = -e / (C * x), x = tan(H/2) = 0.425096.  P = 63 mm, C = 1 m  ->  0.0741 for the left eye.
            const float convergence = 1f;
            Matrix4x4 left = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, convergence);
            Matrix4x4 right = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Right, convergence);

            Assert.Greater(left.m02, 0f, "left eye window moves toward the nose (right)");
            Assert.Less(right.m02, 0f, "right eye window moves toward the nose (left)");
            Assert.AreEqual(0.074101f, left.m02, 1e-5f, "size, left");
            Assert.AreEqual(-0.074101f, right.m02, 1e-5f, "size, right");
            Assert.AreEqual(-left.m02, right.m02, 1e-7f, "mirror images of each other");

            // About 71 px of a 1920 px wide eye image (half-width 960 px).
            Assert.AreEqual(71.1f, left.m02 * 960f, 0.1f);

            // Only m02 changes; the field of view (m00, m11) and depth terms are the symmetric ones.
            Matrix4x4 symmetric = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 0f);
            Assert.AreEqual(symmetric.m00, left.m00, 1e-6f);
            Assert.AreEqual(symmetric.m11, left.m11, 1e-6f);
            Assert.AreEqual(symmetric.m22, left.m22, 1e-6f);
            Assert.AreEqual(symmetric.m23, left.m23, 1e-6f);
            Assert.AreEqual(0f, left.m12, 1e-7f, "no vertical shift");
        }

        [Test]
        public void OffAxisProjection_ShiftIsInverselyProportionalToTheConvergenceDistance()
        {
            float at1 = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 1f).m02;
            float at2 = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 2f).m02;
            float at4 = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 4f).m02;
            Assert.AreEqual(at1 / 2f, at2, 1e-6f);
            Assert.AreEqual(at1 / 4f, at4, 1e-6f);
        }

        [Test]
        public void OffAxisProjection_ShiftGrowsWithTheIpd()
        {
            float narrow = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, 55f, StereoEye.Left, 1f).m02;
            float wide = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, 70f, StereoEye.Left, 1f).m02;
            Assert.Greater(wide, narrow);
            Assert.AreEqual(55f / 70f, narrow / wide, 1e-4f);
        }

        [Test]
        public void OffAxisProjection_APointAtTheConvergenceDistanceOnTheMedianPlane_LandsInTheCentreOfBothEyes()
        {
            // Eye space: the camera looks down -z. The head's median plane is x = -e in eye space.
            foreach (float convergence in new[] { 0.5f, 1f, 3f })
            {
                foreach (StereoEye eye in new[] { StereoEye.Left, StereoEye.Right })
                {
                    float e = StereoMath.EyeOffsetM(IpdMm, eye);
                    Matrix4x4 p = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, eye, convergence);
                    Vector3 ndc = p.MultiplyPoint(new Vector3(-e, 0f, -convergence));
                    Assert.AreEqual(0f, ndc.x, 1e-5f, eye + " eye, C = " + convergence);
                    Assert.AreEqual(0f, ndc.y, 1e-6f);
                }
            }
        }

        [Test]
        public void OffAxisProjection_ParallaxIsUncrossedBeyondTheConvergenceDistanceAndCrossedInFrontOfIt()
        {
            const float convergence = 1f;
            float eL = StereoMath.EyeOffsetM(IpdMm, StereoEye.Left);
            float eR = StereoMath.EyeOffsetM(IpdMm, StereoEye.Right);
            Matrix4x4 pL = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, convergence);
            Matrix4x4 pR = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Right, convergence);

            // A point on the head's median plane at distance z, as each eye sees it.
            float far = 4f;
            float near = 0.5f;
            float farDisparity = pR.MultiplyPoint(new Vector3(-eR, 0f, -far)).x - pL.MultiplyPoint(new Vector3(-eL, 0f, -far)).x;
            float nearDisparity = pR.MultiplyPoint(new Vector3(-eR, 0f, -near)).x - pL.MultiplyPoint(new Vector3(-eL, 0f, -near)).x;
            Assert.Greater(farDisparity, 0f, "beyond C: the right eye's image is to the right of the left eye's");
            Assert.Less(nearDisparity, 0f, "in front of C: crossed");

            // With parallel axes (C = 0) the disparity of a median-plane point at distance z is -m00 * IPD / z: always crossed,
            // and zero only at infinity (here z = 1000 m: -2.35 * 0.063 / 1000 = -0.00015).
            Matrix4x4 qL = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Left, 0f);
            Matrix4x4 qR = StereoMath.EyeProjection(DiagonalDeg, Aspect, Near, Far, IpdMm, StereoEye.Right, 0f);
            float parallelFar = qR.MultiplyPoint(new Vector3(-eR, 0f, -1000f)).x - qL.MultiplyPoint(new Vector3(-eL, 0f, -1000f)).x;
            Assert.AreEqual(0f, parallelFar, 5e-3f, "parallel axes: zero disparity at (almost) infinity");
        }

        [Test]
        public void EyeFrustum_KeepsTheSameWidthAndHeightWhateverTheConvergence()
        {
            float eL = StereoMath.EyeOffsetM(IpdMm, StereoEye.Left);
            NearPlaneFrustum parallel = StereoMath.EyeFrustum(DiagonalDeg, Aspect, Near, Far, eL, 0f);
            NearPlaneFrustum converged = StereoMath.EyeFrustum(DiagonalDeg, Aspect, Near, Far, eL, 1f);

            Assert.AreEqual(parallel.Right - parallel.Left, converged.Right - converged.Left, 1e-6f, "width");
            Assert.AreEqual(parallel.Top - parallel.Bottom, converged.Top - converged.Bottom, 1e-6f, "height");
            Assert.AreEqual(-parallel.Left, parallel.Right, 1e-7f, "parallel frustum is symmetric");
            Assert.AreEqual(0.00315f, (converged.Right + converged.Left) * 0.5f, 1e-6f, "centre of the converged frustum = the near-plane shift");
            Assert.AreEqual(Near * 0.425096f, parallel.Right, 1e-6f, "half-width on the near plane = n * tan(H/2)");
            Assert.AreEqual(Near * 0.239116f, parallel.Top, 1e-6f, "half-height on the near plane = n * tan(V/2)");
            Assert.AreEqual(Near, converged.Near, 0f);
            Assert.AreEqual(Far, converged.Far, 0f);
        }

        [Test]
        public void ProjectionFromFrustum_MatchesTheDocsPerspectiveOffCenterExample()
        {
            // The PerspectiveOffCenter function from the Camera.projectionMatrix page of the Unity 2021.3 docs, evaluated by hand.
            NearPlaneFrustum f;
            f.Left = -0.2f; f.Right = 0.4f; f.Bottom = -0.1f; f.Top = 0.3f; f.Near = 0.5f; f.Far = 50f;
            Matrix4x4 m = StereoMath.ProjectionFromFrustum(f);
            Assert.AreEqual(2f * 0.5f / 0.6f, m.m00, 1e-6f);
            Assert.AreEqual(2f * 0.5f / 0.4f, m.m11, 1e-6f);
            Assert.AreEqual((0.4f - 0.2f) / 0.6f, m.m02, 1e-6f);
            Assert.AreEqual((0.3f - 0.1f) / 0.4f, m.m12, 1e-6f);
            Assert.AreEqual(-(50f + 0.5f) / (50f - 0.5f), m.m22, 1e-6f);
            Assert.AreEqual(-(2f * 50f * 0.5f) / (50f - 0.5f), m.m23, 1e-6f);
            Assert.AreEqual(-1f, m.m32, 0f);
            Assert.AreEqual(0f, m.m33, 0f);
            Assert.AreEqual(0f, m.m01 + m.m03 + m.m10 + m.m13 + m.m20 + m.m21 + m.m30 + m.m31, 0f);
        }

        // ---- 5. viewports -------------------------------------------------------------------------------------------

        [Test]
        public void Viewports_LeftEyeLeftHalf_RightEyeRightHalf_AndSwapped()
        {
            Assert.AreEqual(new Rect(0f, 0f, 0.5f, 1f), StereoMath.EyeViewport(StereoEye.Left, false));
            Assert.AreEqual(new Rect(0.5f, 0f, 0.5f, 1f), StereoMath.EyeViewport(StereoEye.Right, false));
            Assert.AreEqual(new Rect(0.5f, 0f, 0.5f, 1f), StereoMath.EyeViewport(StereoEye.Left, true));
            Assert.AreEqual(new Rect(0f, 0f, 0.5f, 1f), StereoMath.EyeViewport(StereoEye.Right, true));
            Assert.AreEqual(new Rect(0f, 0f, 1f, 1f), StereoMath.FullViewport);
        }

        // ---- bad input ----------------------------------------------------------------------------------------------

        [Test]
        public void BadInput_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.HorizontalFovDeg(0f, Aspect));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.HorizontalFovDeg(180f, Aspect));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.VerticalFovDeg(float.NaN, Aspect));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.VerticalFovDeg(52f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.VerticalFovDeg(52f, -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.AspectOf(0, 1080));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.EyeOffsetM(-1f, StereoEye.Left));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.NearPlaneShift(0.03f, 0.1f, -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.EyeProjection(52f, Aspect, 0f, 20f, 63f, StereoEye.Left, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.EyeProjection(52f, Aspect, 1f, 1f, 63f, StereoEye.Left, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => StereoMath.EyeProjection(52f, Aspect, 0.1f, 20f, 63f, StereoEye.Left, -0.5f));
        }
    }

    /// <summary>
    /// Edit Mode tests of the StereoRig component (no Play Mode: StereoRig.Create builds and applies everything itself, because Awake
    /// does not run in Edit Mode). Checks hierarchy, eye positions, viewports, display index, 2D fallback, clear settings and that the
    /// camera matrices are the ones StereoMath computes. Lives in this file because the slice owns only this test file.
    /// </summary>
    public class StereoRigEditModeTests
    {
        GameObject _root;
        StereoRig _rig;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("StereoRigTestRoot");
            _rig = StereoRig.Create(_root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            _rig = null;
        }

        static RenderFlags Flags(bool stereo, int display, float ipdMm, bool swapEyes, float convergenceM)
        {
            RenderFlags f = new RenderFlags();
            f.stereo = stereo;
            f.glassesDisplay = display;
            f.ipdMm = ipdMm;
            f.swapEyes = swapEyes;
            f.convergenceM = convergenceM;
            return f;
        }

        static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual, string what)
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    Assert.AreEqual(expected[row, col], actual[row, col], 1e-5f * (1f + Mathf.Abs(expected[row, col])), what + " [" + row + "," + col + "]");
        }

        [Test]
        public void Create_BuildsHeadAndTwoEyeCameras_UnderTheGivenParent()
        {
            Assert.AreSame(_root.transform, _rig.transform.parent);
            Assert.IsNotNull(_rig.Head);
            Assert.AreSame(_rig.transform, _rig.Head.parent);
            Assert.AreEqual("Head", _rig.Head.name);
            Assert.IsNotNull(_rig.LeftEye);
            Assert.IsNotNull(_rig.RightEye);
            Assert.AreSame(_rig.Head, _rig.LeftEye.transform.parent);
            Assert.AreSame(_rig.Head, _rig.RightEye.transform.parent);
            Assert.AreEqual("LeftEye", _rig.LeftEye.name);
            Assert.AreEqual("RightEye", _rig.RightEye.name);
            Assert.AreEqual(2, _rig.Head.childCount);
        }

        [Test]
        public void Stereo_PutsTheEyesAtMinusAndPlusHalfIpd_AndGivesEachEyeItsHalfOfTheDisplay()
        {
            _rig.Apply(Flags(true, 1, 63f, false, 0f));

            Assert.IsTrue(_rig.IsStereo);
            Assert.AreEqual(-0.0315f, _rig.LeftEye.transform.localPosition.x, 1e-6f);
            Assert.AreEqual(+0.0315f, _rig.RightEye.transform.localPosition.x, 1e-6f);
            Assert.AreEqual(0f, _rig.LeftEye.transform.localPosition.y, 0f);
            Assert.AreEqual(0f, _rig.LeftEye.transform.localPosition.z, 0f);
            Assert.AreEqual(new Rect(0f, 0f, 0.5f, 1f), _rig.LeftEye.rect);
            Assert.AreEqual(new Rect(0.5f, 0f, 0.5f, 1f), _rig.RightEye.rect);
            Assert.IsTrue(_rig.LeftEye.enabled);
            Assert.IsTrue(_rig.RightEye.enabled);
        }

        [Test]
        public void GlassesDisplayFlag_BecomesTheTargetDisplayOfBothEyes()
        {
            foreach (int display in new[] { 0, 1, 3 })
            {
                _rig.Apply(Flags(true, display, 63f, false, 0f));
                Assert.AreEqual(display, _rig.TargetDisplay);
                Assert.AreEqual(display, _rig.LeftEye.targetDisplay, "left, display " + display);
                Assert.AreEqual(display, _rig.RightEye.targetDisplay, "right, display " + display);
            }
        }

        [Test]
        public void SwapEyes_SwapsTheHalves_NotThePositions()
        {
            _rig.Apply(Flags(true, 1, 63f, true, 0f));

            Assert.AreEqual(new Rect(0.5f, 0f, 0.5f, 1f), _rig.LeftEye.rect, "left camera draws the right half");
            Assert.AreEqual(new Rect(0f, 0f, 0.5f, 1f), _rig.RightEye.rect, "right camera draws the left half");
            Assert.AreEqual(-0.0315f, _rig.LeftEye.transform.localPosition.x, 1e-6f, "the left camera stays on the left");
            Assert.AreEqual(+0.0315f, _rig.RightEye.transform.localPosition.x, 1e-6f);
        }

        [Test]
        public void TwoDFallback_UsesOneCentredFullViewportCamera()
        {
            _rig.Apply(Flags(false, 1, 63f, true, 1f));

            Assert.IsFalse(_rig.IsStereo);
            Assert.IsTrue(_rig.LeftEye.enabled, "the left camera is the single camera");
            Assert.IsFalse(_rig.RightEye.enabled);
            Assert.AreEqual(new Rect(0f, 0f, 1f, 1f), _rig.LeftEye.rect);
            Assert.AreEqual(0f, _rig.LeftEye.transform.localPosition.x, 0f, "centred on the head");
            Assert.AreEqual(1, _rig.LeftEye.targetDisplay);

            // 52 deg diagonal at 16:9, symmetric frustum, whatever the convergence flag says.
            Matrix4x4 expected = Matrix4x4.Perspective(StereoMath.VerticalFovDeg(52f, 1920f / 1080f), 1920f / 1080f, 0.1f, 20f);
            AssertMatrixEqual(expected, _rig.LeftEye.projectionMatrix, "2D projection");
        }

        [Test]
        public void EyeCameras_UseTheProjectionsFromStereoMath()
        {
            _rig.Apply(Flags(true, 1, 63f, false, 0f));
            AssertMatrixEqual(StereoMath.EyeProjection(52f, 1920f / 1080f, 0.1f, 20f, 63f, StereoEye.Left, 0f), _rig.LeftEye.projectionMatrix, "parallel, left");
            AssertMatrixEqual(StereoMath.EyeProjection(52f, 1920f / 1080f, 0.1f, 20f, 63f, StereoEye.Right, 0f), _rig.RightEye.projectionMatrix, "parallel, right");

            _rig.Apply(Flags(true, 1, 63f, false, 1.5f));
            AssertMatrixEqual(StereoMath.EyeProjection(52f, 1920f / 1080f, 0.1f, 20f, 63f, StereoEye.Left, 1.5f), _rig.LeftEye.projectionMatrix, "converged, left");
            AssertMatrixEqual(StereoMath.EyeProjection(52f, 1920f / 1080f, 0.1f, 20f, 63f, StereoEye.Right, 1.5f), _rig.RightEye.projectionMatrix, "converged, right");
            Assert.Greater(_rig.LeftEye.projectionMatrix.m02, 0f);
            Assert.Less(_rig.RightEye.projectionMatrix.m02, 0f);
        }

        [Test]
        public void ClearsToSolidBlack_NoSkybox_NoHdr_NoRenderTexture()
        {
            _rig.Apply(Flags(true, 1, 63f, false, 0f));
            foreach (Camera cam in new[] { _rig.LeftEye, _rig.RightEye })
            {
                Assert.AreEqual(CameraClearFlags.SolidColor, cam.clearFlags, cam.name);
                Assert.AreEqual(0f, cam.backgroundColor.r + cam.backgroundColor.g + cam.backgroundColor.b, 0f, cam.name + " background is black");
                Assert.IsFalse(cam.allowHDR, cam.name);
                Assert.IsNull(cam.targetTexture, cam.name);
                Assert.IsFalse(cam.orthographic, cam.name);
            }
        }

        [Test]
        public void DebugEyeColors_TintTheEyesRedAndBlue_AndBackToBlack()
        {
            _rig.DebugEyeColors = true;
            Assert.Greater(_rig.LeftEye.backgroundColor.r, 0.2f);
            Assert.AreEqual(0f, _rig.LeftEye.backgroundColor.b, 0f);
            Assert.Greater(_rig.RightEye.backgroundColor.b, 0.2f);
            Assert.AreEqual(0f, _rig.RightEye.backgroundColor.r, 0f);

            _rig.Apply(Flags(true, 1, 63f, false, 0f));             // re-applying keeps the tint
            Assert.Greater(_rig.LeftEye.backgroundColor.r, 0.2f);

            _rig.DebugEyeColors = false;
            Assert.AreEqual(0f, _rig.LeftEye.backgroundColor.r, 0f);
            Assert.AreEqual(0f, _rig.RightEye.backgroundColor.b, 0f);
        }

        [Test]
        public void Apply_Again_UpdatesInPlace_WithoutAddingObjects()
        {
            _rig.Apply(Flags(true, 1, 63f, false, 0f));
            _rig.Apply(Flags(true, 1, 70f, false, 0f));

            Assert.AreEqual(2, _rig.Head.childCount);
            Assert.AreEqual(1, _rig.transform.childCount);
            Assert.AreEqual(-0.035f, _rig.LeftEye.transform.localPosition.x, 1e-6f);
            Assert.AreEqual(+0.035f, _rig.RightEye.transform.localPosition.x, 1e-6f);
        }

        [Test]
        public void OutOfRangeFlags_AreClamped_NotThrown()
        {
            RenderFlags bad = new RenderFlags();
            bad.ipdMm = -5f;
            bad.fovDiagonalDeg = 0f;
            bad.nearM = -1f;
            bad.farM = 0f;
            bad.convergenceM = float.NaN;
            bad.glassesDisplay = 99;
            bad.eyeWidth = 0;
            bad.eyeHeight = 0;

            Assert.DoesNotThrow(() => _rig.Apply(bad));

            Assert.AreEqual(7, _rig.TargetDisplay, "Camera.targetDisplay supports at most 8 displays");
            Assert.AreEqual(7, _rig.LeftEye.targetDisplay);
            foreach (Camera cam in new[] { _rig.LeftEye, _rig.RightEye })
            {
                Matrix4x4 m = cam.projectionMatrix;
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        Assert.IsFalse(float.IsNaN(m[row, col]) || float.IsInfinity(m[row, col]), cam.name + " [" + row + "," + col + "]");
                Assert.Greater(cam.farClipPlane, cam.nearClipPlane);
                Assert.Greater(cam.nearClipPlane, 0f);
            }
        }

        [Test]
        public void Describe_NamesTheModeAndTheDisplay()
        {
            _rig.Apply(Flags(true, 1, 63f, false, 0f));
            StringAssert.Contains("SBS", _rig.Describe());
            StringAssert.Contains("display index 1", _rig.Describe());
            _rig.Apply(Flags(false, 1, 63f, false, 0f));
            StringAssert.Contains("2D", _rig.Describe());
        }
    }
}
