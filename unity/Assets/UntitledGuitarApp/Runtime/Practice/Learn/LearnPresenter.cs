// LearnPresenter: learn mode on screen. Binds the Learn HUD (Runtime/Hud/Learn), the floating neck and the fretting ghost hand
// (the Blender-posed PosedGhostHand when Bootstrap has one, else the CapsuleHand) to one LearnFlow for a chosen chord:
//
//   Tune            the needle per string (6 .. 1); the neck and the hand are hidden (the tuner is head-locked, straight
//                   ahead, and the chord is not wanted yet)
//   FingerByFinger  the finger prompt on the neck card; the neck's dot of that finger stays bright and the others dim; the
//                   capsule hand lights the same finger (CapsuleHand.SetActiveFinger). The posed hand is ONE mesh and can only
//                   dim as a whole (PosedGhostHand.SetEmphasis), so it is dimmed a little and the dot carries the emphasis
//   PluckCheck      one mark per string IN the neck's X / O column (see LearnNeckColumn), one tip on the card
//   StrumCheck      a check or the strum's top issue on the card
//   Done            the fretting ghost fades to the ghost.fadeAfterSuccess level
//
// Verdicts come in through Verdicts (ILearnVerdictSink): TEMPORARY Dev/DevLearnScript today, Core's adapter later.
// Everything is created from code (Create); in Play Mode Update/LateUpdate drive it, in Edit Mode (previews) call Tick and
// LateTick yourself. Main thread only; no allocation per frame.
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.GhostHand;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;
using LearnMark = UntitledGuitarApp.Runtime.Hud.Learn.StringMark;
using NeckMark = UntitledGuitarApp.Runtime.Neck.StringMark;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    [DefaultExecutionOrder(19990)]   // after head pose and EditorSimRig; before the Learn HUD places its marks (20000)
    [DisallowMultipleComponent]
    public sealed class LearnPresenter : MonoBehaviour, ILearnView
    {
        /// <summary>The needle must rest in tune this long before the next string is asked for (AutoAdvanceTuning).</summary>
        public const float InTuneAdvanceSeconds = 0.8f;

        /// <summary>Whole-hand brightness of the posed hand during finger by finger (it cannot light one finger).</summary>
        public const float PosedStepEmphasis = 0.6f;

        /// <summary>Size of a learn mark standing in the neck's column, degrees: as tall as the neck's own X / O (NeckStyle.MarkDeg).</summary>
        public const float ColumnMarkDegrees = NeckStyle.MarkDeg;

        LearnHud _hud;
        FloatingNeck _neck;
        PosedGhostHand _posed;
        CapsuleHand _capsule;
        LearnFlow _flow;

        readonly FingertipDot[] _dots = new FingertipDot[FloatingNeck.MaxDots];
        int _dotCount = -1;                                           // -1: leave the neck's dots as Bootstrap set them
        readonly NeckMark[] _chordMarks = new NeckMark[LearnChord.StringCount];
        readonly NeckMark[] _neckShown = new NeckMark[LearnChord.StringCount];
        readonly LearnMark[] _verdicts = new LearnMark[LearnChord.StringCount];
        readonly Vector3[] _slotLocal = new Vector3[LearnChord.StringCount];
        readonly Vector3[] _targets = new Vector3[4];

        bool _docked;
        bool _restorePending;
        bool _neckHidden;
        float _inTuneFor;
        Vector3 _slotAlong = Vector3.right, _slotAcross = Vector3.forward;

        /// <summary>Hide the neck (and so the hand) while tuning. Default true.</summary>
        public bool HideNeckWhileTuning = true;

        /// <summary>Move to the next string by itself once the needle has rested in tune (a Core adapter may instead call OnStringInTune).</summary>
        public bool AutoAdvanceTuning = true;

        /// <summary>
        /// During the pluck check, stand the learn marks in the neck's X / O column and let them replace the X / O there
        /// (one mark per string). False = the old behaviour (both rows drawn, overlapping): kept only so a preview can prove
        /// that its overlap check fires on it.
        /// </summary>
        public bool ShareNeckColumn = true;

        public LearnHud Hud { get { return _hud; } }
        public FloatingNeck Neck { get { return _neck; } }
        public LearnFlow Flow { get { return _flow; } }

        /// <summary>Where verdict sources send their events.</summary>
        public ILearnVerdictSink Verdicts { get { return _flow; } }

        public LearnStage Stage { get { return _flow != null ? _flow.Stage : LearnStage.Idle; } }
        public LearnChord Chord { get { return _flow != null ? _flow.Chord : null; } }

        /// <summary>"posed" or "capsule": which fretting hand is driven.</summary>
        public string HandKind { get { return _posed != null ? "posed" : _capsule != null ? "capsule" : "none"; } }

        /// <summary>The hand object that is drawn (for previews), or null.</summary>
        public GameObject HandObject
        {
            get { return _posed != null ? _posed.gameObject : _capsule != null ? _capsule.gameObject : null; }
        }

        /// <summary>
        /// Creates the presenter under <paramref name="parent"/>. <paramref name="posedHand"/> may be null or unavailable (then
        /// <paramref name="capsuleHand"/> is driven); both null is allowed (no hand).
        /// </summary>
        public static LearnPresenter Create(Transform parent, LearnHud hud, FloatingNeck neck, PosedGhostHand posedHand, CapsuleHand capsuleHand)
        {
            var go = new GameObject("LearnPresenter");
            if (parent != null) go.transform.SetParent(parent, false);
            LearnPresenter p = go.AddComponent<LearnPresenter>();
            p._hud = hud;
            p._neck = neck;
            p._posed = posedHand != null && posedHand.IsAvailable ? posedHand : null;
            p._capsule = capsuleHand;
            p._flow = new LearnFlow(p);
            return p;
        }

        // ------------------------------------------------------------------ control

        /// <summary>
        /// Starts learn mode for <paramref name="chord"/>. <paramref name="dots"/> (neck frame, from the chord's shape) are put
        /// on the neck and given to the hand; null keeps the neck's current dots. False when the chord is null.
        /// </summary>
        public bool Begin(LearnChord chord, FingertipDot[] dots, int dotCount)
        {
            if (chord == null || _flow == null) return false;
            if (dots != null)
            {
                _dotCount = Mathf.Clamp(dotCount, 0, Mathf.Min(dots.Length, _dots.Length));
                for (int i = 0; i < _dotCount; i++) _dots[i] = dots[i];
            }
            LearnNeckColumn.ChordMarks(chord, _chordMarks);
            ShowChordOnNeckAndHand(chord);
            UGALog.Event("learn_begin", "chord", chord.Name, "hand", HandKind);
            return _flow.Begin(chord);
        }

        public void Stop()
        {
            if (_flow != null) _flow.Stop();
        }

        public void Skip()
        {
            if (_flow != null) _flow.Skip();
        }

        public void SkipTo(LearnStage stage)
        {
            if (_flow != null) _flow.SkipTo(stage);
        }

        /// <summary>The X / O the presenter last gave the neck for a string (1 .. 6), for previews and the spectator.</summary>
        public NeckMark NeckMarkShown(int stringNumber)
        {
            return stringNumber >= 1 && stringNumber <= LearnChord.StringCount ? _neckShown[stringNumber - 1] : NeckMark.None;
        }

        /// <summary>True while the learn marks stand in the neck's X / O column.</summary>
        public bool MarksDocked { get { return _docked; } }

        /// <summary>The chord's dots as given to Begin (count -1 = none given).</summary>
        public int DotCount { get { return _dotCount; } }

        public FingertipDot Dot(int index)
        {
            return index >= 0 && index < _dotCount ? _dots[index] : default(FingertipDot);
        }

        /// <summary>
        /// The neck's X / O slot of a string in world space, with the two directions its mark is drawn on and the mark's
        /// half height (metres). False without a built neck. For previews (the overlap check) and for docking.
        /// </summary>
        public bool TryGetColumnSlot(int stringNumber, out Vector3 centre, out Vector3 along, out Vector3 across, out float halfHeightM)
        {
            centre = along = across = Vector3.zero;
            halfHeightM = 0f;
            if (stringNumber < 1 || stringNumber > LearnChord.StringCount || !UpdateColumnSlots()) return false;
            Transform frame = _neck.Socket;
            centre = frame.TransformPoint(_slotLocal[stringNumber - 1]);
            along = frame.TransformVector(_slotAlong).normalized;
            across = frame.TransformVector(_slotAcross).normalized;
            halfHeightM = frame.TransformVector(_slotAlong * (_neck.Sizes.markRadiusMm * NeckMath.MetresPerMm)).magnitude;
            return true;
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        void LateUpdate()
        {
            LateTick();
        }

        /// <summary>One step: the flow's timed hold, the tuner's auto-advance and the deferred return of the X / O marks.</summary>
        public void Tick(float dt)
        {
            if (_flow == null) return;
            _flow.Tick(dt);

            if (_flow.Stage == LearnStage.Tune && AutoAdvanceTuning && _hud != null && _hud.Tuner != null && _hud.Tuner.IsInTune)
            {
                _inTuneFor += dt;
                if (_inTuneFor >= InTuneAdvanceSeconds)
                {
                    _inTuneFor = 0f;
                    _flow.OnStringInTune(_flow.TuneString);
                }
            }
            else
            {
                _inTuneFor = 0f;
            }

            // The chord's X / O come back only once the learn marks have faded, so the two never share a frame.
            if (_restorePending && (_hud == null || !_hud.Marks.AnyVisible))
            {
                _restorePending = false;
                Undock();
                PushNeckMarks(_chordMarks);
            }
        }

        /// <summary>Keeps docked learn marks on the neck's X / O slots (they follow the eye). LateUpdate calls it.</summary>
        public void LateTick()
        {
            if (_docked && UpdateColumnSlots()) _hud.Marks.SetSlotAnchors(_neck.Socket, _slotLocal);
        }

        bool UpdateColumnSlots()
        {
            if (_neck == null || _neck.Layout == null) return false;
            Transform viewer = _neck.Viewer != null ? _neck.Viewer : _hud != null ? _hud.Head : null;
            Vector3 viewerMm = viewer != null ? _neck.transform.InverseTransformPoint(viewer.position) / NeckMath.MetresPerMm : Vector3.zero;
            return LearnNeckColumn.SlotCentres(_neck.Sizes, _neck.Layout.stringHeightMm, viewer != null, viewerMm,
                _slotLocal, out _slotAlong, out _slotAcross);
        }

        // ------------------------------------------------------------------ ILearnView

        void ILearnView.EnterStage(LearnStage stage, LearnChord chord)
        {
            switch (stage)
            {
                case LearnStage.Idle:
                    if (_hud != null) _hud.ClearAll();
                    SetNeckHidden(false);
                    if (_docked || !SameMarks(_neckShown, _chordMarks)) _restorePending = true;
                    SetFingerEmphasis(-1);
                    SetHandFade(1f);
                    break;
                case LearnStage.Tune:
                    if (_hud != null) _hud.ClearMessage();
                    if (HideNeckWhileTuning) SetNeckHidden(true);
                    break;
                case LearnStage.PluckCheck:
                    _restorePending = false;
                    for (int i = 0; i < _verdicts.Length; i++) _verdicts[i] = LearnMark.None;
                    if (ShareNeckColumn) Dock();
                    break;
                case LearnStage.Done:
                    FlagSet flags = FlagStore.Current;
                    SetHandFade(flags != null && flags.ghost != null ? flags.ghost.fadeAfterSuccess : 0.4f);
                    break;
            }
            UGALog.Event("learn_stage", "stage", stage.ToString(), "chord", chord != null ? chord.Name : "");
        }

        void ILearnView.LeaveStage(LearnStage stage, LearnChord chord)
        {
            switch (stage)
            {
                case LearnStage.Tune:
                    if (_hud != null) _hud.Tuner.Clear();
                    SetNeckHidden(false);
                    break;
                case LearnStage.FingerByFinger:
                    SetFingerEmphasis(-1);
                    if (_hud != null) _hud.ClearMessage();
                    break;
                case LearnStage.PluckCheck:
                    if (_hud != null) _hud.Marks.ClearAll();
                    _restorePending = true;            // Tick puts the X / O back once the marks are gone
                    break;
                case LearnStage.Done:
                    SetHandFade(1f);
                    break;
            }
        }

        void ILearnView.ShowTunerTarget(int stringNumber, string noteName)
        {
            _inTuneFor = 0f;
            if (_hud != null) _hud.Tuner.SetTarget(stringNumber, noteName);
        }

        void ILearnView.ShowTunerReading(float cents, float confidence)
        {
            if (_hud != null) _hud.Tuner.SetReading(cents, confidence);
        }

        void ILearnView.ShowFingerStep(int finger, int stringNumber, int fret, int step, int stepCount)
        {
            if (_hud != null) _hud.ShowFingerStep(finger, stringNumber, fret, step, stepCount);
            SetFingerEmphasis(finger);
        }

        void ILearnView.ShowStringMark(int stringNumber, LearnMark mark)
        {
            if (_hud == null || stringNumber < 1 || stringNumber > LearnChord.StringCount) return;
            // During the check a passed string keeps its check until the check ends: it stands in for the string's X / O,
            // and a slot that empties by itself would leave that string without any mark.
            _hud.Marks.Show(stringNumber, mark, float.PositiveInfinity);
            _verdicts[stringNumber - 1] = mark;
            if (_docked) RefreshColumn();
        }

        void ILearnView.ShowTip(string tip, TipIcon icon)
        {
            if (_hud != null) _hud.ShowTip(tip, icon);
        }

        void ILearnView.ShowStrum(bool clean, string issue, TipIcon icon)
        {
            if (_hud != null) _hud.ShowStrum(clean, issue, icon);
        }

        // ------------------------------------------------------------------ neck and hand

        void ShowChordOnNeckAndHand(LearnChord chord)
        {
            if (_neck != null)
            {
                if (_dotCount >= 0) _neck.SetDots(_dots, _dotCount);
                PushNeckMarks(_chordMarks);
            }
            if (_posed != null) _posed.SetPose(chord.Name, false);
            if (_capsule != null && _dotCount >= 0) _capsule.SetFingertipTargets(LearnNeckColumn.FingertipTargets(_dots, _dotCount, _targets));
        }

        void Dock()
        {
            if (_hud == null || _neck == null || !UpdateColumnSlots())
            {
                PushNeckMarks(null);    // no column to share: at least never draw two rows
                return;
            }
            _docked = true;
            _hud.Marks.MarkSizeDegrees = ColumnMarkDegrees;
            _hud.Marks.SetSlotAnchors(_neck.Socket, _slotLocal);
            RefreshColumn();
        }

        void Undock()
        {
            if (_hud != null)
            {
                _hud.Marks.SetSlotAnchors(null, null);
                _hud.Marks.MarkSizeDegrees = LearnHudLayout.MarkDegrees;
                for (int s = 1; s <= LearnChord.StringCount; s++) _hud.Marks.SuppressLegend(s, false);
            }
            _docked = false;
        }

        // The neck keeps only the X marks no verdict has replaced; those slots keep their string number hidden.
        void RefreshColumn()
        {
            var column = _neckShownScratch;
            LearnNeckColumn.ColumnMarksDuringPluck(Chord, _verdicts, column);
            PushNeckMarks(column);
            for (int i = 0; i < column.Length; i++) _hud.Marks.SuppressLegend(i + 1, column[i] != NeckMark.None);
        }

        readonly NeckMark[] _neckShownScratch = new NeckMark[LearnChord.StringCount];

        void PushNeckMarks(NeckMark[] marks)
        {
            for (int i = 0; i < _neckShown.Length; i++) _neckShown[i] = marks != null && i < marks.Length ? marks[i] : NeckMark.None;
            if (_neck != null) _neck.SetStringMarks(_neckShown);
        }

        static bool SameMarks(NeckMark[] a, NeckMark[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        void SetNeckHidden(bool hidden)
        {
            if (_neck == null || hidden == _neckHidden) return;
            _neckHidden = hidden;
            _neck.gameObject.SetActive(!hidden);
        }

        // finger 1-4 lights that finger (neck dot, capsule finger); -1 lights everything again.
        void SetFingerEmphasis(int finger)
        {
            bool one = finger >= 1 && finger <= 4;
            if (_neck != null) _neck.SetActiveFinger(one ? finger : -1);
            if (_capsule != null) _capsule.SetActiveFinger(one ? finger : CapsuleHand.AllFingers);
            if (_posed != null) _posed.SetEmphasis(one ? PosedStepEmphasis : 1f);
        }

        void SetHandFade(float fade)
        {
            if (_posed != null) _posed.SetFade(fade);
            if (_capsule != null) _capsule.SetFade(fade);
        }
    }
}
