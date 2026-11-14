// GhostHandsDirector: drives both Blender-posed ghost hands from the practice timeline (Runtime/Practice/PracticeFrame).
//
//     PosedGhostHand fretting = PosedGhostHand.Create(neck.Socket, GhostPose.HandFretting, poses);            // nut frame
//     PosedGhostHand strumming = GhostHandsDirector.CreateStrummingHand(neck, poses);                         // strum zone
//     GhostHandsDirector hands = GhostHandsDirector.Create(fretting, strumming);   // a null or unavailable hand is skipped
//     each frame:  if (frame.Active) hands.Apply(frame, Time.deltaTime); else hands.Idle(chordOnTheNeck, Time.deltaTime);
//
// FRETTING: shows frame.Chord; from one beat before the change (BeatsToChange = 1) it morphs to frame.NextChord, locked to
// the beat cursor, arriving by the change (StrumMath.MorphProgress: one beat, clamped to 0.25-0.8 s). Count-in (Chord
// null): the song's first chord (NextChord), or rest. Wait mode (the playhead held ON the change, Chord = the new chord):
// it holds Chord and does not start toward the next one. ChordConfirmed fades it to flag ghost.fadeAfterSuccess (0.4); it is
// back at full brightness as soon as it starts forming the next chord.
// STRUMMING: plays frame.StrumSlots in time with frame.Beat (StrumMath.Sample: D = down-start -> down-end, U = up-start ->
// up-end, "-" = hover at pick-ready, each slot ending on the next one's start pose). Count-in and wait mode: pick-ready,
// no strokes. No song or no pattern: rest.
// THE SWEEP (StrokeSweep): on a FloatingNeck, each D / U stroke also moves the whole hand so the pick (9 mm beyond the
// thumb-index pinch, as authored) sweeps from outside the chord's bass string to past string 1 (D) or from past string 1
// to between strings 3 and 4 (U), in the string plane while it crosses the struck strings and lifted at both ends. The
// recover and hover phases carry that offset smoothly to the next stroke's start; held poses ease it back to zero.
// Sweep = false switches it off (the authored blend alone).
// SMOOTHNESS: held poses (rest, pick-ready, a chord between morphs) are reached with PosedGhostHand.SetPose's 0.28 s ease;
// beat-locked poses are set every frame. When a beat-locked pose takes over from a held one more than SettleJumpMm away
// (leaving wait mode mid-pattern, a song starting without a count-in), the hand cross-fades to it over SettleSeconds
// instead of jumping; the count-in's lead-in makes the normal start seamless, so it needs none.
//
// THE STRUMMING ANCHOR (why CreateStrummingHand puts the hand where it does):
//   * The strumming poses are authored in the NECK frame, millimetres from the nut, around a soundhole centred at
//     x = AuthoredSoundholeXMm = 440 (art/blender/hand/README.md: "nut-to-soundhole 440"; build_neck_mockup.py line 271
//     puts the Soundhole marker at (nut_to_soundhole, 0, 0); pose_hands.py strum_line_x = soundhole - half its radius =
//     415 mm). strumming-*.pose.json: wrists at x 452-485 mm, fingertips at x 392-486 mm.
//   * FloatingNeck.StrumZone sits at (StrumZoneXMm, stringHeightMm, 0) (FloatingNeck.cs line 685), and StrumZoneXMm is the
//     soundhole centre in aligned mode (NeckMath.StrumZoneXMm: layout.nutToSoundholeMm) but only LastFretXMm + 110 mm on
//     the floating neck (FloatingNeck.cs line 663, NeckMath.FloatingStrumZoneXMm: 272.6 mm for the dev 5-fret layout),
//     whose strings end at the strum zone + 40 mm (NeckMeshBuilder.cs lines 95-98, NeckStyle strumHalfMm 40).
//   * Parented to Socket the hand would hover at 415 mm, 100 mm beyond the floating neck's last string; parented to
//     StrumZone at the identity (FloatingNeck.cs line 9) it would be 440 mm further still. So it goes under StrumZone at
//     (-440 mm, -stringHeight, 0): the authored soundhole lands on the strum zone in both modes (exactly the authored place
//     on a guitar measured at 440 mm), the pick line 25 mm toward the nut from it, over solid strings. The director
//     re-applies that offset whenever the strum zone moves (mode change, new layout).
// No per-frame allocation. Plain C#: the owner (Bootstrap) calls Apply or Idle once a frame; PosedGhostHand.Update runs the
// eases (call PosedGhostHand.Tick yourself where Update does not run: previews, tests).
using UnityEngine;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Practice;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public sealed class GhostHandsDirector
    {
        /// <summary>The soundhole centre the strumming poses were authored around, neck-frame mm from the nut.</summary>
        public const float AuthoredSoundholeXMm = 440f;
        /// <summary>A beat-locked pose that takes over from a held pose farther than this (wrist, mm) is cross-faded in.</summary>
        public const float SettleJumpMm = 3f;
        /// <summary>How long that cross-fade takes (seconds); the hand is exactly on the beat-locked pose afterwards.</summary>
        public const float SettleSeconds = 0.25f;
        /// <summary>The fade after success when the flag is unreadable.</summary>
        public const float DefaultFadeAfterSuccess = 0.4f;

        readonly Motion _fretting;
        readonly Motion _strumming;
        FloatingNeck _neck;
        Transform _strumZone;
        readonly Quaternion[] _rotations = new Quaternion[GhostPose.BoneCount];   // sweep: the blended pose, then its FK
        readonly Quaternion[] _totals = new Quaternion[GhostPose.BoneCount];
        readonly Vector3[] _joints = new Vector3[GhostPose.JointCount];
        readonly float[] _stringZ = new float[NeckLayout.StringCount];
        string _bassChord;
        int _bassString = 6;
        Vector3 _strumOffset, _pickTarget, _authoredPick;
        Vector3 _strumZoneLocal;
        bool _strumZoneKnown;

        StrumSample _lastStrum;
        string _frettingFrom, _frettingTo;
        float _frettingMorph, _frettingFade = 1f;

        GhostHandsDirector(PosedGhostHand fretting, PosedGhostHand strumming)
        {
            _fretting = Usable(fretting) ? new Motion(fretting) : null;
            _strumming = Usable(strumming) ? new Motion(strumming) : null;
            _lastStrum = StrumMath.Sample(false, false, 0.0, null, 1);
            _frettingFrom = _frettingTo = StrumMath.Rest;
            if (_strumming != null) _strumZone = FindStrumZone(strumming, out _neck);
            Sweep = true;
        }

        /// <summary>The stroke sweep on top of the authored strokes (file header). True by default; needs a FloatingNeck.</summary>
        public bool Sweep { get; set; }
        /// <summary>The whole-hand translation of the strumming hand asked for this frame (neck mm; zero without a sweep).</summary>
        public Vector3 StrumOffsetMm { get { return _strumOffset; } }
        /// <summary>During a stroke: where the pick should be (neck mm); otherwise the last such target.</summary>
        public Vector3 PickTargetMm { get { return _pickTarget; } }
        /// <summary>During a stroke: where the authored blend alone puts the pick (neck mm).</summary>
        public Vector3 AuthoredPickMm { get { return _authoredPick; } }

        /// <summary>
        /// A director for the two hands. Either may be null or unavailable (PosedGhostHand.IsAvailable false): that hand is
        /// then left alone (the capsule fretting hand stays in charge of its side). Never returns null.
        /// </summary>
        public static GhostHandsDirector Create(PosedGhostHand fretting, PosedGhostHand strumming)
        {
            return new GhostHandsDirector(fretting, strumming);
        }

        /// <summary>
        /// The strumming hand on <paramref name="neck"/>'s strum zone, placed by StrummingHandLocalPosition (see the file
        /// header). Never null (check IsAvailable); with a null neck it is made at the scene root, unplaced.
        /// </summary>
        public static PosedGhostHand CreateStrummingHand(FloatingNeck neck, GhostPoseLibrary library)
        {
            Transform zone = neck != null ? neck.StrumZone : null;
            PosedGhostHand hand = PosedGhostHand.Create(zone, GhostPose.HandStrumming, library);
            if (zone != null) hand.transform.localPosition = StrummingHandLocalPosition(zone.localPosition);
            return hand;
        }

        /// <summary>
        /// Where the strumming hand goes under FloatingNeck.StrumZone (local metres) so that its poses' neck frame has its
        /// nut AuthoredSoundholeXMm before the strum zone, on the fretboard surface and the centre line:
        /// (-0.440, -zone.y, -zone.z) for a strum zone at local position <paramref name="strumZoneLocalPosition"/>.
        /// </summary>
        public static Vector3 StrummingHandLocalPosition(Vector3 strumZoneLocalPosition)
        {
            return new Vector3(-AuthoredSoundholeXMm * NeckMath.MetresPerMm, -strumZoneLocalPosition.y, -strumZoneLocalPosition.z);
        }

        // ------------------------------------------------------------------------------------------------ state, for HUD/previews

        /// <summary>True when the fretting hand is driven (it was given, and was available).</summary>
        public bool DrivesFretting { get { return _fretting != null; } }
        /// <summary>True when the strumming hand is driven.</summary>
        public bool DrivesStrumming { get { return _strumming != null; } }
        /// <summary>The strumming hand's last sample (phase, poses, slot).</summary>
        public StrumSample LastStrum { get { return _lastStrum; } }
        /// <summary>The chord (or rest) the fretting hand shows or morphs from.</summary>
        public string FrettingFrom { get { return _frettingFrom; } }
        /// <summary>The chord the fretting hand morphs to (equal to FrettingFrom when it holds one).</summary>
        public string FrettingTo { get { return _frettingTo; } }
        /// <summary>The fretting morph's linear progress, 0..1 (0 while holding).</summary>
        public float FrettingMorph { get { return _frettingMorph; } }
        /// <summary>The fade asked of the fretting hand (1 = full).</summary>
        public float FrettingFade { get { return _frettingFade; } }

        // ------------------------------------------------------------------------------------------------ per frame

        /// <summary>One frame of a running song (frame.Active). An inactive frame is treated as Idle(null).</summary>
        public void Apply(PracticeFrame frame, float dt)
        {
            if (!frame.Active)
            {
                Idle(null, dt);
                return;
            }
            SyncStrumAnchor();

            string from, to;
            float u;
            StrumMath.FrettingTarget(frame.Chord, frame.NextChord, frame.BeatsToChange, frame.BeatsPerSecond, frame.Waiting,
                                     out from, out to, out u);
            _frettingFrom = from;
            _frettingTo = to;
            _frettingMorph = u;
            _frettingFade = StrumMath.FrettingFade(frame.ChordConfirmed, u, FadeFlag());
            if (_fretting != null)
            {
                if (u > 0f) _fretting.Blend(from, to, StrumMath.Ease(u), dt, Vector3.zero);
                else _fretting.Hold(from, dt);
                _fretting.Fade(_frettingFade);
            }

            _lastStrum = StrumMath.Sample(true, frame.Waiting, frame.Beat, frame.StrumSlots, frame.SlotsPerBeat);
            _strumOffset = StrumOffset(ref frame, _lastStrum);
            DriveStrumming(dt);
        }

        /// <summary>No song: the fretting hand holds <paramref name="chord"/> (null = its rest pose) at full brightness, the
        /// strumming hand rests.</summary>
        public void Idle(string chord, float dt)
        {
            SyncStrumAnchor();
            _frettingFrom = _frettingTo = chord ?? StrumMath.Rest;
            _frettingMorph = 0f;
            _frettingFade = 1f;
            if (_fretting != null)
            {
                _fretting.Hold(_frettingFrom, dt);
                _fretting.Fade(1f);
            }
            _lastStrum = StrumMath.Sample(false, false, 0.0, null, 1);
            _strumOffset = Vector3.zero;
            DriveStrumming(dt);
        }

        void DriveStrumming(float dt)
        {
            if (_strumming == null) return;
            StrumPhase p = _lastStrum.Phase;
            if (p == StrumPhase.Rest || p == StrumPhase.Ready) _strumming.Hold(_lastStrum.From, dt);
            else _strumming.Blend(_lastStrum.From, _lastStrum.To, _lastStrum.Blend, dt, _strumOffset);
        }

        // ------------------------------------------------------------------------------------------------ the stroke sweep

        // The strumming hand's translation for this sample: exact during a stroke, carried linearly (with the pose blend)
        // from one stroke's end to the next one's start in the recover / hover / lead-in phases, zero when held.
        Vector3 StrumOffset(ref PracticeFrame frame, StrumSample s)
        {
            if (!Sweep || _neck == null || _neck.Layout == null || _strumming == null || !_strumming.Alive) return Vector3.zero;
            switch (s.Phase)
            {
                case StrumPhase.Stroke:
                    return SweepOffset(s.Kind, CurrentChord(ref frame), s.From, s.To, s.Blend, true);
                case StrumPhase.Recover:
                    return Vector3.Lerp(SweepOffset(s.Kind, CurrentChord(ref frame), StrumMath.StrokeEndPose(s.Kind), StrumMath.StrokeEndPose(s.Kind), 1f, false),
                                        EntryOffset(s.To, NextSlotChord(ref frame)), s.Blend);
                case StrumPhase.Hover:
                    return Vector3.Lerp(Vector3.zero, EntryOffset(s.To, NextSlotChord(ref frame)), s.Blend);
                case StrumPhase.LeadIn:
                    return Vector3.Lerp(Vector3.zero, EntryOffset(s.To, ChordAtBeat(ref frame, 0.0)), s.Blend);
                default:
                    return Vector3.zero;
            }
        }

        // The offset at the start of the stroke that begins on entry pose 'pose' (zero for pick-ready: a hover).
        Vector3 EntryOffset(string pose, string chord)
        {
            char kind = string.Equals(pose, StrumMath.DownStart, System.StringComparison.Ordinal) ? 'D'
                      : string.Equals(pose, StrumMath.UpStart, System.StringComparison.Ordinal) ? 'U' : '-';
            if (kind == '-') return Vector3.zero;
            return SweepOffset(kind, chord, pose, pose, 0f, false);
        }

        Vector3 SweepOffset(char kind, string chord, string fromPose, string toPose, float u, bool remember)
        {
            Vector3 authored;
            if (!AuthoredPickNeckMm(fromPose, toPose, u, out authored)) return Vector3.zero;
            NeckLayout layout = _neck.Layout;
            for (int i = 0; i < NeckLayout.StringCount; i++) _stringZ[i] = NeckMath.StringZMm(layout, i + 1, authored.x);
            Vector3 target = StrokeSweep.Target(kind, BassString(chord), u, authored.x, layout.stringHeightMm, _stringZ);
            if (remember)
            {
                _pickTarget = target;
                _authoredPick = authored;
            }
            return StrokeSweep.Offset(target, authored);
        }

        // Where the authored blend of two strumming poses puts the pick, neck-frame mm (forward kinematics of the pose data).
        bool AuthoredPickNeckMm(string fromPose, string toPose, float u, out Vector3 pick)
        {
            pick = Vector3.zero;
            GhostPose a, b;
            if (!_strumming.Hand.TryGetPose(fromPose, out a) || !_strumming.Hand.TryGetPose(toPose, out b) || !a.HasBind) return false;
            Vector3 root;
            if (!GhostPose.BlendLinear(a, b, u, _rotations, out root)) return false;
            if (!a.ComputeJointPositionsMm(_rotations, root, _totals, _joints)) return false;
            Vector3 inPoseFrame = StrokeSweep.Pick(_joints[GhostPose.BoneCount + 0], _joints[GhostPose.BoneCount + 1], _totals[GhostPose.Wrist]);
            pick = inPoseFrame + (_strumZone.localPosition + _strumming.Hand.transform.localPosition) / NeckMath.MetresPerMm;
            return true;
        }

        int BassString(string chord)
        {
            if (!ReferenceEquals(chord, _bassChord))
            {
                _bassChord = chord;
                ChordShape shape;
                _bassString = chord != null && ChordLibrary.TryGet(chord, out shape) ? shape.BassString : 6;
            }
            return _bassString;
        }

        static string CurrentChord(ref PracticeFrame frame)
        {
            return frame.Chord ?? frame.NextChord;
        }

        // The chord sounding when the next pattern slot starts.
        static string NextSlotChord(ref PracticeFrame frame)
        {
            int perBeat = frame.SlotsPerBeat > 0 ? frame.SlotsPerBeat : 1;
            double nextSlotBeat = (System.Math.Floor(frame.Beat * perBeat + StrumMath.BoundaryEpsilon) + 1.0) / perBeat;
            return ChordAtBeat(ref frame, nextSlotBeat);
        }

        static string ChordAtBeat(ref PracticeFrame frame, double beat)
        {
            if (frame.Chord == null) return frame.NextChord;
            bool changed = frame.NextChord != null && !double.IsInfinity(frame.BeatsToChange) && beat >= frame.Beat + frame.BeatsToChange - 1e-6;
            return changed ? frame.NextChord : frame.Chord;
        }

        static float FadeFlag()
        {
            FlagSet flags = FlagStore.Current;
            return flags != null && flags.ghost != null ? flags.ghost.fadeAfterSuccess : DefaultFadeAfterSuccess;
        }

        // ------------------------------------------------------------------------------------------------ the strum anchor

        // The strum zone the hand hangs under, if its parent is a FloatingNeck's StrumZone (then the offset is kept in step).
        static Transform FindStrumZone(PosedGhostHand hand, out FloatingNeck neck)
        {
            neck = null;
            Transform parent = hand.transform.parent;
            if (parent == null || parent.parent == null) return null;
            FloatingNeck owner = parent.parent.GetComponent<FloatingNeck>();
            if (owner == null || owner.StrumZone != parent) return null;
            neck = owner;
            return parent;
        }

        void SyncStrumAnchor()
        {
            if (_strumZone == null || _strumming == null || !_strumming.Alive) return;
            Vector3 local = _strumZone.localPosition;
            if (_strumZoneKnown && local == _strumZoneLocal) return;
            _strumZoneLocal = local;
            _strumZoneKnown = true;
            _strumming.Hand.transform.localPosition = StrummingHandLocalPosition(local);
        }

        static bool Usable(PosedGhostHand hand)
        {
            return hand != null && hand.IsAvailable;
        }

        // ------------------------------------------------------------------------------------------------ one hand

        // Held poses go through SetPose's ease; beat-locked blends are set every frame, with a short catch-up when they take
        // over from a held pose.
        sealed class Motion
        {
            public readonly PosedGhostHand Hand;
            bool _blending, _settling;
            float _settleElapsed;
            Vector3 _offset, _offsetFrom;
            float _offsetEase = 1f;

            public Motion(PosedGhostHand hand)
            {
                Hand = hand;
            }

            public bool Alive { get { return Hand != null && Hand.IsAvailable; } }

            public void Fade(float fade)
            {
                if (Alive) Hand.SetFade(fade);
            }

            public void Hold(string pose, float dt)
            {
                if (!Alive) return;
                if (_blending)
                {
                    _blending = false;
                    _offsetFrom = _offset;
                    _offsetEase = _offset == Vector3.zero ? 1f : 0f;
                }
                bool moved = false;
                if (_offsetEase < 1f)                       // the sweep's translation eases out like the pose (morphSeconds)
                {
                    float step = dt > 0f && !float.IsInfinity(dt) ? dt : 0f;
                    _offsetEase = Mathf.Min(1f, _offsetEase + step / Mathf.Max(Hand.morphSeconds, 1e-3f));
                    _offset = _offsetFrom * (1f - StrumMath.Ease(_offsetEase));
                    Hand.SetRootOffsetMm(_offset);
                    moved = true;
                }
                Hand.SetPose(pose, false);
                if (moved && Hand.Driver != null && !Hand.Driver.IsMorphing) Hand.RefreshPose();
            }

            public void Blend(string from, string to, float u, float dt, Vector3 offset)
            {
                if (!Alive) return;
                if (!_blending)
                {
                    _blending = true;
                    _settling = Hand.DistanceToBlendMm(from, to, u, offset) > SettleJumpMm;
                    _settleElapsed = 0f;
                    _offsetFrom = _offset;
                    if (_settling) Hand.HoldSnapshot();
                }
                float weight = 1f;
                if (_settling)
                {
                    if (dt > 0f && !float.IsInfinity(dt)) _settleElapsed += dt;
                    weight = StrumMath.Ease(_settleElapsed / SettleSeconds);
                    if (weight >= 1f) _settling = false;
                }
                _offset = weight >= 1f ? offset : Vector3.Lerp(_offsetFrom, offset, weight);
                Hand.SetRootOffsetMm(_offset);
                Hand.ShowBlend(from, to, u, weight);
            }
        }
    }
}
