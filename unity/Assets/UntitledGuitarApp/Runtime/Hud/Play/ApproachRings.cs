// ApproachRings: osu!-style timing rings for chord changes (docs/MASTER_PLAN.md section 23, M11). For each fingertip
// position of the NEXT chord a ring appears large and faint, shrinks at a steady speed, and lands exactly on the spot at the
// moment of the change; a thin ring marks the spot it is heading for. "When" is read from the size of the ring, never from
// colour.
//
// TIME. Everything is a pure function of (hit time - now), both on the caller's clock (the song clock of the practice
// session). There is no clock in here, so the rings cannot drift from the music, they slow down with the tempo preset, and
// they freeze when the playhead holds: in wait mode keep calling Tick with the held time and the landed ring simply stays,
// bright, on the spot ("play it now") until time moves on. The radius is LINEAR in time on purpose: a ring that closes at a
// constant speed lets the eye predict the landing; easing is used only for the fade-in, the landing swell and the fade-out.
//
// SPACE. Targets arrive in the pinned neck frame in millimetres (origin on the centre line at the nut on the fretboard, +x
// to the bridge, +y out of the fretboard, +z to string 1). Rings lie on the fretboard plane (section 17), except that a
// seated player sees the fretboard at a grazing angle of only about 5 to 20 degrees, where a flat ring would be a sliver:
// each ring is tipped toward the eyes just enough to be seen at no less than MinViewElevationDeg (RingNormal).
// The stroke keeps its weight while the radius changes (the ring's vertices are rewritten in place, no allocation).
//
// Parent it under anything with identity scale (the HUD root or nothing): ring poses are set in world space every Tick.
//
// SOURCES: Transform.TransformPoint.html, Quaternion.LookRotation.html, Mesh.SetVertices.html, Mesh.MarkDynamic.html
// ("Call this before assigning vertices to get better performance when continually updating the Mesh") in the offline
// documentation of Unity 2021.3.3f1 (Unity.app/Contents/Documentation/en/ScriptReference).
//
// UNVERIFIED (nobody has run this in Unity yet): everything visual; sizes and timings are first guesses to tune on the glasses.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// Approach rings. <see cref="Attach"/> it to the neck socket and the head once, call <see cref="Schedule"/> when a
    /// chord change is coming up (again with the same hit time is free) and <see cref="Tick"/> every frame. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ApproachRings : MonoBehaviour
    {
        /// <summary>Most fingertip targets per chord change.</summary>
        public const int MaxRings = 6;

        /// <summary>Changes that can be on their way at once (a one-beat chord is followed by its next change before it has landed).</summary>
        public const int MaxChanges = 2;

        /// <summary>A ring starts this many times its landing size.</summary>
        public const float StartScale = 3.2f;

        public const float DefaultApproachSeconds = 1.2f;
        public const float MinApproachSeconds = 0.8f;
        public const float MaxApproachSeconds = 2.4f;

        /// <summary>The ring fades in over this first part of its approach.</summary>
        public const float FadeInFraction = 0.3f;

        /// <summary>After landing the ring stays at full brightness this long, then fades over <see cref="FadeOutSeconds"/>.</summary>
        public const float AfterHoldSeconds = 0.12f;
        public const float FadeOutSeconds = 0.35f;

        /// <summary>The stroke swells within this long either side of the landing (a single eased swell, not a flash).</summary>
        public const float LandWindowSeconds = 0.09f;
        public const float LandSwell = 0.8f;

        public const float DefaultTargetRadiusMm = 7f;
        public const float DefaultStrokeMm = 1.3f;

        /// <summary>Rings are tipped toward the eyes until they are seen at least this many degrees above their own plane.</summary>
        public const float DefaultMinViewElevationDeg = 35f;

        const float TargetLevel = 0.4f;
        const float StaticRingCentreLine = 0.44f;     // HudShape.Ring: centre-line radius of the unit mesh

        static readonly Color RingColor = Color.white;
        static readonly Color TargetColor = new Color(0.702f, 0.898f, 0.988f, 1f);    // #B3E5FC

        sealed class Change
        {
            public bool Active;
            public double HitTime;
            public int Count;
            public readonly Vector3[] TargetsMm = new Vector3[MaxRings];
            public readonly HudShape[] Approach = new HudShape[MaxRings];
            public readonly HudShape[] Target = new HudShape[MaxRings];
            public readonly Mesh[] Meshes = new Mesh[MaxRings];
            public readonly Vector3[][] Vertices = new Vector3[MaxRings][];
            public readonly float[] ShownRadius = new float[MaxRings];
            public readonly float[] ShownStroke = new float[MaxRings];
            public bool Shown;
        }

        readonly Change[] _changes = new Change[MaxChanges];
        Transform _neck;
        Transform _head;
        bool _built;
        double _now;

        /// <summary>How long before the change a ring appears, in seconds of the caller's clock. Set it from the tempo with <see cref="ApproachForBeat"/>.</summary>
        public float ApproachSeconds = DefaultApproachSeconds;

        /// <summary>Radius of a landed ring, millimetres of the neck frame (a little larger than a fingertip dot, so it frames the dot).</summary>
        public float TargetRadiusMm = DefaultTargetRadiusMm;

        /// <summary>Stroke width of the rings, millimetres of the neck frame.</summary>
        public float StrokeMm = DefaultStrokeMm;

        /// <summary>See <see cref="DefaultMinViewElevationDeg"/>. 0 keeps the rings flat on the fretboard whatever the view.</summary>
        public float MinViewElevationDeg = DefaultMinViewElevationDeg;

        /// <summary>The time last given to <see cref="Tick"/>.</summary>
        public double Now { get { return _now; } }

        /// <summary>Creates the rings under <paramref name="parent"/> (may be null; identity scale).</summary>
        public static ApproachRings Create(Transform parent)
        {
            var go = new GameObject("ApproachRings");
            if (parent != null) go.transform.SetParent(parent, false);
            ApproachRings rings = go.AddComponent<ApproachRings>();
            rings.Build();
            return rings;
        }

        /// <summary>
        /// <paramref name="neckSocket"/>: the pinned neck frame (one unit = one metre of neck). <paramref name="head"/>:
        /// the wearer's head (StereoRig.Head); may be null, the rings then lie flat on the fretboard.
        /// </summary>
        public void Attach(Transform neckSocket, Transform head)
        {
            if (!_built) Build();
            _neck = neckSocket;
            _head = head;
        }

        void Build()
        {
            if (_built) return;
            _built = true;
            for (int c = 0; c < MaxChanges; c++)
            {
                var change = new Change();
                for (int i = 0; i < MaxRings; i++)
                {
                    change.Target[i] = HudShape.Create(transform, "Target" + c + "_" + i, HudShape.Ring);
                    change.Target[i].Visible = false;

                    change.Vertices[i] = new Vector3[HudShapeGeometry.RingVertexCount(HudShape.RingSegments)];
                    HudShapeGeometry.FillRingVertices(change.Vertices[i], HudShape.RingSegments, 0.01f, 0.001f, 0.001f);
                    change.Meshes[i] = HudShape.CreateDynamicRing("ApproachRing" + c + "_" + i, change.Vertices[i], 0.25f);
                    change.Approach[i] = HudShape.Create(transform, "Ring" + c + "_" + i, change.Meshes[i]);
                    change.Approach[i].Visible = false;
                    change.ShownRadius[i] = -1f;
                    change.ShownStroke[i] = -1f;
                }
                _changes[c] = change;
            }
        }

        void OnDestroy()
        {
            for (int c = 0; c < MaxChanges; c++)
            {
                Change change = _changes[c];
                if (change == null) continue;
                for (int i = 0; i < MaxRings; i++)
                {
                    Mesh mesh = change.Meshes[i];
                    if (mesh == null) continue;
                    if (Application.isPlaying) Destroy(mesh);
                    else DestroyImmediate(mesh);
                }
            }
        }

        // ------------------------------------------------------------------ scheduling

        /// <summary>
        /// Announces a chord change: rings will shrink onto the first <paramref name="count"/> positions of
        /// <paramref name="targetsNeckLocalMm"/> (the fingertip dots of the chord being changed TO, pinned neck frame,
        /// millimetres) and land at <paramref name="hitTimeSec"/> on the clock that <see cref="Tick"/> is given. The
        /// positions are copied. Calling it again with the same hit time only updates the positions, so it may be called
        /// every frame; a different hit time adds a second change (at most <see cref="MaxChanges"/>, the oldest is replaced).
        /// </summary>
        public void Schedule(Vector3[] targetsNeckLocalMm, int count, double hitTimeSec)
        {
            if (!_built) Build();
            if (double.IsNaN(hitTimeSec) || double.IsInfinity(hitTimeSec)) return;
            int n = targetsNeckLocalMm != null ? Mathf.Clamp(count, 0, Mathf.Min(MaxRings, targetsNeckLocalMm.Length)) : 0;

            Change slot = null;
            for (int c = 0; c < MaxChanges; c++)
            {
                if (_changes[c].Active && System.Math.Abs(_changes[c].HitTime - hitTimeSec) < 1e-3)
                {
                    slot = _changes[c];
                    break;
                }
            }
            if (slot == null)
            {
                for (int c = 0; c < MaxChanges && slot == null; c++)
                {
                    if (!_changes[c].Active) slot = _changes[c];
                }
            }
            if (slot == null)
            {
                slot = _changes[0];
                for (int c = 1; c < MaxChanges; c++)
                {
                    if (_changes[c].HitTime < slot.HitTime) slot = _changes[c];
                }
            }

            if (n == 0)
            {
                Hide(slot);
                slot.Active = false;
                return;
            }

            slot.Active = true;
            slot.HitTime = hitTimeSec;
            slot.Count = n;
            for (int i = 0; i < n; i++) slot.TargetsMm[i] = targetsNeckLocalMm[i];
        }

        /// <summary>Removes every ring at once (leaving play-along, seeking, a new song).</summary>
        public void Clear()
        {
            if (!_built) return;
            for (int c = 0; c < MaxChanges; c++)
            {
                Hide(_changes[c]);
                _changes[c].Active = false;
            }
        }

        /// <summary>Number of changes whose rings are on their way or fading.</summary>
        public int ActiveChanges
        {
            get
            {
                int n = 0;
                for (int c = 0; _built && c < MaxChanges; c++)
                {
                    if (_changes[c].Active) n++;
                }
                return n;
            }
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// Draws the rings for the time <paramref name="nowSec"/> (same clock as the hit times). Call it every frame while
        /// playing; while wait mode holds, keep passing the held time. Nothing is allocated.
        /// </summary>
        public void Tick(double nowSec)
        {
            if (!_built) return;
            _now = nowSec;

            bool haveNeck = _neck != null;
            Vector3 origin = Vector3.zero, neckX = Vector3.right, neckY = Vector3.up;
            float metresPerNeckMetre = 1f;
            if (haveNeck)
            {
                origin = _neck.TransformPoint(Vector3.zero);
                neckX = _neck.TransformPoint(Vector3.right) - origin;
                neckY = _neck.TransformPoint(Vector3.up) - origin;
                metresPerNeckMetre = neckY.magnitude;
                if (!(metresPerNeckMetre > 1e-6f) || !(neckX.magnitude > 1e-6f)) haveNeck = false;
            }

            float approach = Mathf.Max(0.05f, ApproachSeconds);
            for (int c = 0; c < MaxChanges; c++)
            {
                Change change = _changes[c];
                if (!change.Active) continue;

                float toHit = (float)(change.HitTime - nowSec);
                if (IsOver(toHit))
                {
                    Hide(change);
                    change.Active = false;
                    continue;
                }

                float level = RingLevel(toHit, approach, AfterHoldSeconds, FadeOutSeconds);
                if (!haveNeck || level <= 0f)
                {
                    Hide(change);
                    continue;
                }

                float mm = 0.001f * metresPerNeckMetre;
                float radius = RingRadiusMm(toHit, approach, TargetRadiusMm, StartScale) * mm;
                float swell = LandBloom(toHit, LandWindowSeconds);
                float halfStroke = 0.5f * StrokeMm * mm * (1f + LandSwell * swell);
                float targetSize = TargetRadiusMm * mm / StaticRingCentreLine;
                Vector3 unitX = neckX / neckX.magnitude;

                change.Shown = true;
                for (int i = 0; i < MaxRings; i++)
                {
                    bool used = i < change.Count;
                    change.Approach[i].Visible = used;
                    change.Target[i].Visible = used;
                    if (!used) continue;

                    Vector3 centre = _neck.TransformPoint(change.TargetsMm[i] * 0.001f);
                    Vector3 toEye = _head != null ? _head.position - centre : neckY;
                    Vector3 normal = RingNormal(neckY, toEye, MinViewElevationDeg);
                    Vector3 upHint = Mathf.Abs(Vector3.Dot(normal, unitX)) < 0.95f ? unitX : neckY;
                    Quaternion rotation = Quaternion.LookRotation(normal, upHint);

                    HudShape ring = change.Approach[i];
                    ring.Transform.SetPositionAndRotation(centre, rotation);
                    if (Mathf.Abs(radius - change.ShownRadius[i]) > 1e-6f || Mathf.Abs(halfStroke - change.ShownStroke[i]) > 1e-7f)
                    {
                        change.ShownRadius[i] = radius;
                        change.ShownStroke[i] = halfStroke;
                        HudShapeGeometry.FillRingVertices(change.Vertices[i], HudShape.RingSegments, radius, halfStroke, halfStroke);
                        HudShape.UploadRing(change.Meshes[i], change.Vertices[i]);
                    }
                    ring.SetColor(RingColor, level);

                    HudShape target = change.Target[i];
                    target.Transform.SetPositionAndRotation(centre, rotation);
                    target.SetSize(targetSize);
                    target.SetColor(TargetColor, TargetLevel * level);
                }
            }
        }

        static void Hide(Change change)
        {
            if (!change.Shown) return;
            change.Shown = false;
            for (int i = 0; i < MaxRings; i++)
            {
                change.Approach[i].Visible = false;
                change.Target[i].Visible = false;
            }
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>
        /// Size of a ring relative to its landing size, as a function of the time left until the hit:
        /// <paramref name="startScale"/> at <paramref name="approachSec"/> (or earlier), shrinking LINEARLY to exactly 1 at
        /// 0, and 1 afterwards (the ring has landed and stays on the spot).
        /// </summary>
        public static float RingScale(float timeToHitSec, float approachSec, float startScale)
        {
            if (float.IsNaN(timeToHitSec) || timeToHitSec <= 0f) return 1f;
            if (!(approachSec > 0f) || timeToHitSec >= approachSec) return startScale;
            return 1f + (startScale - 1f) * (timeToHitSec / approachSec);
        }

        /// <summary>Ring radius in millimetres as a function of the time left until the hit: <see cref="RingScale"/> times the landing radius.</summary>
        public static float RingRadiusMm(float timeToHitSec, float approachSec, float targetRadiusMm, float startScale)
        {
            return targetRadiusMm * RingScale(timeToHitSec, approachSec, startScale);
        }

        /// <summary>
        /// Brightness of a ring, 0..1: nothing before the approach starts, an eased fade-in over the first
        /// <see cref="FadeInFraction"/> of it, full until <paramref name="afterHoldSec"/> after the hit, then an eased
        /// fade-out over <paramref name="fadeOutSec"/>.
        /// </summary>
        public static float RingLevel(float timeToHitSec, float approachSec, float afterHoldSec, float fadeOutSec)
        {
            if (float.IsNaN(timeToHitSec)) return 0f;
            if (timeToHitSec >= approachSec) return 0f;
            if (timeToHitSec >= 0f)
            {
                float fadeIn = approachSec * FadeInFraction;
                return fadeIn > 0f ? PlayHudLayout.Ease01((approachSec - timeToHitSec) / fadeIn) : 1f;
            }
            float after = -timeToHitSec;
            if (after <= afterHoldSec) return 1f;
            if (!(fadeOutSec > 0f)) return 0f;
            return 1f - PlayHudLayout.Ease01((after - afterHoldSec) / fadeOutSec);
        }

        /// <summary>True once a ring has completely faded after its hit.</summary>
        public static bool IsOver(float timeToHitSec)
        {
            return timeToHitSec < -(AfterHoldSeconds + FadeOutSeconds);
        }

        /// <summary>The landing swell, 0..1: 1 exactly at the hit, easing to 0 at <paramref name="windowSec"/> before and after it.</summary>
        public static float LandBloom(float timeToHitSec, float windowSec)
        {
            if (!(windowSec > 0f) || float.IsNaN(timeToHitSec)) return 0f;
            return PlayHudLayout.Ease01(1f - Mathf.Abs(timeToHitSec) / windowSec);
        }

        /// <summary>
        /// A good approach time for a tempo: two beats (so the ring is half-way in exactly one beat before the change, when
        /// the fretting ghost hand starts to move), kept between <see cref="MinApproachSeconds"/> and
        /// <see cref="MaxApproachSeconds"/>. <paramref name="beatSeconds"/> = 60 / (bpm * tempo preset).
        /// </summary>
        public static float ApproachForBeat(float beatSeconds)
        {
            if (!(beatSeconds > 0f) || float.IsInfinity(beatSeconds)) return DefaultApproachSeconds;
            return Mathf.Clamp(2f * beatSeconds, MinApproachSeconds, MaxApproachSeconds);
        }

        /// <summary>
        /// The direction a ring faces (unit). It is the fretboard normal while the eyes see the fretboard at
        /// <paramref name="minElevationDeg"/> or more above its plane; at flatter views it is turned toward the eyes just
        /// far enough that the ring is seen at exactly that elevation, so it stays a readable ellipse instead of a sliver.
        /// </summary>
        public static Vector3 RingNormal(Vector3 boardNormal, Vector3 toEye, float minElevationDeg)
        {
            float nLen = boardNormal.magnitude;
            Vector3 n = nLen > 1e-9f ? boardNormal / nLen : Vector3.up;
            float eLen = toEye.magnitude;
            if (!(eLen > 1e-9f) || !(minElevationDeg > 0f)) return n;
            Vector3 e = toEye / eLen;

            float maxAngle = (90f - Mathf.Min(minElevationDeg, 90f)) * Mathf.Deg2Rad;   // largest allowed angle between the normal and the sight line
            float cos = Vector3.Dot(n, e);
            if (cos >= Mathf.Cos(maxAngle)) return n;

            Vector3 side = n - e * cos;                     // the part of the normal that is across the sight line
            float sideLen = side.magnitude;
            if (sideLen < 1e-6f) return e;                  // the fretboard faces exactly away: face the eyes
            side /= sideLen;
            return e * Mathf.Cos(maxAngle) + side * Mathf.Sin(maxAngle);
        }
    }
}
