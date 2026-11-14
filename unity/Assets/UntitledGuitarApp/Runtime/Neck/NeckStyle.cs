// NeckStyle: the look of the neck on an additive display, in one place. Black is invisible on the glasses, so only light
// exists: no filled fretboard, no dark plates, no outlines. Contrast comes from brightness, size and stroke weight.
//
// LINE WIDTHS ARE CHOSEN IN VISUAL ANGLE. One eye of the glasses shows 1920 x 1080 px over about 46 x 27 degrees, which is
// 39 px per degree at the centre (NeckMath.PixelsPerDegreeAtCentre), so 1 px is about 0.025 degrees. A line thinner than a
// pixel flickers as the head moves, and the shader spends about one pixel of each line on its anti-aliased edges. So no
// line is drawn narrower than 0.075 degrees (3 px: 2 px of body plus the two half-pixel edges). The angles below are turned
// into neck-local millimetres for the distance and scale the neck is actually shown at (Resolve).
//
// Hierarchy by brightness: fingertip dots (white-hot core, the brightest thing in view) > nut (a clear bright bar) >
// strings (thin, bright, cool white; the low strings are drawn thicker, as on a real guitar) > frets (dimmer, so the
// strings read first) > fretboard edges.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>Every size the neck is drawn with, in neck-local millimetres (before the neck transform's scale).</summary>
    public struct NeckSizes
    {
        public float stringThinMm;      // string 1 (high E)
        public float stringThickMm;     // string 6 (low E)
        public float fretMm;            // fret line width
        public float nutMm;             // nut bar thickness (along the neck)
        public float edgeMm;            // fretboard edge line width
        public float tailMm;            // strings and edges fade out over this length beyond the last fret
        public float strumHalfMm;       // the strings reach this far past the strum zone (aligned neck: half the patch there)
        public float strumFadeMm;       // and fade out over this length (aligned neck: the patch fades in and out)
        public float dotRadiusMm;       // fingertip dot core
        public float haloRadiusMm;      // soft glow around the dot
        public float hotRadiusMm;       // white-hot centre
        public float markRadiusMm;      // X / O mark at the nut
        public float markStrokeMm;      // stroke of the X and of the O ring
        public float markGapMm;         // gap between the nut and the marks
        public float beadRadiusMm;      // dotted leader from a dot to its finger number
        public float beadPitchMm;
        public float labelCapMm;        // finger-number digit height
        public float labelCellMm;       // room one digit needs along the neck
        public float labelGapMm;        // gap between the fretboard edge and the digits

        /// <summary>Line width of a string in mm: string 1 is the thinnest, string 6 the thickest.</summary>
        public float StringWidthMm(int stringNumber)
        {
            float t = (Mathf.Clamp(stringNumber, 1, NeckLayout.StringCount) - 1) / (float)(NeckLayout.StringCount - 1);
            return stringThinMm + (stringThickMm - stringThinMm) * t;
        }
    }

    public static class NeckStyle
    {
        // ---- sizes, in degrees of view (see the file header) --------------------------------------------------------------
        public const float MinLineDeg = 0.075f;          // 3 px on the glasses: the thinnest line that does not flicker
        public const float StringThinDeg = 0.075f;       // string 1
        public const float StringThickDeg = 0.115f;      // string 6 (4.5 px)
        public const float FretDeg = 0.075f;
        public const float NutDeg = 0.25f;               // a bar, 10 px
        public const float EdgeDeg = 0.075f;
        public const float MarkStrokeDeg = 0.095f;
        public const float BeadDeg = 0.10f;              // diameter of one leader bead (4 px)
        public const float BeadPitchDeg = 0.19f;
        public const float MinDotDeg = 0.40f;            // a fingertip dot is never smaller than this across (16 px)

        /// <summary>Finger-number height: the project minimum for text (MASTER_PLAN section 17), 2 d tan(1.2 deg / 2).</summary>
        public const float LabelCapDeg = 1.2f;

        /// <summary>Text grows by this factor when the largeText flag is on (the same factor the HUD uses).</summary>
        public const float LargeTextScale = 1.35f;

        // ---- sizes tied to the string spacing (fractions of the string pitch at the nut) -----------------------------------
        public const float DotRadiusPitch = 0.46f;       // the dot just fits between its neighbours
        public const float HaloRadiusPitch = 1.20f;
        public const float HotRadiusPitch = 0.24f;
        public const float MarkRadiusPitch = 0.44f;

        // ---- brightness (0..1 of full white) ------------------------------------------------------------------------------
        public const float StringIntensity = 0.80f;
        public const float FretIntensity = 0.34f;
        public const float NutIntensity = 1.00f;
        public const float EdgeIntensity = 0.24f;
        public const float HaloIntensity = 0.60f;
        public const float BeadIntensity = 0.65f;
        public const float LabelIntensity = 1.00f;       // full white: on an additive display anything less is only a dimmer white

        /// <summary>Everything that is not the active finger is shown at this brightness (GHOST_HANDS section 1).</summary>
        public const float InactiveGain = 0.30f;

        /// <summary>A string marked X (not played) is dimmed to this.</summary>
        public const float MutedStringGain = 0.35f;

        /// <summary>
        /// Brightness changes at most this much per second: the 0.3 to 1.0 swing takes 250 ms (MASTER_PLAN: morphs ease over
        /// 250 to 300 ms), so a full on-off-on cycle takes at least 0.5 s, 2 Hz, under the 3 Hz flash limit.
        /// </summary>
        public const float GainSlewPerSecond = (1f - InactiveGain) / 0.25f;

        /// <summary>New dots start at this brightness and ease up, instead of popping in.</summary>
        public const float DotFadeInStart = 0.15f;

        // ---- colours (sRGB) -----------------------------------------------------------------------------------------------
        public static readonly Color32 CoolWhite = new Color32(200, 228, 255, 255);    // strings, frets, edges
        public static readonly Color32 NutWhite = new Color32(236, 244, 255, 255);
        public static readonly Color32 DotWhite = new Color32(255, 255, 255, 255);
        public static readonly Color32 GhostBlue = new Color32(79, 195, 247, 255);     // #4FC3F7, the ghost hands' colour: the dots' glow
        public static readonly Color32 MarkOpen = new Color32(255, 255, 255, 255);     // O: white
        public static readonly Color32 MarkMuted = new Color32(255, 64, 48, 255);      // X: red (a little orange, so it is not dim on the display)
        public static readonly Color32 Finger1 = new Color32(255, 235, 59, 255);       // yellow
        public static readonly Color32 Finger2 = new Color32(255, 64, 255, 255);       // magenta
        public static readonly Color32 Finger3 = new Color32(174, 255, 64, 255);       // lime
        public static readonly Color32 Finger4 = new Color32(255, 152, 0, 255);        // orange
        public static readonly Color32 DebugX = new Color32(255, 60, 60, 255);
        public static readonly Color32 DebugY = new Color32(60, 255, 60, 255);
        public static readonly Color32 DebugZ = new Color32(80, 140, 255, 255);

        /// <summary>
        /// The colour of one finger when the ghost.fingerColors flag is on: 1 yellow, 2 magenta, 3 lime, 4 orange (MASTER_PLAN
        /// section 17). Anything else is white. The digit, not the colour, carries the meaning.
        /// </summary>
        public static Color32 FingerColor(int finger)
        {
            switch (finger)
            {
                case 1: return Finger1;
                case 2: return Finger2;
                case 3: return Finger3;
                case 4: return Finger4;
                default: return DotWhite;
            }
        }

        /// <summary>The colour scaled toward black: on an additive display that is simply "dimmer".</summary>
        public static Color32 Dim(Color32 c, float intensity)
        {
            float k = Mathf.Clamp01(intensity);
            return new Color32((byte)(c.r * k + 0.5f), (byte)(c.g * k + 0.5f), (byte)(c.b * k + 0.5f), c.a);
        }

        /// <summary>The colour moved toward white by <paramref name="amount"/> (0..1): brighter, less saturated.</summary>
        public static Color32 Lighten(Color32 c, float amount)
        {
            float k = Mathf.Clamp01(amount);
            return new Color32((byte)(c.r + (255 - c.r) * k + 0.5f), (byte)(c.g + (255 - c.g) * k + 0.5f), (byte)(c.b + (255 - c.b) * k + 0.5f), c.a);
        }

        /// <summary>Degrees of view at <paramref name="viewDistanceM"/> to neck-local millimetres for a neck drawn at <paramref name="scale"/>.</summary>
        public static float AngleToLocalMm(float degrees, float viewDistanceM, float scale)
        {
            float s = scale > 1e-4f ? scale : 1f;
            return NeckMath.SizeForAngle(degrees, viewDistanceM) * 1000f / s;
        }

        /// <summary>The digit height in degrees for the current text setting.</summary>
        public static float LabelCapDegrees(bool largeText)
        {
            return largeText ? LabelCapDeg * LargeTextScale : LabelCapDeg;
        }

        /// <summary>
        /// Turns the visual angles above into neck-local millimetres for a neck seen from <paramref name="viewDistanceM"/> and
        /// drawn at <paramref name="scale"/> (the magnitude of the neck transform's scale).
        /// </summary>
        public static NeckSizes Resolve(NeckLayout layout, float viewDistanceM, float scale, bool largeText)
        {
            float pitch = Mathf.Max(0.5f, NeckMath.StringPitchAtNutMm(layout));
            float cap = AngleToLocalMm(LabelCapDegrees(largeText), viewDistanceM, scale);
            float lastFretSpacing = layout.LastFret >= 1 ? layout.fretXMm[layout.LastFret] - layout.fretXMm[layout.LastFret - 1] : 30f;

            NeckSizes s;
            s.stringThinMm = AngleToLocalMm(StringThinDeg, viewDistanceM, scale);
            s.stringThickMm = AngleToLocalMm(StringThickDeg, viewDistanceM, scale);
            s.fretMm = AngleToLocalMm(FretDeg, viewDistanceM, scale);
            s.nutMm = AngleToLocalMm(NutDeg, viewDistanceM, scale);
            s.edgeMm = AngleToLocalMm(EdgeDeg, viewDistanceM, scale);
            s.tailMm = 0.7f * lastFretSpacing;
            s.strumHalfMm = 40f;
            s.strumFadeMm = 18f;
            s.dotRadiusMm = Mathf.Max(DotRadiusPitch * pitch, 0.5f * AngleToLocalMm(MinDotDeg, viewDistanceM, scale));
            s.haloRadiusMm = s.dotRadiusMm * (HaloRadiusPitch / DotRadiusPitch);
            s.hotRadiusMm = s.dotRadiusMm * (HotRadiusPitch / DotRadiusPitch);
            s.markRadiusMm = MarkRadiusPitch * pitch;
            s.markStrokeMm = AngleToLocalMm(MarkStrokeDeg, viewDistanceM, scale);
            s.markGapMm = 0.35f * pitch;
            s.beadRadiusMm = 0.5f * AngleToLocalMm(BeadDeg, viewDistanceM, scale);
            s.beadPitchMm = AngleToLocalMm(BeadPitchDeg, viewDistanceM, scale);
            s.labelCapMm = cap;
            s.labelCellMm = 0.80f * cap;    // an Arial digit is 0.556 em wide and 0.716 em tall: 0.78 of its height
            s.labelGapMm = 0.30f * cap;
            return s;
        }
    }
}
