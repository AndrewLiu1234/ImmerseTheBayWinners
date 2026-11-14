// NeckHudCard: the play-along card that floats by the neck (docs/MASTER_PLAN.md section 17: "about 8 cm above the neck
// near fret 5, angled toward the eyes"). It holds the current chord (large), the next chord with the beat-dot countdown,
// the strum or picking strip, the count-in and (optional) the lyric strip. There is no plate and no frame: on the additive glasses a
// "card" is only a group of bright things that move together.
//
// PLACEMENT. The card is not a child of the neck: it reads the neck socket and the head every frame and sets its own world
// pose. That way it always faces the eyes, stays level in the display, keeps its angular size at any distance, and is never
// mirrored or squashed by a left-handed (mirrored) or scaled neck. All of the maths is in PlayHudLayout (pure, unit-tested).
// Parent it under anything with identity scale (the HUD root, StereoRig.Head, or nothing); do not parent it under the neck.
//
// SOURCES (offline documentation of Unity 2021.3.3f1, Unity.app/Contents/Documentation/en/ScriptReference):
//  * Transform.TransformPoint.html: "Transforms position from local space to world space." (it includes the scale, so a
//    mirrored or 1.2x neck is followed correctly).
//  * Quaternion.LookRotation.html: "Creates a rotation with the specified forward and upwards directions."
//
// UNVERIFIED (nobody has run this in Unity yet): everything visual. Layout numbers come from a posture model, not the device.
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// The neck HUD card. Create it with <see cref="Create"/>, attach it with <see cref="Attach"/>, then every frame of a
    /// play-along feed <see cref="SetChords"/>, <see cref="Beats"/>, <see cref="Pattern"/>, <see cref="Lyrics"/> and
    /// <see cref="SetWaiting"/>.
    /// Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9000)]
    public sealed class NeckHudCard : MonoBehaviour
    {
        /// <summary>Words of the wait cue, before the chord name: "strum G".</summary>
        public const string WaitCuePrefix = "strum ";

        /// <summary>The wait cue fades in over this long, so a change that is played in time never makes it pop.</summary>
        public const float WaitFadeSeconds = 0.45f;

        /// <summary>While waiting, the chord name breathes this many times a second (slow on purpose: a reminder, not a nag).</summary>
        public const float WaitBreathHz = 0.6f;

        /// <summary>How much the chord name grows at the top of a breath.</summary>
        public const float WaitBreathGain = 0.06f;

        static readonly Color ChordColor = Color.white;
        static readonly Color InfoColor = new Color(0.702f, 0.898f, 0.988f, 1f);      // #B3E5FC, the ghost-hand rim colour

        Transform _neck;
        Transform _head;

        Transform _chordPivot;
        HudText _chord;
        HudText _nextWord;
        HudText _nextChord;
        HudText _waitText;
        HudShape _pauseLeft;
        HudShape _pauseRight;
        Transform _waitGroup;
        BeatDots _beats;
        PatternStrip _pattern;
        LyricStrip _lyrics;
        CountIn _countIn;
        bool _countInShown;

        string _currentRaw = string.Empty;
        string _nextRaw = string.Empty;
        bool _large;
        bool _built;
        bool _waiting;
        float _waitLevel;
        float _waitClock;
        float _shownWaitLevel = -1f;
        float _appliedScale = 1f;

        /// <summary>Position along the neck, as a fret number (5 = at fret 5).</summary>
        public float AnchorFret = PlayHudLayout.DefaultAnchorFret;

        /// <summary>How far the card floats from the neck's centre line, millimetres of the neck frame.</summary>
        public float LiftMm = PlayHudLayout.DefaultLiftMm;

        /// <summary>The guitar's scale length (nut to saddle), millimetres; only used to find the anchor fret.</summary>
        public float ScaleLengthMm = PlayHudLayout.DefaultScaleLengthMm;

        /// <summary>Extra shift of the whole card in its own plane, degrees of view (x to the viewer's right, y up). For tuning on the glasses.</summary>
        public Vector2 OffsetDeg;

        /// <summary>The beat-dot countdown to the next chord change.</summary>
        public BeatDots Beats { get { return _beats; } }

        /// <summary>The strum or picking strip.</summary>
        public PatternStrip Pattern { get { return _pattern; } }

        /// <summary>The lyric strip (chords above the words, or the chord sequence of a chords-only song). Optional: leave it unfed and it shows nothing.</summary>
        public LyricStrip Lyrics { get { return _lyrics; } }

        /// <summary>The 4-3-2-1 count-in. While it shows, it takes the place of the "next chord" block.</summary>
        public CountIn CountIn { get { return _countIn; } }

        /// <summary>The current chord as last set.</summary>
        public string CurrentChord { get { return _currentRaw; } }

        /// <summary>The next chord as last set.</summary>
        public string NextChord { get { return _nextRaw; } }

        /// <summary>True while the wait cue is asked for.</summary>
        public bool Waiting { get { return _waiting; } }

        /// <summary>Distance from the eyes to the card's anchor at the last pose update, metres.</summary>
        public float ViewingDistanceM { get; private set; }

        /// <summary>Creates the card under <paramref name="parent"/> (may be null; identity scale; not the neck). No Inspector wiring needed.</summary>
        public static NeckHudCard Create(Transform parent)
        {
            var go = new GameObject("NeckHudCard");
            if (parent != null) go.transform.SetParent(parent, false);
            NeckHudCard card = go.AddComponent<NeckHudCard>();
            card.Build();
            return card;
        }

        /// <summary>
        /// Ties the card to the neck and the wearer's head. <paramref name="neckSocket"/> is the pinned neck frame (origin
        /// at the nut on the fretboard, +x to the bridge, +y out of the fretboard, +z to string 1, one unit = one metre of
        /// neck); <paramref name="head"/> is the wearer's head (StereoRig.Head). Either may be null: without a head the card
        /// faces out of the fretboard, without a neck it stays where it is.
        /// </summary>
        public void Attach(Transform neckSocket, Transform head)
        {
            if (!_built) Build();
            _neck = neckSocket;
            _head = head;
            UpdatePose();
        }

        void Build()
        {
            if (_built) return;
            _built = true;
            float d = PlayHudLayout.DesignDistanceM;

            var pivot = new GameObject("ChordPivot");
            pivot.transform.SetParent(transform, false);
            _chordPivot = pivot.transform;

            _chord = HudText.Create(_chordPivot, "CurrentChord");
            _chord.Color = ChordColor;
            _chord.Anchor = TextAnchor.MiddleLeft;
            _chord.Alignment = TextAlignment.Left;

            _nextWord = HudText.Create(transform, "NextWord");
            _nextWord.Color = InfoColor;
            _nextWord.Anchor = TextAnchor.MiddleLeft;
            _nextWord.Alignment = TextAlignment.Left;
            _nextWord.SetText(ChordLabel.NextPrefix.Trim());
            _nextWord.Visible = false;

            _nextChord = HudText.Create(transform, "NextChord");
            _nextChord.Color = InfoColor;
            _nextChord.Anchor = TextAnchor.MiddleLeft;
            _nextChord.Alignment = TextAlignment.Left;
            _nextChord.Visible = false;

            _beats = BeatDots.Create(transform, d);
            _pattern = PatternStrip.Create(transform, d);
            _lyrics = LyricStrip.Create(transform, d);
            _countIn = CountIn.Create(transform, d);

            var wait = new GameObject("WaitCue");
            wait.transform.SetParent(transform, false);
            _waitGroup = wait.transform;
            _waitText = HudText.Create(_waitGroup, "WaitText");
            _waitText.Color = ChordColor;
            _waitText.Anchor = TextAnchor.MiddleLeft;
            _waitText.Alignment = TextAlignment.Left;
            _waitText.Visible = false;

            _large = FlagStore.Current.largeText;
            ApplyLayout();
        }

        // ------------------------------------------------------------------ content

        /// <summary>
        /// The chord to play now (large) and the one after it (small). Null or empty hides that part. Texts are rebuilt only
        /// when the words change, so call it every frame.
        /// </summary>
        public void SetChords(string current, string next)
        {
            if (HudText.NeedsRebuild(_currentRaw, current))
            {
                _currentRaw = current ?? string.Empty;
                _chord.SetText(_currentRaw);
                _waitText.SetText(WaitCueText(_currentRaw));
            }

            if (HudText.NeedsRebuild(_nextRaw, next))
            {
                _nextRaw = next ?? string.Empty;
                _nextChord.SetText(_nextRaw);
                ApplyVisibility();
            }
        }

        /// <summary>
        /// Wait mode: true while the playhead is holding for the player to play the change. The countdown and the next
        /// chord make way for a steady "strum G" with a pause mark, and the chord name breathes slowly. Nothing flashes and
        /// nothing escalates; it fades in over <see cref="WaitFadeSeconds"/> and out again as soon as this is false.
        /// </summary>
        public void SetWaiting(bool waiting)
        {
            if (waiting == _waiting) return;
            _waiting = waiting;
            if (waiting) _waitClock = 0f;
        }

        // ------------------------------------------------------------------ per frame

        void LateUpdate()
        {
            Advance(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// One frame of the card: follows the neck and the head, and advances the wait cue by
        /// <paramref name="deltaSeconds"/>. LateUpdate calls it; Editor code (previews, tests) may call it directly.
        /// </summary>
        public void Advance(float deltaSeconds)
        {
            if (!_built) return;
            bool large = FlagStore.Current.largeText;
            if (large != _large)
            {
                _large = large;
                ApplyLayout();
            }

            if (_countIn.IsShowing != _countInShown)
            {
                _countInShown = _countIn.IsShowing;
                ApplyVisibility();
            }

            UpdatePose();
            AdvanceWait(deltaSeconds);
        }

        /// <summary>
        /// Shows or hides the whole card (for example while the results card is up). Everything it holds is kept; a hidden
        /// card does not run (its LateUpdate stops) and picks up the neck and the head again when shown.
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf == visible) return;
            gameObject.SetActive(visible);
            if (visible) UpdatePose();
        }

        /// <summary>False after SetVisible(false).</summary>
        public bool IsVisible { get { return gameObject.activeSelf; } }

        /// <summary>Recomputes the card's world pose from the neck socket and the head. Cheap; no allocation.</summary>
        public void UpdatePose()
        {
            if (_neck == null) return;

            // The neck frame in world space, read through TransformPoint so mirroring and scale are followed.
            Vector3 origin = _neck.TransformPoint(Vector3.zero);
            Vector3 neckX = _neck.TransformPoint(Vector3.right) - origin;
            Vector3 neckY = _neck.TransformPoint(Vector3.up) - origin;
            Vector3 neckZ = _neck.TransformPoint(Vector3.forward) - origin;

            Vector3 eye;
            Vector3 headUp;
            if (_head != null)
            {
                eye = _head.position;
                headUp = _head.up;
            }
            else
            {
                // No head yet: stand one design distance out of the fretboard, bass side up.
                float scale = neckY.magnitude > 1e-6f ? neckY.magnitude : 1f;
                Vector3 basePoint = origin + neckX * (PlayHudLayout.FretDistanceMm(ScaleLengthMm, AnchorFret) * 0.001f);
                eye = basePoint + neckY / scale * (PlayHudLayout.DesignDistanceM + LiftMm * 0.001f * scale);
                headUp = -neckZ;
            }

            Vector3 anchor = PlayHudLayout.AnchorWorld(origin, neckX, neckY, eye, headUp,
                PlayHudLayout.FretDistanceMm(ScaleLengthMm, AnchorFret), LiftMm);

            PlayHudLayout.BillboardBasis(anchor, eye, headUp, out Vector3 right, out Vector3 up, out Vector3 forward);
            float distance = (anchor - eye).magnitude;
            ViewingDistanceM = distance;

            Vector3 position = anchor
                + right * PlayHudLayout.Offset(OffsetDeg.x, distance)
                + up * PlayHudLayout.Offset(OffsetDeg.y, distance);
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, up));

            // Children are sized for the design distance; scaling the card keeps every angle right at this distance.
            float parentScale = 1f;
            Transform parent = transform.parent;
            if (parent != null && parent.lossyScale.y > 1e-6f) parentScale = parent.lossyScale.y;
            float s = PlayHudLayout.ScaleForDistance(distance, PlayHudLayout.DesignDistanceM) / parentScale;
            if (Mathf.Abs(s - _appliedScale) > 1e-5f)
            {
                _appliedScale = s;
                transform.localScale = new Vector3(s, s, s);
            }
        }

        void AdvanceWait(float dt)
        {
            if (float.IsNaN(dt) || dt < 0f) dt = 0f;
            float target = _waiting ? 1f : 0f;
            if (_waitLevel != target)
            {
                float step = WaitFadeSeconds > 0f ? dt / WaitFadeSeconds : 1f;
                _waitLevel = Mathf.MoveTowards(_waitLevel, target, step);
            }
            if (_waiting) _waitClock += dt;

            float breath = _waitLevel > 0f ? WaitBreath(_waitClock, WaitBreathHz) * _waitLevel : 0f;
            float pivotScale = 1f + WaitBreathGain * breath;
            if (Mathf.Abs(pivotScale - _chordPivot.localScale.x) > 1e-4f)
                _chordPivot.localScale = new Vector3(pivotScale, pivotScale, 1f);

            if (Mathf.Abs(_waitLevel - _shownWaitLevel) > 0.002f)
            {
                _shownWaitLevel = _waitLevel;
                ApplyVisibility();
                float eased = PlayHudLayout.Ease01(_waitLevel);
                if (_pauseLeft != null)
                {
                    _pauseLeft.SetColor(InfoColor, eased);
                    _pauseRight.SetColor(InfoColor, eased);
                }
                // The cue slides up a fraction of its height as it appears: an eased entrance instead of a pop.
                float capM = _waitCapM;
                _waitGroup.localPosition = new Vector3(_waitGroup.localPosition.x, _waitRowY - (1f - eased) * 0.35f * capM, 0f);
            }
        }

        float _waitRowY;
        float _waitCapM;

        void ApplyVisibility()
        {
            bool cue = _waitLevel > 0.5f;
            bool hasNext = _nextRaw.Length > 0 && !_countInShown;
            _nextWord.Visible = hasNext && !cue;
            _nextChord.Visible = hasNext && !cue;
            _beats.Visible = !cue;
            _waitText.Visible = cue && _currentRaw.Length > 0;
            if (_pauseLeft != null)
            {
                _pauseLeft.Visible = cue;
                _pauseRight.Visible = cue;
            }
        }

        void ApplyLayout()
        {
            float d = PlayHudLayout.DesignDistanceM;
            float world = WorldDistance();
            PlayHudLayout.CardBlocks b = PlayHudLayout.Blocks(HudText.TextScale(_large));
            float left = PlayHudLayout.Offset(b.ColumnLeft, d);

            _chordPivot.localScale = Vector3.one;
            _chordPivot.localPosition = new Vector3(left, PlayHudLayout.Offset(b.ChordRow, d), 0f);
            _chord.SetAngularHeight(b.ChordCap, world);

            float nextLeft = PlayHudLayout.Offset(b.NextLeft, d);
            _nextWord.transform.localPosition = new Vector3(nextLeft, PlayHudLayout.Offset(b.NextWordRow, d), 0f);
            _nextWord.SetAngularHeight(b.NextWordCap, world);
            _nextChord.transform.localPosition = new Vector3(nextLeft, PlayHudLayout.Offset(b.NextChordRow, d), 0f);
            _nextChord.SetAngularHeight(b.NextChordCap, world);

            // The beat dots count down to the next chord, so they sit under it (only as many as end inside the view).
            _beats.transform.localPosition = new Vector3(PlayHudLayout.Offset(b.DotsFirstX, d), PlayHudLayout.Offset(b.DotsRow, d), 0f);
            _beats.MaxVisible = b.DotsMaxVisible;

            // The pattern strip grows up from its lowest ink, which stays above the strings, between the two hands.
            _pattern.MaxWidthDeg = b.PatternMaxWidth;
            _pattern.AnchorBottom = true;
            _pattern.transform.localPosition = new Vector3(left, PlayHudLayout.Offset(b.PatternBottom, d), 0f);

            // One lyric line above the chord name (the next line does not fit on the floating neck's view).
            _lyrics.ShowNextLine = false;
            _lyrics.transform.localPosition = new Vector3(left, PlayHudLayout.Offset(b.LyricRow, d), 0f);
            // Large text: the lyric line no longer fits above the chord name inside the view; it is optional (PRD 6), so it yields.
            if (_lyrics.gameObject.activeSelf != b.LyricFits) _lyrics.gameObject.SetActive(b.LyricFits);
            _countIn.transform.localPosition = new Vector3(PlayHudLayout.Offset(b.CountInX, d), PlayHudLayout.Offset(b.CountInRow, d), 0f);

            // Wait cue: a pause mark (two upright bars) and "strum G" in the "next" block, which makes way for it.
            float capM = PlayHudLayout.Size(b.WaitCap, d);
            _waitCapM = capM;
            if (_pauseLeft == null)
            {
                _pauseLeft = HudShape.CreateBar(_waitGroup, "PauseLeft", capM * 0.26f, capM);
                _pauseRight = HudShape.CreateBar(_waitGroup, "PauseRight", capM * 0.26f, capM);
                _pauseLeft.Visible = false;
                _pauseRight.Visible = false;
            }
            _pauseLeft.SetPosition(capM * 0.13f, 0f);
            _pauseRight.SetPosition(capM * 0.60f, 0f);
            _waitText.transform.localPosition = new Vector3(capM * 1.25f, 0f, 0f);
            _waitText.SetAngularHeight(b.WaitCap, world);
            float waitY = PlayHudLayout.Offset(b.WaitRow, d);
            _waitRowY = waitY;
            _waitGroup.localPosition = new Vector3(nextLeft, waitY, 0f);

            _shownWaitLevel = -1f;
            ApplyVisibility();
        }

        // The distance to hand to HudText so its size is right in WORLD space while this card is scaled.
        float WorldDistance()
        {
            float s = transform.lossyScale.y;
            return PlayHudLayout.DesignDistanceM * (s > 1e-6f ? s : 1f);
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>The wait cue for a chord: "strum G". Empty when there is no chord.</summary>
        public static string WaitCueText(string chord)
        {
            return string.IsNullOrEmpty(chord) ? string.Empty : WaitCuePrefix + chord;
        }

        /// <summary>
        /// The breath of the chord name while waiting: 0 at the start (so it never jumps), rising and falling smoothly
        /// between 0 and 1, <paramref name="hz"/> times a second.
        /// </summary>
        public static float WaitBreath(float secondsWaiting, float hz)
        {
            if (!(secondsWaiting > 0f) || !(hz > 0f)) return 0f;
            return 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * hz * secondsWaiting);
        }
    }
}
