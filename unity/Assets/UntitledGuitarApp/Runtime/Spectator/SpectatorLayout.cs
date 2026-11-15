using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Spectator
{
    /// <summary>
    /// Pure layout and field-of-view maths for the spectator view (the Mac screen). No engine state, no allocation, all unit-tested.
    ///
    /// The spectator shows exactly the glasses' frame: the vertical field of view and the 16:9 shape of one eye image, so the
    /// judges never see more of the virtual world than the wearer does. If the Mac screen is a different shape, the frame
    /// is letterboxed (centred, dark bars) in the area above the status panel.
    /// </summary>
    public static class SpectatorLayout
    {
        /// <summary>The status panel takes this fraction of the screen height, at the bottom.</summary>
        public const float PanelFraction = 0.30f;

        /// <summary>Font size of status line 1 as a fraction of the panel height. On a 14 inch MacBook this is about 0.65 degrees of cap height at 1.5 m.</summary>
        public const float Line1OfPanel = 0.40f;

        /// <summary>Font size of status line 2 as a fraction of the panel height (about 0.5 degrees of cap height at 1.5 m on a 14 inch MacBook).</summary>
        public const float Line2OfPanel = 0.30f;

        /// <summary>Caption font size as a fraction of the screen height.</summary>
        public const float CaptionOfScreen = 0.03f;

        public const int MinLinePx = 12;
        public const int MinCaptionPx = 14;

        /// <summary>One eye of the glasses: 1920x1080.</summary>
        public const float DefaultAspect = 16f / 9f;

        public static int PanelHeightPx(int screenHeight)
        {
            return screenHeight <= 0 ? 0 : Mathf.RoundToInt(screenHeight * PanelFraction);
        }

        public static int Line1FontPx(int panelHeightPx)
        {
            return Mathf.Max(MinLinePx, Mathf.RoundToInt(panelHeightPx * Line1OfPanel));
        }

        public static int Line2FontPx(int panelHeightPx)
        {
            return Mathf.Max(MinLinePx, Mathf.RoundToInt(panelHeightPx * Line2OfPanel));
        }

        public static int CaptionFontPx(int screenHeight)
        {
            return Mathf.Max(MinCaptionPx, Mathf.RoundToInt(screenHeight * CaptionOfScreen));
        }

        /// <summary>
        /// Vertical field of view (degrees) of an image of shape <paramref name="aspect"/> (width / height) whose DIAGONAL
        /// field of view is <paramref name="diagonalDeg"/> (flat image plane: tan(d/2)^2 = tan(h/2)^2 + tan(v/2)^2).
        /// 52 degrees diagonal at 16:9 gives 26.9 vertical. Returns 0 for an unusable diagonal.
        /// </summary>
        public static float VerticalFovFromDiagonal(float diagonalDeg, float aspect)
        {
            if (!(diagonalDeg > 0f) || diagonalDeg >= 179f) return 0f;
            if (!(aspect > 0f)) aspect = DefaultAspect;
            float tanHalfDiagonal = Mathf.Tan(diagonalDeg * 0.5f * Mathf.Deg2Rad);
            float tanHalfVertical = tanHalfDiagonal / Mathf.Sqrt(1f + aspect * aspect);
            return 2f * Mathf.Atan(tanHalfVertical) * Mathf.Rad2Deg;
        }

        /// <summary>Horizontal field of view (degrees) for the same image: 52 degrees diagonal at 16:9 gives 46.0.</summary>
        public static float HorizontalFovFromDiagonal(float diagonalDeg, float aspect)
        {
            if (!(diagonalDeg > 0f) || diagonalDeg >= 179f) return 0f;
            if (!(aspect > 0f)) aspect = DefaultAspect;
            float tanHalfDiagonal = Mathf.Tan(diagonalDeg * 0.5f * Mathf.Deg2Rad);
            float tanHalfHorizontal = tanHalfDiagonal * aspect / Mathf.Sqrt(1f + aspect * aspect);
            return 2f * Mathf.Atan(tanHalfHorizontal) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The glasses' frame as a camera viewport rect (normalised, origin at the bottom left): the largest rectangle of
        /// shape <paramref name="aspect"/> that fits in the screen above a bottom panel of <paramref name="panelFraction"/>
        /// of the height, centred. Use it as Camera.rect.
        /// </summary>
        public static Rect FrameViewportRect(int screenW, int screenH, float aspect, float panelFraction)
        {
            if (screenW <= 0 || screenH <= 0) return new Rect(0f, 0f, 1f, 1f);
            float x, yTop, w, h;
            ComputeFrame(screenW, screenH, aspect, panelFraction, out x, out yTop, out w, out h);
            return new Rect(x / screenW, (screenH - yTop - h) / screenH, w / screenW, h / screenH);
        }

        /// <summary>The same frame in screen pixels with the origin at the TOP left (the IMGUI convention), for drawing the caption on it.</summary>
        public static Rect FramePixelRect(int screenW, int screenH, float aspect, float panelFraction)
        {
            if (screenW <= 0 || screenH <= 0) return new Rect(0f, 0f, Mathf.Max(0, screenW), Mathf.Max(0, screenH));
            float x, yTop, w, h;
            ComputeFrame(screenW, screenH, aspect, panelFraction, out x, out yTop, out w, out h);
            return new Rect(x, yTop, w, h);
        }

        static void ComputeFrame(int screenW, int screenH, float aspect, float panelFraction, out float x, out float yTop, out float w, out float h)
        {
            if (!(aspect > 0f)) aspect = DefaultAspect;
            float panelH = screenH * Mathf.Clamp01(panelFraction);
            float areaH = screenH - panelH;
            w = Mathf.Min(screenW, areaH * aspect);
            h = w / aspect;
            x = (screenW - w) * 0.5f;
            yTop = (areaH - h) * 0.5f;
        }

        /// <summary>
        /// Shrinks a font so a line of text fits: returns <paramref name="basePx"/> if the measured width fits in the
        /// available width, otherwise a proportionally smaller size (3% safety margin), never below <paramref name="minPx"/>
        /// and never above <paramref name="basePx"/>.
        /// </summary>
        public static int FitFontSize(int basePx, float measuredWidthPx, float availableWidthPx, int minPx)
        {
            if (!(measuredWidthPx > 0f) || !(availableWidthPx > 0f) || measuredWidthPx <= availableWidthPx) return basePx;
            int fitted = Mathf.FloorToInt(basePx * (availableWidthPx / measuredWidthPx) * 0.97f);
            return Mathf.Clamp(fitted, Mathf.Min(minPx, basePx), basePx);
        }

        /// <summary>
        /// True when an IMGUI surface of <paramref name="w"/> x <paramref name="h"/> pixels is the glasses' side-by-side
        /// target (two eyes of eyeW x eyeH = 3840x1080). IMGUI text must never be drawn there: it would split across the eyes.
        /// A defensive guard for the Editor, where a Game view showing the glasses' display could otherwise receive the panel.
        /// </summary>
        public static bool IsSbsGlassesSurface(int w, int h, int eyeW, int eyeH)
        {
            return eyeW > 0 && eyeH > 0 && w == 2 * eyeW && h == eyeH;
        }

        /// <summary>
        /// Whether the spectator flag turns the spectator off. "hostScreen" (the default in every profile) and anything
        /// unrecognised keep it on; "off", "none", "false", "disabled" or "0" (any case) switch it off.
        /// </summary>
        public static bool IsOffValue(string spectatorFlag)
        {
            if (string.IsNullOrEmpty(spectatorFlag)) return false;
            string v = spectatorFlag.Trim();
            return string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "disabled", StringComparison.OrdinalIgnoreCase)
                || v == "0";
        }

        /// <summary>
        /// Vertical field of view (degrees) of a pinhole camera with focal length <paramref name="fx"/> pixels and image
        /// height <paramref name="heightPx"/> pixels: fovY = 2 * atan((height / 2) / fx). Used by the webcam overlay so the
        /// virtual spectator camera projects the way the vision bridge's solvePnP assumed. Returns 0 for unusable inputs.
        /// </summary>
        public static float VerticalFovFromFx(float fx, int heightPx)
        {
            if (!(fx > 0f) || heightPx <= 0) return 0f;
            return 2f * Mathf.Atan((heightPx * 0.5f) / fx) * Mathf.Rad2Deg;
        }
    }
}
