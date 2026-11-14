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
        Transform _strumZone;
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
            if (_strumming != null) _strumZone = FindStrumZone(strumming);
        }

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
                if (u > 0f) _fretting.Blend(from, to, StrumMath.Ease(u), dt);
                else _fretting.Hold(from);
                _fretting.Fade(_frettingFade);
            }

            _lastStrum = StrumMath.Sample(true, frame.Waiting, frame.Beat, frame.StrumSlots, frame.SlotsPerBeat);
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
                _fretting.Hold(_frettingFrom);
                _fretting.Fade(1f);
            }
            _lastStrum = StrumMath.Sample(false, false, 0.0, null, 1);
            DriveStrumming(dt);
        }

        void DriveStrumming(float dt)
        {
            if (_strumming == null) return;
            StrumPhase p = _lastStrum.Phase;
            if (p == StrumPhase.Rest || p == StrumPhase.Ready) _strumming.Hold(_lastStrum.From);
            else _strumming.Blend(_lastStrum.From, _lastStrum.To, _lastStrum.Blend, dt);
        }

        static float FadeFlag()
        {
            FlagSet flags = FlagStore.Current;
            return flags != null && flags.ghost != null ? flags.ghost.fadeAfterSuccess : DefaultFadeAfterSuccess;
        }

        // ------------------------------------------------------------------------------------------------ the strum anchor

        // The strum zone the hand hangs under, if its parent is a FloatingNeck's StrumZone (then the offset is kept in step).
        static Transform FindStrumZone(PosedGhostHand hand)
        {
            Transform parent = hand.transform.parent;
            if (parent == null || parent.parent == null) return null;
            FloatingNeck neck = parent.parent.GetComponent<FloatingNeck>();
            return neck != null && neck.StrumZone == parent ? parent : null;
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

            public Motion(PosedGhostHand hand)
            {
                Hand = hand;
            }

            public bool Alive { get { return Hand != null && Hand.IsAvailable; } }

            public void Fade(float fade)
            {
                if (Alive) Hand.SetFade(fade);
            }

            public void Hold(string pose)
            {
                if (!Alive) return;
                _blending = false;
                Hand.SetPose(pose, false);
            }

            public void Blend(string from, string to, float u, float dt)
            {
                if (!Alive) return;
                if (!_blending)
                {
                    _blending = true;
                    _settling = Hand.DistanceToBlendMm(from, to, u) > SettleJumpMm;
                    _settleElapsed = 0f;
                    if (_settling) Hand.HoldSnapshot();
                }
                float weight = 1f;
                if (_settling)
                {
                    if (dt > 0f && !float.IsInfinity(dt)) _settleElapsed += dt;
                    weight = StrumMath.Ease(_settleElapsed / SettleSeconds);
                    if (weight >= 1f) _settling = false;
                }
                Hand.ShowBlend(from, to, u, weight);
            }
        }
    }
}
