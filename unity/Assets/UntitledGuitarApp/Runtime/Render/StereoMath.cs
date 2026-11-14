// StereoMath: the projection maths behind StereoRig. Plain C#: no MonoBehaviour, no scene objects, no allocation.
// Every method is a pure function of its arguments, so Tests/EditMode/StereoMathTests.cs can check them without a scene.
//
// ---------------------------------------------------------------------------------------------------------------------
// Derivation (the code below is this text, line by line)
// ---------------------------------------------------------------------------------------------------------------------
//
// 1. Diagonal FOV -> horizontal and vertical FOV
//    A perspective camera draws a flat, rectangular image. Put that image plane 1 unit in front of the eye, centred on
//    the optical axis. With square pixels and aspect a = width / height, its half-width is x = tan(H/2) and its
//    half-height is y = tan(V/2), so x = a * y. A corner of the image is the point (x, y, 1). The angle between the
//    optical axis and a corner is atan(sqrt(x^2 + y^2)); the diagonal FOV D is the angle between two opposite corners,
//    which is twice that:
//        tan(D/2) = sqrt(x^2 + y^2) = y * sqrt(1 + a^2)
//    so
//        y = tan(D/2) / sqrt(1 + a^2)      V = 2 * atan(y)
//        x = a * y                          H = 2 * atan(x)
//    Check, D = 52 deg and a = 16/9:  tan(26 deg) = 0.48773,  sqrt(1 + 256/81) = 2.03973,  y = 0.239116,  x = 0.425096,
//    so V = 26.90 deg and H = 46.06 deg. That is the "52 deg diagonal is about 46 x 27 deg" of docs/HOW_IT_WORKS.md.
//    Angles do NOT add like Pythagoras: sqrt(46.06^2 + 26.90^2) = 53.3, not 52. Only the tangents do, because the image is flat.
//    Cross-check against XREAL's own marketing figure: a flat 154-inch 16:9 image seen from 4 m (XREAL: "154-inch display
//    from 13 feet") is 46.2 x 27.0 deg = 52.1 deg diagonal, which is the same geometry.
//
// 2. Eye positions
//    Head frame: +x to the wearer's right, +y up, +z forward. With an inter-pupillary distance P (millimetres) the left
//    eye is at x = -P/2 and the right eye at x = +P/2, both looking along +z (parallel optical axes).
//
// 3. Off-axis frusta and the convergence distance C
//    Parallel axes with symmetric frusta (C = 0): an object at infinity straight ahead lands on the centre of BOTH eye
//    images (zero parallax at infinity), and an object at distance Z is shifted by about f*P/Z pixels between the
//    eyes, exactly like two real eyes looking straight ahead. That is right when the glasses' optical axes are parallel.
//    If the glasses' two virtual images are designed to overlap at a finite distance (a birdbath "virtual screen at
//    4 m"), the zero-parallax plane belongs at that distance instead. We keep the axes parallel and use an OFF-AXIS
//    frustum: each eye's frustum is shifted sideways so that one common window (a rectangle centred on the head's median
//    plane, at distance C) fills each eye image. A point on the median plane at distance C then has zero parallax.
//
//    Seen from an eye at lateral offset e (e = -P/2 for the left eye), the centre of that window is at lateral
//    position -e from the eye's own axis, at distance C. On the near plane (distance n) that is -e * n / C. So the
//    frustum is the symmetric one (half-width w = n*x, half-height h = n*y, from step 1) translated by
//        s = -e * n / C
//    which gives, on the near plane:
//        left = -w + s     right = +w + s     bottom = -h     top = +h
//    The left eye (e < 0) gets s > 0: its window moves toward the nose. C = 0 means "no convergence": s = 0.
//
// 4. Projection matrix (OpenGL convention, like Camera.projectionMatrix, Matrix4x4.Frustum and Matrix4x4.Perspective; it is
//    the PerspectiveOffCenter example of the Camera.projectionMatrix page in the Unity 2021.3 docs)
//        | 2n/(r-l)     0        (r+l)/(r-l)       0        |
//        |    0      2n/(t-b)    (t+b)/(t-b)       0        |
//        |    0         0       -(f+n)/(f-n)   -2fn/(f-n)   |
//        |    0         0            -1             0        |
//    With r - l = 2w and r + l = 2s:   m00 = 1/x,   m11 = 1/y,   m02 = s/w = -e / (C * x),   m12 = 0.
//    Symmetric case (s = 0): m00 = 1/(a * tan(V/2)) and m11 = 1/tan(V/2), which is exactly Matrix4x4.Perspective(V, a, n, f).
//    Sign check: a point on the median plane at distance C is at eye-space (-e, 0, -C). Its clip x is
//    m00 * (-e) + m02 * (-C) = -e/x + e/x = 0, so it lands on the centre of both eye images. Points farther than C land
//    left of centre in the left eye and right of centre in the right eye (uncrossed parallax, behind the screen plane);
//    nearer points do the opposite (crossed parallax, in front of the screen plane).
//    Size check, P = 63 mm, C = 1 m, 52 deg at 16:9: m02 = 0.063 / (2 * 1 * 0.425096) = 0.0741 for the left eye,
//    i.e. the window is shifted by 7.4 % of the half-width, about 71 px of a 1920 px eye image.
// ---------------------------------------------------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Render
{
    /// <summary>Which eye. The head frame has +x to the wearer's right, so the left eye sits at -x.</summary>
    public enum StereoEye
    {
        Left = 0,
        Right = 1
    }

    /// <summary>
    /// A perspective frustum given by the rectangle it cuts out of its near plane (view space, metres) and the near
    /// and far distances. These are the six arguments of <c>Matrix4x4.Frustum(left, right, bottom, top, zNear, zFar)</c>.
    /// </summary>
    public struct NearPlaneFrustum
    {
        public float Left;
        public float Right;
        public float Bottom;
        public float Top;
        public float Near;
        public float Far;
    }

    /// <summary>Per-eye projection maths for the glasses. See the derivation at the top of this file.</summary>
    public static class StereoMath
    {
        const double Deg2Rad = Math.PI / 180.0;
        const double Rad2Deg = 180.0 / Math.PI;

        /// <summary>Viewport of one camera that fills the whole target (the 2D fallback).</summary>
        public static readonly Rect FullViewport = new Rect(0f, 0f, 1f, 1f);

        // ------------------------------------------------------------------------------------------------------------
        // 1. Diagonal FOV <-> horizontal and vertical FOV
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Width / height of an eye image, for example 1920 / 1080 = 1.7778.</summary>
        public static float AspectOf(int widthPx, int heightPx)
        {
            if (widthPx <= 0) throw new ArgumentOutOfRangeException("widthPx", widthPx, "must be > 0");
            if (heightPx <= 0) throw new ArgumentOutOfRangeException("heightPx", heightPx, "must be > 0");
            return (float)widthPx / heightPx;
        }

        /// <summary>Horizontal FOV in degrees of a flat image with the given diagonal FOV and aspect ratio (width / height).</summary>
        public static float HorizontalFovDeg(float diagonalDeg, float aspect)
        {
            double halfWidth, halfHeight;
            HalfExtentsAtUnitDistance(diagonalDeg, aspect, out halfWidth, out halfHeight);
            return (float)(2.0 * Math.Atan(halfWidth) * Rad2Deg);
        }

        /// <summary>Vertical FOV in degrees of a flat image with the given diagonal FOV and aspect ratio (width / height).</summary>
        public static float VerticalFovDeg(float diagonalDeg, float aspect)
        {
            double halfWidth, halfHeight;
            HalfExtentsAtUnitDistance(diagonalDeg, aspect, out halfWidth, out halfHeight);
            return (float)(2.0 * Math.Atan(halfHeight) * Rad2Deg);
        }

        /// <summary>Both at once: 52 deg diagonal at 16:9 gives about 46.06 x 26.90 deg.</summary>
        public static void DiagonalToHorizontalVertical(float diagonalDeg, float aspect, out float horizontalDeg, out float verticalDeg)
        {
            double halfWidth, halfHeight;
            HalfExtentsAtUnitDistance(diagonalDeg, aspect, out halfWidth, out halfHeight);
            horizontalDeg = (float)(2.0 * Math.Atan(halfWidth) * Rad2Deg);
            verticalDeg = (float)(2.0 * Math.Atan(halfHeight) * Rad2Deg);
        }

        /// <summary>The inverse of <see cref="DiagonalToHorizontalVertical"/>: tan(D/2) = sqrt(tan(H/2)^2 + tan(V/2)^2).</summary>
        public static float HorizontalVerticalToDiagonal(float horizontalDeg, float verticalDeg)
        {
            if (!(horizontalDeg > 0f && horizontalDeg < 180f)) throw new ArgumentOutOfRangeException("horizontalDeg", horizontalDeg, "must be in (0, 180)");
            if (!(verticalDeg > 0f && verticalDeg < 180f)) throw new ArgumentOutOfRangeException("verticalDeg", verticalDeg, "must be in (0, 180)");
            double x = Math.Tan(0.5 * horizontalDeg * Deg2Rad);
            double y = Math.Tan(0.5 * verticalDeg * Deg2Rad);
            return (float)(2.0 * Math.Atan(Math.Sqrt(x * x + y * y)) * Rad2Deg);
        }

        // Half-width x and half-height y of the image rectangle at distance 1 (step 1 of the derivation).
        static void HalfExtentsAtUnitDistance(float diagonalDeg, float aspect, out double halfWidth, out double halfHeight)
        {
            // "!(a && b)" instead of "a || b" so that NaN is rejected too.
            if (!(diagonalDeg > 0f && diagonalDeg < 180f)) throw new ArgumentOutOfRangeException("diagonalDeg", diagonalDeg, "must be in (0, 180)");
            if (!(aspect > 0f)) throw new ArgumentOutOfRangeException("aspect", aspect, "must be > 0");
            double tanHalfDiagonal = Math.Tan(0.5 * diagonalDeg * Deg2Rad);
            halfHeight = tanHalfDiagonal / Math.Sqrt(1.0 + (double)aspect * aspect);
            halfWidth = aspect * halfHeight;
        }

        // ------------------------------------------------------------------------------------------------------------
        // 2. Eye offsets
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Head-space x of an eye in metres: -IPD/2 for the left eye, +IPD/2 for the right eye (ipdMm is in millimetres).</summary>
        public static float EyeOffsetM(float ipdMm, StereoEye eye)
        {
            if (!(ipdMm >= 0f)) throw new ArgumentOutOfRangeException("ipdMm", ipdMm, "must be >= 0");
            float half = 0.5f * ipdMm * 0.001f;
            return eye == StereoEye.Left ? -half : half;
        }

        // ------------------------------------------------------------------------------------------------------------
        // 3. Off-axis frustum
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Sideways shift of the frustum on the near plane, in metres: s = -e * n / C (step 3). Positive means the window moves
        /// to the wearer's right, which is what the left eye needs. Zero when convergenceM is 0 (parallel axes, no shift).
        /// </summary>
        public static float NearPlaneShift(float eyeOffsetM, float nearM, float convergenceM)
        {
            if (!(convergenceM >= 0f)) throw new ArgumentOutOfRangeException("convergenceM", convergenceM, "must be >= 0 (0 = parallel axes)");
            if (convergenceM == 0f) return 0f;
            return -eyeOffsetM * nearM / convergenceM;
        }

        /// <summary>
        /// The frustum of one eye: a symmetric frustum for the given diagonal FOV and aspect ratio, shifted sideways on the near
        /// plane by <see cref="NearPlaneShift"/>. eyeOffsetM is the eye's head-space x (see <see cref="EyeOffsetM"/>).
        /// </summary>
        public static NearPlaneFrustum EyeFrustum(float diagonalDeg, float aspect, float nearM, float farM, float eyeOffsetM, float convergenceM)
        {
            CheckClipPlanes(nearM, farM);
            double halfWidth, halfHeight;
            HalfExtentsAtUnitDistance(diagonalDeg, aspect, out halfWidth, out halfHeight);
            float w = (float)(nearM * halfWidth);
            float h = (float)(nearM * halfHeight);
            float s = NearPlaneShift(eyeOffsetM, nearM, convergenceM);

            NearPlaneFrustum f;
            f.Left = -w + s;
            f.Right = w + s;
            f.Bottom = -h;
            f.Top = h;
            f.Near = nearM;
            f.Far = farM;
            return f;
        }

        // ------------------------------------------------------------------------------------------------------------
        // 4. Projection matrices
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The OpenGL-convention projection matrix of a frustum (step 4). It is the PerspectiveOffCenter function of the Camera.projectionMatrix
        /// page of the Unity 2021.3 docs, and what <c>Matrix4x4.Frustum(left, right, bottom, top, zNear, zFar)</c> returns.
        /// </summary>
        public static Matrix4x4 ProjectionFromFrustum(NearPlaneFrustum f)
        {
            CheckClipPlanes(f.Near, f.Far);
            float width = f.Right - f.Left;
            float height = f.Top - f.Bottom;
            if (!(width > 0f)) throw new ArgumentException("frustum has no width (right <= left)");
            if (!(height > 0f)) throw new ArgumentException("frustum has no height (top <= bottom)");
            float depth = f.Far - f.Near;

            Matrix4x4 m = new Matrix4x4();            // all zero
            m.m00 = 2f * f.Near / width;
            m.m02 = (f.Right + f.Left) / width;
            m.m11 = 2f * f.Near / height;
            m.m12 = (f.Top + f.Bottom) / height;
            m.m22 = -(f.Far + f.Near) / depth;
            m.m23 = -2f * f.Far * f.Near / depth;
            m.m32 = -1f;
            return m;
        }

        /// <summary>
        /// Projection matrix for one eye of the glasses, ready for Camera.projectionMatrix.
        /// diagonalDeg 52 (XREAL Air 2 Ultra); aspect is the eye image's width / height; ipdMm is the inter-pupillary distance;
        /// convergenceM 0 means parallel axes with symmetric frusta, > 0 means off-axis frusta converging at that distance.
        /// </summary>
        public static Matrix4x4 EyeProjection(float diagonalDeg, float aspect, float nearM, float farM, float ipdMm, StereoEye eye, float convergenceM)
        {
            float offset = EyeOffsetM(ipdMm, eye);
            return ProjectionFromFrustum(EyeFrustum(diagonalDeg, aspect, nearM, farM, offset, convergenceM));
        }

        // ------------------------------------------------------------------------------------------------------------
        // 5. Viewports
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Where an eye camera draws inside the side-by-side frame: the left half for the left eye and the right half for the
        /// right eye, or the other way round when swapEyes is set (glasses that show the left half to the right eye).
        /// Camera.rect is normalised and has its origin at the bottom left.
        /// </summary>
        public static Rect EyeViewport(StereoEye eye, bool swapEyes)
        {
            bool leftHalf = (eye == StereoEye.Left) != swapEyes;
            return leftHalf ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0.5f, 0f, 0.5f, 1f);
        }

        static void CheckClipPlanes(float nearM, float farM)
        {
            if (!(nearM > 0f)) throw new ArgumentOutOfRangeException("nearM", nearM, "must be > 0");
            if (!(farM > nearM)) throw new ArgumentOutOfRangeException("farM", farM, "must be > nearM");
        }
    }
}
