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
// the eyes". The card faces the eyes and stays upright in the display; it is laid out in DEGREES OF VIEW relative to its
// anchor (fret 5, lifted), +x to the viewer's right and +y up. Blocks are STACKED from sizes (Blocks), so large text grows
// them apart instead of onto each other.
//
// ZONE MAP (Sat 12:25, coordinator's decision; floating neck, default placement, LEFT eye, measured on the 11:38 player
// frame). The card's anchor sits at view (+0.61, +2.15) deg. Taken: the fretting hand az -10..-2 from the strings up to
// el +5.3; strings el -2.4..-5.5 along the whole neck; finger numbers down to el -7.5; the X/O column az -15..-13; the
// strumming hand az +8..+14, el -1..-7; the SIMULATED badge el -11.2..-9.3. Comfortable: every ink edge within az +-21 and
// el +-11.4. So, in view degrees (anchor-relative = view - (0.61, 2.15)):
//
//      el  +7.5..+11.2  lyric strip, ONE line (words with chords above, or a chords-only song's sequence)
//      el  +1.6..+5.8   CURRENT CHORD (4 deg) from az -1.4  |  "next" + next chord from az +8.6; count-in digit there
//      el  -0.4..+1.4   beat dots under the "next" block (they count down to it); the wait cue replaces both
//      el  -1.9..~+1.2  strum / picking strip between the hands (az -1.4..+7.4), never on the strings
//
// The aligned (real guitar) view uses the same anchor-relative blocks; it has not been checked against a zone map.
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

        // ------------------------------------------------------------------ the blocks (degrees relative to the anchor)

        /// <summary>Left edge of the pattern strip, the chord name and the lyric strip: 0.6 deg right of the fretting hand.</summary>
        public const float ColumnLeftDeg = -2.0f;

        /// <summary>Widest the lyric strip may get (it starts at the column's left edge and must end inside the comfortable zone).</summary>
        public const float ColumnMaxWidthDeg = 18f;

        /// <summary>The comfortable zone's right edge relative to the anchor of the default floating neck (view az +21).</summary>
        public const float ZoneRightDeg = 20.4f;

        /// <summary>Lowest ink of the chord name, at the top of its wait-mode breath.</summary>
        public const float ChordBottomDeg = -0.55f;
        /// <summary>Current chord cap height. Same as ChordLabel.CurrentDegrees; never below HudText.ChordNameMinDegrees.</summary>
        public const float ChordCapDeg = 4f;
        /// <summary>Row (middle of the capitals) of the chord name at normal text size.</summary>
        public const float ChordRowDeg = ChordBottomDeg + ChordCapDeg * 0.5f * (1f + NeckHudCard.WaitBreathGain);

        /// <summary>Width of the widest chord name the songs use ("Am", "Em" in bold Arial), in ems: the next block starts after it.</summary>
        public const float WidestChordEm = 1.62f;
        /// <summary>Gap between the widest chord name and the "next" block.</summary>
        public const float NextBlockGapDeg = 1.0f;
        /// <summary>Left edge of the "next chord" block at normal text size.</summary>
        public const float NextBlockLeftDeg = ColumnLeftDeg + WidestChordEm * ChordCapDeg / 0.716f + NextBlockGapDeg;
        public const float NextWordCapDeg = 1.2f;
        public const float NextChordCapDeg = 2.2f;
        public const float NextWordGapDeg = 0.4f;
        /// <summary>Rows at normal text size: the next chord sits on the chord name's baseline, "next" above it.</summary>
        public const float NextChordRowDeg = ChordBottomDeg + NextChordCapDeg * 0.5f;
        public const float NextWordRowDeg = ChordBottomDeg + NextChordCapDeg + NextWordGapDeg + NextWordCapDeg * 0.5f;

        /// <summary>Beat dots: under the "next" block, clear of the strumming hand below them (view el -1).</summary>
        public const float DotsRowDeg = -1.55f;
        /// <summary>Least gap between the biggest beat dot and the next chord above it.</summary>
        public const float DotsGapDeg = 0.2f;

        /// <summary>The comfortable zone's top edge relative to the anchor of the default floating neck (view el +11.4).</summary>
        public const float ZoneTopDeg = 9.25f;
        public const float DotPitchDeg = 1.3f;
        public const float DotPassedDeg = 0.42f;
        public const float DotUpcomingDeg = 0.62f;
        public const float DotCurrentDeg = 0.95f;

        /// <summary>Wait cue ("|| strum Am"), in the "next" block; shrunk (never below the minimum) to end inside the zone.</summary>
        public const float WaitCueCapDeg = 1.5f;
        /// <summary>Width of the widest wait cue in ems: the pause mark (1.25 cap heights) plus "strum Am".</summary>
        public const float WaitCueWidestEm = 5.6f;

        /// <summary>Lowest ink of the pattern strip (its cursor lane): 0.5 deg above the top of the strings.</summary>
        public const float PatternBottomDeg = -4.0f;
        /// <summary>The strip fits between the fretting hand and the strumming hand (view az -1.4..+7.4).</summary>
        public const float PatternMaxWidthDeg = 8.8f;
        /// <summary>Legacy name: the strip's origin row. With PatternStrip.AnchorBottom the origin is the strip's lowest ink.</summary>
        public const float PatternRowDeg = PatternBottomDeg;
        public const float PatternPitchDeg = 1.8f;
        public const float PatternMinPitchDeg = 1.1f;
        public const float PatternGlyphDeg = 1.9f;
        public const float PatternDigitCapDeg = 1.7f;
        /// <summary>Least drop of the cursor lane below the glyph row centre (the lane also clears the cells' lowest ink).</summary>
        public const float PatternLaneDropDeg = 1.55f;
        public const float PatternCaretDeg = 0.7f;
        /// <summary>Gap between the cells' lowest ink and the top of the cursor lane.</summary>
        public const float PatternLaneGapDeg = 0.25f;

        /// <summary>Gap between the chord name's top and the lowest ink of the lyric strip (its caret).</summary>
        public const float LyricGapDeg = 0.5f;

        // The lyric strip, relative to its own origin (the left end of the current line, at the middle of its letters).
        public const float LyricLineCapDeg = 1.5f;
        public const float LyricChordRiseDeg = 2.25f;
        public const float LyricChordCapDeg = 1.5f;
        public const float LyricMarkerRiseDeg = 1.2f;
        public const float LyricMarkerWidthDeg = 1.1f;
        public const float LyricMarkerHeightDeg = 0.2f;
        // The caret glides between the descenders of the line above it (p, y: 0.3 cap heights below the baseline) and the
        // ascenders of the next line below it (b, d, h: as tall as capitals). It used to sit 0.24 degrees inside the descenders.
        public const float LyricCaretDropDeg = 1.6f;
        public const float LyricCaretDeg = 0.5f;
        public const float LyricNextDropDeg = 2.8f;

        /// <summary>Row of the current lyric line at normal text size: above the chord name, its caret clear of it.</summary>
        public const float LyricRowDeg = ChordBottomDeg + ChordCapDeg * (1f + NeckHudCard.WaitBreathGain) + LyricGapDeg
            + LyricCaretDropDeg + LyricCaretDeg * 0.5f;
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
        /// <summary>Half the width of a digit in bold Arial, ems (the count-in is centred on its digit).</summary>
        public const float DigitHalfEm = 0.278f;
        public const float CountInXDeg = NextBlockLeftDeg + DigitHalfEm * CountInCapDeg / 0.716f;
        public const float CountInRowDeg = ChordRowDeg;

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

        /// <summary>Where each block of the card goes for a text scale (1, or HudText.LargeTextScale), degrees relative to the anchor.</summary>
        public struct CardBlocks
        {
            public float ColumnLeft;
            public float PatternBottom, PatternMaxWidth;
            public float ChordCap, ChordRow, ChordTop;
            public float NextLeft, NextChordCap, NextChordRow, NextWordCap, NextWordRow;
            public float CountInCap, CountInX, CountInRow;
            public float DotsLeft, DotsRow, DotsFirstX;
            public int DotsMaxVisible;
            public float WaitCap, WaitRow;
            public float LyricRow, LyricTop;
            /// <summary>False when the lyric strip would end above the comfortable zone (large text): the card hides it.</summary>
            public bool LyricFits;
        }

        /// <summary>
        /// The card's blocks, stacked from their sizes: chord name on its fixed baseline, the "next" block right of the widest
        /// chord name, the beat dots under it (only as many as end inside the zone), the wait cue shrunk to end inside the
        /// zone, the lyric strip above the chord name with its caret clear of it. Pure.
        /// </summary>
        public static CardBlocks Blocks(float textScale)
        {
            float s = textScale > 0f && !float.IsNaN(textScale) ? textScale : 1f;
            const float capPerEm = 0.716f;
            var b = new CardBlocks();
            b.ColumnLeft = ColumnLeftDeg;
            b.PatternBottom = PatternBottomDeg;
            b.PatternMaxWidth = PatternMaxWidthDeg;

            float breath = 1f + NeckHudCard.WaitBreathGain;
            b.ChordCap = Mathf.Max(ChordCapDeg * s, HudText.ChordNameMinDegrees);
            b.ChordRow = ChordBottomDeg + b.ChordCap * 0.5f * breath;
            b.ChordTop = ChordBottomDeg + b.ChordCap * breath;

            float maxDot = DotCurrentDeg * s * (1f + BeatDots.PulseGain) * BeatDots.DownbeatGain;
            b.NextLeft = ColumnLeftDeg + WidestChordEm * b.ChordCap / capPerEm + NextBlockGapDeg * s;
            float nextBottom = Mathf.Max(ChordBottomDeg, DotsRowDeg + 0.5f * maxDot + DotsGapDeg);
            b.NextChordCap = Mathf.Max(NextChordCapDeg * s, HudText.MinTextDegrees);
            b.NextChordRow = nextBottom + b.NextChordCap * 0.5f;
            b.NextWordCap = Mathf.Max(NextWordCapDeg * s, HudText.MinTextDegrees);
            b.NextWordRow = nextBottom + b.NextChordCap + NextWordGapDeg * s + b.NextWordCap * 0.5f;

            b.CountInCap = Mathf.Max(CountInCapDeg * s, HudText.MinTextDegrees);
            b.CountInX = b.NextLeft + DigitHalfEm * b.CountInCap / capPerEm;
            b.CountInRow = b.ChordRow;

            b.DotsLeft = b.NextLeft;
            b.DotsRow = DotsRowDeg;
            b.DotsFirstX = b.NextLeft + 0.5f * maxDot;                 // the biggest (pulsing downbeat) dot starts at the block's edge
            float room = ZoneRightDeg - b.DotsFirstX - 0.5f * maxDot;
            b.DotsMaxVisible = Mathf.Clamp(Mathf.FloorToInt(room / (DotPitchDeg * s)) + 1, 1, BeatDots.MaxDots);

            b.WaitCap = FitCapDegrees(WaitCueWidestEm, WaitCueCapDeg * s, ZoneRightDeg - b.NextLeft, HudText.MinTextDegrees, capPerEm);
            b.WaitRow = b.NextChordRow;

            b.LyricRow = b.ChordTop + LyricGapDeg * s + (LyricCaretDropDeg + LyricCaretDeg * 0.5f) * s;
            b.LyricTop = b.LyricRow + (LyricChordRiseDeg + LyricChordCapDeg * 0.5f) * s;
            b.LyricFits = b.LyricTop <= ZoneTopDeg + 1e-3f;
            return b;
        }

        /// <summary>
        /// World size in metres of something spanning <paramref name="degrees"/> at <paramref name="distanceM"/> (same formula
        /// as HudText: 2 d tan(degrees / 2)). SIGNED: a negative angle gives a negative length, so a part placed below or
        /// left of its origin stays there. (It used to return HudText.SizeForAngularHeight, which is 0 for a negative
        /// angle; Hud/Learn had the same trap, see LearnHudLayout.SignedMeters.) 0 for a non-finite angle or an unusable distance.
        /// </summary>
        public static float Size(float degrees, float distanceM)
        {
            return SignedMeters(degrees, distanceM);
        }

        /// <summary>
        /// The signed version of HudText.SizeForAngularHeight: the length in metres that <paramref name="degrees"/> of view
        /// spans at <paramref name="distanceM"/>, with the sign of the angle. Use it (or <see cref="Offset"/>) for anything
        /// that is a displacement; HudText.SizeForAngularHeight returns 0 for a negative angle. 0 for a non-finite angle or an
        /// unusable distance.
        /// </summary>
        public static float SignedMeters(float degrees, float distanceM)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0f;
            float meters = HudText.SizeForAngularHeight(Mathf.Abs(degrees), distanceM);
            return degrees < 0f ? -meters : meters;
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
