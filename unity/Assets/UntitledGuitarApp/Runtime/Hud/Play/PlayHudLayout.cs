// PlayHudLayout: every number and every piece of placement maths for the play-along HUD, in one place, so it can be tuned
// on the glasses without hunting through components. Pure static functions, no engine state, no allocation, unit-tested
// (Tests/EditMode/HudPlayTests.cs). Only managed maths is used (Vector3 arithmetic, Mathf), so the functions also run
// outside the editor.
//
// PINNED NECK FRAME (shared with the other workers): origin on the neck's centre line at the nut face, on the fretboard
// surface; +x along the neck toward the bridge; +y up out of the fretboard; +z across toward string 1 (high E).
// Millimetres in data, metres in transforms.
//
// THE CARD. docs/MASTER_PLAN.md section 17: "The neck HUD card floats about 8 cm above the neck near fret 5, angled toward
// the eyes", and everything needed while playing sits within about 10 degrees of the gaze at the fretboard. The card is a
// screen-aligned column (it always faces the eyes and stays upright in the display), laid out in DEGREES OF VIEW relative to
// its anchor, +x to the viewer's right and +y up:
//
//      +12.0   chord names above the words      (lyric strip, optional; a chords-only song shows its
//       +9.8   current lyric line                chord sequence with one tick per beat here instead)
//       +7.4   next lyric line (smaller)
//       +3.0   CURRENT CHORD (4 degrees)   |  "next" + next chord
//       -0.9   beat dots (countdown to the change)  /  wait cue
//       -4.2   strum or picking strip, cursor lane under it
//
// Why a narrow, tall column: seen by a seated right-handed player the neck recedes steeply up and to the left, the fretting
// hand sits about 11 degrees left of the anchor and its wrist reaches to about 7 degrees left of it, so the column starts
// 6.5 degrees left of the anchor and grows to the right, where the view is free. The numbers come from a posture model
// (eyes 0.6 m from the nut), not from the device: tune them on the glasses.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>Layout constants and pure placement maths for the play-along HUD.</summary>
    public static class PlayHudLayout
    {
        // ------------------------------------------------------------------ the anchor on the neck

        /// <summary>Scale length used to find fret 5 when the caller does not know the guitar's (25.5 inch).</summary>
        public const float DefaultScaleLengthMm = 648f;

        /// <summary>The card sits near this fret.</summary>
        public const float DefaultAnchorFret = 5f;

        /// <summary>The card floats this far from the neck's centre line (millimetres of the neck frame).</summary>
        public const float DefaultLiftMm = 80f;

        /// <summary>
        /// The children of the card are sized for a viewer this far away; the card then scales itself by
        /// actual distance / this, so every angular size holds at any distance without touching a text.
        /// </summary>
        public const float DesignDistanceM = 0.5f;

        /// <summary>Below this sine of the angle between the sight line and the fretboard plane's lift candidate, the view counts as face-on (floating neck).</summary>
        public const float FaceOnThreshold = 0.34f;

        // ------------------------------------------------------------------ the column (degrees relative to the anchor)

        /// <summary>Left edge of the column: clear of the fretting hand's wrist.</summary>
        public const float ColumnLeftDeg = -6.5f;

        /// <summary>Widest thing the column may hold (the view is only 46 degrees wide and the column starts right of the hand).</summary>
        public const float ColumnMaxWidthDeg = 18f;

        public const float ChordRowDeg = 3.0f;
        /// <summary>Current chord cap height. Same as ChordLabel.CurrentDegrees; never below HudText.ChordNameMinDegrees.</summary>
        public const float ChordCapDeg = 4f;

        /// <summary>Left edge of the "next chord" block, right of the big chord.</summary>
        public const float NextBlockLeftDeg = 4.2f;
        public const float NextWordRowDeg = 4.6f;
        public const float NextWordCapDeg = 1.2f;
        public const float NextChordRowDeg = 2.1f;
        public const float NextChordCapDeg = 2.2f;

        public const float DotsRowDeg = -0.9f;
        public const float DotPitchDeg = 1.3f;
        public const float DotPassedDeg = 0.42f;
        public const float DotUpcomingDeg = 0.62f;
        public const float DotCurrentDeg = 0.95f;

        public const float WaitCueCapDeg = 1.8f;

        public const float PatternRowDeg = -4.2f;
        public const float PatternPitchDeg = 1.8f;
        public const float PatternMinPitchDeg = 1.1f;
        public const float PatternGlyphDeg = 1.9f;
        public const float PatternDigitCapDeg = 1.7f;
        /// <summary>Centre of the cursor lane, below the glyph row centre.</summary>
        public const float PatternLaneDropDeg = 1.55f;
        public const float PatternCaretDeg = 0.7f;

        /// <summary>Row of the current lyric line (or, for a chords-only line, the reference row of the chord sequence).</summary>
        public const float LyricRowDeg = 9.8f;

        // The lyric strip, relative to its own origin (the left end of the current line, at the middle of its letters).
        public const float LyricLineCapDeg = 1.5f;
        public const float LyricChordRiseDeg = 2.25f;
        public const float LyricChordCapDeg = 1.5f;
        public const float LyricMarkerRiseDeg = 1.2f;
        public const float LyricMarkerWidthDeg = 1.1f;
        public const float LyricMarkerHeightDeg = 0.2f;
        public const float LyricCaretDropDeg = 1.25f;
        public const float LyricCaretDeg = 0.6f;
        public const float LyricNextDropDeg = 2.45f;
        public const float LyricNextCapDeg = HudText.MinTextDegrees;

        // A chords-only line: chord names over one tick per beat, the next line's names smaller underneath.
        public const float SequenceNameRiseDeg = 1.25f;
        public const float SequenceNameCapDeg = 1.6f;
        public const float SequenceTickDropDeg = 0.3f;
        public const float SequenceNextDropDeg = 2.3f;
        public const float SequenceNextCapDeg = HudText.MinTextDegrees;
        public const float SequenceMaxBeatPitchDeg = 2.4f;
        public const float SequenceMinBeatPitchDeg = 0.85f;

        /// <summary>The count-in digit: large, in the "next chord" block (nothing there is needed before the song starts).</summary>
        public const float CountInCapDeg = 5f;
        public const float CountInXDeg = 7.6f;
        public const float CountInRowDeg = 3.0f;

        // ------------------------------------------------------------------ the glasses

        /// <summary>One eye of the glasses: 52 degrees diagonal at 16:9 is 46.1 by 26.9 degrees.</summary>
        public const float ViewWidthDeg = 46.06f;
        public const float ViewHeightDeg = 26.9f;

        /// <summary>What is needed while playing stays within about this many degrees of the anchor row.</summary>
        public const float PlayZoneDeg = 10f;

        // ------------------------------------------------------------------ sizes

        /// <summary>Text size with the largeText flag applied: bigger only, and never below the project minimum.</summary>
        public static float TextDeg(float capDegrees, bool largeText)
        {
            return Mathf.Max(capDegrees * HudText.TextScale(largeText), HudText.MinTextDegrees);
        }

        /// <summary>World size in metres of something spanning <paramref name="degrees"/> at <paramref name="distanceM"/> (same formula as HudText).</summary>
        public static float Size(float degrees, float distanceM)
        {
            return HudText.SizeForAngularHeight(degrees, distanceM);
        }

        /// <summary>Distance in metres from the card's centre of a point <paramref name="degrees"/> off-centre on a flat card (d * tan). Signed.</summary>
        public static float Offset(float degrees, float distanceM)
        {
            if (!(distanceM > 0f) || float.IsNaN(degrees)) return 0f;
            return distanceM * Mathf.Tan(Mathf.Clamp(degrees, -80f, 80f) * Mathf.Deg2Rad);
        }

        /// <summary>The scale the card applies to itself so sizes authored for <paramref name="designM"/> hold at <paramref name="actualM"/>. 1 for unusable input.</summary>
        public static float ScaleForDistance(float actualM, float designM)
        {
            if (!(actualM > 1e-4f) || !(designM > 1e-4f) || float.IsInfinity(actualM)) return 1f;
            return actualM / designM;
        }

        /// <summary>
        /// Cap height (degrees) for a line that is <paramref name="widthEm"/> ems wide: the preferred size if the line then
        /// fits in <paramref name="maxWidthDeg"/>, otherwise as large as fits, but never below <paramref name="minDeg"/>
        /// (a line that is still too long at the minimum simply runs wider). <paramref name="capPerEm"/> is the font's cap
        /// height in ems (Arial: 0.716).
        /// </summary>
        public static float FitCapDegrees(float widthEm, float preferredDeg, float maxWidthDeg, float minDeg, float capPerEm)
        {
            float floor = Mathf.Max(minDeg, HudText.MinTextDegrees);
            float preferred = Mathf.Max(preferredDeg, floor);
            if (!(widthEm > 0f) || !(maxWidthDeg > 0f) || !(capPerEm > 0f)) return preferred;
            float fitting = maxWidthDeg * capPerEm / widthEm;
            return Mathf.Max(floor, Mathf.Min(preferred, fitting));
        }

        /// <summary>Cell pitch (degrees) of a strip of <paramref name="slots"/> cells: the preferred pitch, squeezed so the strip fits the column, never below the minimum.</summary>
        public static float PatternPitch(int slots, float preferredDeg, float maxWidthDeg, float minDeg)
        {
            if (slots <= 0) return preferredDeg;
            return Mathf.Max(minDeg, Mathf.Min(preferredDeg, maxWidthDeg / slots));
        }

        // ------------------------------------------------------------------ the neck

        /// <summary>Distance from the nut to fret <paramref name="fret"/>: L * (1 - 2^(-fret/12)). Fret 5 of a 648 mm scale is 162.5 mm.</summary>
        public static float FretDistanceMm(float scaleLengthMm, float fret)
        {
            if (!(scaleLengthMm > 0f) || float.IsNaN(fret)) return 0f;
            return scaleLengthMm * (1f - Mathf.Pow(2f, -fret / 12f));
        }

        /// <summary>
        /// The direction (unit, world) in which the card is lifted off the neck: perpendicular to the neck and to the line of
        /// sight, so 8 cm of lift is 8 cm of visible separation and the card never hides the neck. When the fretboard is seen
        /// at a grazing angle (the real guitar on your lap) that is its front side, +y of the neck frame. When it is seen
        /// face-on (the floating neck) +y points at the eyes, so the card goes up in the display instead.
        /// </summary>
        /// <param name="neckX">neck axis toward the bridge (world, any length)</param>
        /// <param name="neckY">out of the fretboard (world, any length)</param>
        /// <param name="toEye">from the point on the neck to the eyes (world, any length)</param>
        /// <param name="headUp">the display's up direction (world); used only for the face-on case</param>
        public static Vector3 LiftDirection(Vector3 neckX, Vector3 neckY, Vector3 toEye, Vector3 headUp)
        {
            Vector3 x = SafeNormalize(neckX, Vector3.right);
            Vector3 y = SafeNormalize(neckY, Vector3.up);
            Vector3 s = SafeNormalize(toEye, y);

            Vector3 w = Vector3.Cross(x, s);
            if (w.sqrMagnitude < 1e-8f) return y;   // looking straight down the neck: nothing better than +y
            w = w.normalized;

            float alongY = Vector3.Dot(w, y);
            if (Mathf.Abs(alongY) >= FaceOnThreshold) return alongY >= 0f ? w : -w;

            float alongUp = Vector3.Dot(w, headUp);
            return alongUp >= 0f ? w : -w;
        }

        /// <summary>
        /// Axes of a card at <paramref name="cardPos"/> that faces the eyes at <paramref name="eyePos"/> and is upright with
        /// respect to <paramref name="upHint"/> (the head's up, so text is level in the display). Text built by HudText reads
        /// correctly when its +z is <paramref name="forward"/> (away from the viewer) and its +y is <paramref name="up"/>.
        /// </summary>
        public static void BillboardBasis(Vector3 cardPos, Vector3 eyePos, Vector3 upHint, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            forward = SafeNormalize(cardPos - eyePos, Vector3.forward);
            Vector3 hint = SafeNormalize(upHint, Vector3.up);
            Vector3 r = Vector3.Cross(hint, forward);
            if (r.sqrMagnitude < 1e-6f)
            {
                // Looking along the up hint: any upright will do, pick a stable one.
                r = Vector3.Cross(Mathf.Abs(forward.y) < 0.9f ? Vector3.up : Vector3.right, forward);
            }
            right = r.normalized;
            up = Vector3.Cross(forward, right);
        }

        /// <summary>
        /// Where the card's anchor is, in world space. <paramref name="neckOrigin"/>, <paramref name="neckX"/> and
        /// <paramref name="neckY"/> are the neck frame in world space with the axes scaled to "world metres per neck metre"
        /// (so a 1.2x floating neck moves the card 1.2x as far).
        /// </summary>
        public static Vector3 AnchorWorld(Vector3 neckOrigin, Vector3 neckX, Vector3 neckY, Vector3 eyePos, Vector3 headUp,
            float alongNeckMm, float liftMm)
        {
            Vector3 basePoint = neckOrigin + neckX * (alongNeckMm * 0.001f);
            Vector3 lift = LiftDirection(neckX, neckY, eyePos - basePoint, headUp);
            float metresPerNeckMetre = neckY.magnitude;
            if (!(metresPerNeckMetre > 1e-6f)) metresPerNeckMetre = 1f;
            return basePoint + lift * (liftMm * 0.001f * metresPerNeckMetre);
        }

        /// <summary>Angle in degrees between two directions as seen from the eyes (0 for unusable input). For layout checks.</summary>
        public static float AngleBetween(Vector3 a, Vector3 b)
        {
            float la = a.magnitude, lb = b.magnitude;
            if (!(la > 1e-9f) || !(lb > 1e-9f)) return 0f;
            float c = Mathf.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f);
            return Mathf.Acos(c) * Mathf.Rad2Deg;
        }

        /// <summary>a mod m, always in [0, m). 0 when m is not positive.</summary>
        public static float PositiveMod(float a, float m)
        {
            if (!(m > 0f) || float.IsNaN(a) || float.IsInfinity(a)) return 0f;
            float r = a - Mathf.Floor(a / m) * m;
            return r >= m ? 0f : r;
        }

        /// <summary>a mod m for integers, always in [0, m). 0 when m is not positive.</summary>
        public static int PositiveMod(int a, int m)
        {
            if (m <= 0) return 0;
            int r = a % m;
            return r < 0 ? r + m : r;
        }

        /// <summary>Hermite ease 0..1 (clamped): slow in, slow out. The HUD's only easing curve.</summary>
        public static float Ease01(float t)
        {
            if (float.IsNaN(t) || t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float m = v.magnitude;
            return m > 1e-9f && !float.IsInfinity(m) ? v / m : fallback;
        }
    }
}
