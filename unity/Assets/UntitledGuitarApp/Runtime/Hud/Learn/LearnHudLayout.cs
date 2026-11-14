using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// Where the Learn HUD's parts go. Pure static maths (vectors in, vectors out; no engine objects), unit-tested.
    ///
    /// THE PINNED NECK FRAME (other code uses the same one): origin on the neck's centre line at the nut face, on the
    /// fretboard surface; +x along the neck toward the bridge; +y up out of the fretboard; +z across toward string 1
    /// (high E). Metres in transforms. Strings are numbered 1 (high E) to 6 (low E).
    ///
    /// The layout, as the wearer sees it (docs/MASTER_PLAN.md section 17):
    ///  * One message at a time (tip, strum verdict or finger prompt) sits on the "neck HUD card" spot: about 8 cm
    ///    above the fretboard at fret 5, turned to face the eyes. At a typical half metre that is under 10 degrees
    ///    from the neck, so it is read without looking away.
    ///  * The per-string marks sit in a row across the nut, just beyond it, spread wider than the real strings (which
    ///    are only 7 mm apart, less than half a degree) and ordered the way the strings appear: the layout of the X / O
    ///    row of a chord diagram.
    ///  * The tuner is placed relative to the head (the neck is not aligned yet while tuning).
    /// </summary>
    public static class LearnHudLayout
    {
        // ------------------------------------------------------------------ the neck HUD card

        /// <summary>A common steel-string scale length (25.5 inches), used only until the guitar's own measurement is supplied.</summary>
        public const float DefaultScaleLengthM = 0.648f;

        /// <summary>The message sits above this fret.</summary>
        public const int CardFret = 5;

        /// <summary>...this far out of the fretboard, metres (clear of the knuckles of the fretting hand).</summary>
        public const float CardHeightM = 0.08f;

        /// <summary>Everything needed while playing stays within about this angle of the neck.</summary>
        public const float GlanceDegrees = 10f;

        /// <summary>Distance from the nut to fret <paramref name="fret"/>: L * (1 - 2^(-n/12)) (docs/MASTER_PLAN.md Appendix B4).</summary>
        public static float FretDistanceM(float scaleLengthM, int fret)
        {
            if (!(scaleLengthM > 0f) || fret <= 0) return 0f;
            return scaleLengthM * (1f - Mathf.Pow(2f, -fret / 12f));
        }

        /// <summary>The card spot in the neck frame for a guitar of this scale length: above fret 5, 8 cm out of the fretboard.</summary>
        public static Vector3 NeckCardLocal(float scaleLengthM)
        {
            return NeckCardLocal(FretDistanceM(scaleLengthM, CardFret), CardHeightM);
        }

        /// <summary>The card spot in the neck frame, given its distance along the neck from the nut and its height above the fretboard.</summary>
        public static Vector3 NeckCardLocal(float alongNeckM, float heightM)
        {
            return new Vector3(alongNeckM, heightM, 0f);
        }

        // ------------------------------------------------------------------ a typical player (defaults, previews, tests)

        /// <summary>
        /// Where a seated right-handed player's eyes are in the neck frame, metres: 36 cm toward the bridge from the nut,
        /// 8 cm in front of the fretboard plane, 43 cm above the low-E edge. An ESTIMATE from body proportions (guitar
        /// tilted back about 25 degrees, headstock raised 18 degrees), used only until the neck is aligned to the head.
        /// </summary>
        public static readonly Vector3 TypicalEyeInNeckFrame = new Vector3(0.36f, 0.08f, -0.43f);

        /// <summary>The room's "up" in the neck frame for that same typical posture (unit length).</summary>
        public static readonly Vector3 TypicalUpInNeckFrame = new Vector3(-0.309f, 0.335f, -0.890f);

        // ------------------------------------------------------------------ facing the eyes

        /// <summary>
        /// The orientation that turns a HUD element at <paramref name="point"/> to face an eye at <paramref name="eye"/>.
        /// HudText is readable from behind its +Z axis, so <paramref name="forward"/> points from the eye to the point;
        /// <paramref name="up"/> is <paramref name="upHint"/> made square to it. Feed both to Quaternion.LookRotation.
        /// Returns false (and safe axes) when the eye is at the point.
        /// </summary>
        public static bool TryFacing(Vector3 eye, Vector3 point, Vector3 upHint, out Vector3 forward, out Vector3 up)
        {
            forward = Vector3.forward;
            up = Vector3.up;
            Vector3 f = point - eye;
            float length = f.magnitude;
            if (!(length > 1e-5f)) return false;
            f /= length;

            Vector3 u = upHint - Vector3.Dot(upHint, f) * f;
            if (!(u.sqrMagnitude > 1e-8f))
            {
                // The hint points along the line of sight: fall back to any direction square to it.
                Vector3 other = Mathf.Abs(f.y) < 0.9f ? Vector3.up : Vector3.right;
                u = other - Vector3.Dot(other, f) * f;
            }
            forward = f;
            up = u.normalized;
            return true;
        }

        // ------------------------------------------------------------------ angles as the eye sees them

        /// <summary>The angle in degrees between two points as seen from <paramref name="eye"/> (0 if either is at the eye).</summary>
        public static float AngleBetweenDegrees(Vector3 eye, Vector3 a, Vector3 b)
        {
            Vector3 da = a - eye;
            Vector3 db = b - eye;
            float la = da.magnitude;
            float lb = db.magnitude;
            if (!(la > 1e-6f) || !(lb > 1e-6f)) return 0f;
            float cos = Mathf.Clamp(Vector3.Dot(da, db) / (la * lb), -1f, 1f);
            return Mathf.Acos(cos) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// How far (degrees of view) <paramref name="point"/> is from the nearest part of the neck, taken as the line from
        /// <paramref name="nut"/> along <paramref name="towardBridge"/> for <paramref name="neckLengthM"/> metres.
        /// </summary>
        public static float AngleFromNeckDegrees(Vector3 eye, Vector3 point, Vector3 nut, Vector3 towardBridge, float neckLengthM)
        {
            const int Steps = 24;
            Vector3 along = towardBridge.sqrMagnitude > 1e-10f ? towardBridge.normalized : Vector3.right;
            float best = float.MaxValue;
            for (int i = 0; i <= Steps; i++)
            {
                Vector3 onNeck = nut + along * (neckLengthM * i / Steps);
                float angle = AngleBetweenDegrees(eye, point, onNeck);
                if (angle < best) best = angle;
            }
            return best;
        }

        // ------------------------------------------------------------------ per-string marks: a row across the nut

        /// <summary>Size of one mark, degrees.</summary>
        public const float MarkDegrees = 1.5f;

        /// <summary>Centre-to-centre spacing of the marks, degrees: wider than the mark, so six never touch.</summary>
        public const float MarkPitchDegrees = 1.8f;

        /// <summary>The row sits this far beyond the nut (over the headstock), metres, clear of the first fret.</summary>
        public const float MarkBeyondNutM = 0.03f;

        /// <summary>
        /// The row the marks sit in, for an eye at <paramref name="eye"/>: its centre (the nut centre moved
        /// <paramref name="beyondNutM"/> toward the headstock) and its direction. The row runs square to the neck as the
        /// eye sees it, and its direction points to the side where string 1 goes: the side string 1 appears on, which
        /// for a player looking down their own neck is also the side the fretboard faces. All vectors in one space
        /// (world, or the neck frame). Returns false when no row can be made (eye at the nut, or looking exactly along
        /// the neck); <paramref name="rowDirection"/> is then <paramref name="towardString1"/>.
        /// </summary>
        public static bool TryMarkRow(Vector3 eye, Vector3 nutCentre, Vector3 towardBridge, Vector3 towardString1,
            Vector3 outOfFretboard, float beyondNutM, out Vector3 rowCentre, out Vector3 rowDirection)
        {
            Vector3 x = towardBridge.sqrMagnitude > 1e-10f ? towardBridge.normalized : Vector3.right;
            Vector3 z = towardString1.sqrMagnitude > 1e-10f ? towardString1.normalized : Vector3.forward;
            rowCentre = nutCentre - x * beyondNutM;
            rowDirection = z;

            Vector3 view = rowCentre - eye;
            float distance = view.magnitude;
            if (!(distance > 1e-5f)) return false;
            view /= distance;

            Vector3 neckAsSeen = x - Vector3.Dot(x, view) * view;
            if (!(neckAsSeen.sqrMagnitude > 1e-6f)) return false;

            Vector3 row = Vector3.Cross(view, neckAsSeen).normalized;
            Vector3 string1Side = z + (outOfFretboard.sqrMagnitude > 1e-10f ? outOfFretboard.normalized : Vector3.zero);
            if (Vector3.Dot(row, string1Side) < 0f) row = -row;
            rowDirection = row;
            return true;
        }

        /// <summary>
        /// How many spacings from the row centre the mark for a string sits: string 1 at +2.5, string 6 at -2.5
        /// (positive = along the row direction). Strings outside 1..6 are clamped.
        /// </summary>
        public static float MarkSlotOffset(int stringNumber)
        {
            return 3.5f - Mathf.Clamp(stringNumber, 1, 6);
        }

        /// <summary>The position of the mark for <paramref name="stringNumber"/> in a row with <paramref name="pitchM"/> metres between marks.</summary>
        public static Vector3 MarkSlot(Vector3 rowCentre, Vector3 rowDirection, int stringNumber, float pitchM)
        {
            return rowCentre + rowDirection * (MarkSlotOffset(stringNumber) * pitchM);
        }

        // ------------------------------------------------------------------ head-relative placement

        /// <summary>The tuner sits straight ahead...</summary>
        public const float TunerAzimuthDegrees = 0f;

        /// <summary>...a little below the centre of view (so its tall dial stays inside the 27 degree high view)...</summary>
        public const float TunerElevationDegrees = -1f;

        /// <summary>...and this far away, metres (the distance at which the glasses' fine text is sharpest).</summary>
        public const float TunerDistanceM = 1f;

        /// <summary>Local position of the tuner under the head (identity rotation, +Z forward).</summary>
        public static Vector3 TunerHeadLocal()
        {
            return HudLayout.PointAtAngles(TunerAzimuthDegrees, TunerElevationDegrees, TunerDistanceM);
        }

        /// <summary>When there is no neck to attach to, the message goes here under the head: just below the centre of view.</summary>
        public const float FloatingMessageElevationDegrees = -6f;

        public const float FloatingMessageDistanceM = 1f;

        public static Vector3 FloatingMessageHeadLocal()
        {
            return HudLayout.PointAtAngles(0f, FloatingMessageElevationDegrees, FloatingMessageDistanceM);
        }

        /// <summary>True when a distance changed enough (more than <paramref name="fraction"/>) to be worth re-sizing text for.</summary>
        public static bool DistanceChanged(float appliedM, float nowM, float fraction)
        {
            if (!(nowM > 0f)) return false;
            if (!(appliedM > 0f)) return true;
            return Mathf.Abs(nowM - appliedM) > fraction * appliedM;
        }
    }
}
