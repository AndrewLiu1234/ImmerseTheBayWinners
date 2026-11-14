using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// The tuner: a dial of +-50 cents with a pointer, the note name large in the middle, the string under it, and one
    /// short instruction. Flat and sharp are shown three ways, never by colour alone: by the pointer's position (left =
    /// too low, right = too high), by words a beginner knows ("Too low: tighten", "Too high: loosen", "In tune!") and by
    /// an icon (arrow up, arrow down, check).
    ///
    /// The pointer eases and never jitters: readings pass a confidence gate and are smoothed, and when the note dies away
    /// the pointer keeps its place and dims (an old reading) instead of dropping to the centre, which would look like
    /// "in tune". "In tune" needs the pointer inside +-5 cents for 0.3 s and is kept until it leaves +-8 cents. The words
    /// change at most 3 times a second. It only displays: the pitch comes from Core through <see cref="SetReading"/>.
    ///
    /// With the debugOverlay flag a line shows the cents; largeText enlarges everything. The object's origin is the
    /// centre of the whole block (about 14 degrees high, 11 wide). Faces +Z: parent it to the head (StereoRig's Head)
    /// with identity rotation, at LearnHudLayout.TunerHeadLocal(). Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TunerNeedle : LearnWidget
    {
        /// <summary>Radius of the dial, degrees of view (before the largeText multiplier, like every size here).</summary>
        public const float DialRadiusDegrees = 8f;

        /// <summary>Cap height of the note name, degrees: "the string and note name large".</summary>
        public const float NoteDegrees = 3.2f;

        public const float StringDegrees = 1.4f;
        public const float StatusDegrees = 1.6f;
        public const float StatusIconDegrees = 2.0f;
        public const float DebugDegrees = HudText.MinTextDegrees;

        public const string TooLowText = "Too low: tighten";
        public const string TooHighText = "Too high: loosen";
        public const string InTuneText = "In tune!";

        // Vertical positions, degrees, measured from the dial's pivot (the block is then centred on the object's origin).
        const float PivotY = -2.25f;
        const float NoteY = 2.3f;
        const float StringY = -0.9f;
        const float StatusY = -3.6f;
        const float DebugY = -6.0f;
        const float StatusGapDegrees = 0.5f;

        const float ZoneRestOpacity = 0.55f;
        const float StaleNeedleOpacity = 0.35f;
        const float NeedleFadeSeconds = 0.15f;

        Transform _dial;
        HudShape _track;
        HudShape _zone;
        HudShape _needle;
        HudShape _statusIcon;
        LearnLabel _note;
        LearnLabel _string;
        LearnLabel _status;
        LearnLabel _debug;

        TunerModel _model;
        HudFader _fader;
        ChangeGate _targetGate;
        ChangeGate _statusGate;

        bool _wantVisible;
        int _wantString;
        string _wantNote = string.Empty;
        TunerState _pendingState = TunerState.Idle;
        TunerState _shownState = TunerState.Idle;
        int _shownString;
        string _statusText = string.Empty;
        bool _statusHasIcon;

        float _needleDegreesShown = float.NaN;
        float _needleLevel;
        float _needleLevelShown = -1f;
        float _zoneLevel = ZoneRestOpacity;
        bool _debugOn;
        float _lastCents;
        string _debugTextShown;

        /// <summary>Readings with less confidence than this are ignored. Default 0.8 (the plan's figure); set it to Core's scale if that differs.</summary>
        public float MinConfidence { get; set; } = TunerMath.DefaultMinConfidence;

        /// <summary>What the tuner currently says (for the spectator panel or an auto-advance rule).</summary>
        public TunerState State { get { return _model.State; } }

        /// <summary>True once the pointer has rested in the in-tune zone.</summary>
        public bool IsInTune { get { return _model.InTune; } }

        /// <summary>The eased pitch error the pointer shows, cents.</summary>
        public float ShownCents { get { return _model.ShownCents; } }

        /// <summary>Creates the tuner under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static TunerNeedle Create(Transform parent, float viewingDistanceM)
        {
            return CreateWidget<TunerNeedle>(parent, "TunerNeedle", viewingDistanceM);
        }

        protected override void Build()
        {
            var dial = new GameObject("Dial");
            dial.transform.SetParent(transform, false);
            _dial = dial.transform;

            _track = HudShape.Create(_dial, "Track", LearnGlyph.DialTrack);
            _track.Color = LearnPalette.Quiet;
            _track.SortingOrder = 0;
            _zone = HudShape.Create(_dial, "InTuneZone", LearnGlyph.DialZone);
            _zone.Color = LearnPalette.Good;
            _zone.SortingOrder = 1;
            _needle = HudShape.Create(_dial, "Needle", LearnGlyph.DialNeedle);
            _needle.Color = LearnPalette.Text;
            _needle.SortingOrder = 2;

            _note = LearnLabel.Create(transform, "Note", TextAnchor.MiddleCenter, LearnPalette.Text);
            _string = LearnLabel.Create(transform, "String", TextAnchor.MiddleCenter, LearnPalette.Info);
            _status = LearnLabel.Create(transform, "Status", TextAnchor.MiddleLeft, LearnPalette.Info);
            _statusIcon = HudShape.Create(transform, "StatusIcon", LearnGlyph.ArrowUp);
            _statusIcon.Color = LearnPalette.Caution;
            _debug = LearnLabel.Create(transform, "Debug", TextAnchor.MiddleCenter, LearnPalette.Info);

            ApplyOpacity();
        }

        protected override void ApplySizes()
        {
            float radius = Meters(DialRadiusDegrees);
            _dial.localPosition = new Vector3(0f, Meters(PivotY), 0f);
            _track.SetSize(radius);
            _zone.SetSize(radius);
            _needle.SetSize(radius);

            SizeText(_note, NoteDegrees);
            SizeText(_string, StringDegrees);
            SizeText(_status, StatusDegrees);
            SizeText(_debug, DebugDegrees);
            _statusIcon.SetSize(Meters(StatusIconDegrees));

            _note.Transform.localPosition = new Vector3(0f, Meters(PivotY + NoteY), 0f);
            _string.Transform.localPosition = new Vector3(0f, Meters(PivotY + StringY), 0f);
            _debug.Transform.localPosition = new Vector3(0f, Meters(PivotY + DebugY), 0f);
            LayoutStatus();
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Chooses the string being tuned: its number (1 = high E ... 6 = low E) and the note it should sound ("E", "A",
        /// "F#"). Shows the tuner and starts listening afresh. Repeating the same target changes nothing.
        /// </summary>
        public void SetTarget(int stringNumber, string noteName)
        {
            EnsureBuilt();
            string note = noteName == null ? string.Empty : noteName.Trim();
            if (_wantVisible && stringNumber == _wantString && !HudText.NeedsRebuild(_wantNote, note)) return;
            _wantVisible = true;
            _wantString = stringNumber;
            _wantNote = note;
            _model.SetTarget();
            _targetGate.Request();
            OnTick(0f);
        }

        /// <summary>
        /// The latest pitch reading for the target string: <paramref name="cents"/> away from the target (negative =
        /// flat) and how sure the detector is (<paramref name="confidence"/> 0..1). Call it as often as readings arrive;
        /// unclear ones are ignored. Does nothing before <see cref="SetTarget"/>.
        /// </summary>
        public void SetReading(float cents, float confidence)
        {
            EnsureBuilt();
            if (_model.SetReading(cents, confidence, MinConfidence)) _lastCents = cents;
        }

        /// <summary>No string is being tuned: fades the tuner out.</summary>
        public void Clear()
        {
            EnsureBuilt();
            if (!_wantVisible) return;
            _wantVisible = false;
            _model.Clear();
            _targetGate.Request();
            OnTick(0f);
        }

        // ------------------------------------------------------------------ animation

        protected override void OnTick(float dt)
        {
            if (_targetGate.Tick(dt))
            {
                if (_wantVisible)
                {
                    _note.SetText(_wantNote.Length > 0 ? _wantNote : "?");
                    _string.SetText(LearnText.StringLabel(_wantString));
                    _shownString = _wantString;
                    _pendingState = _model.State;
                    _statusGate.Cancel();
                    ApplyState(_pendingState);
                    if (_fader.Target) _fader.Pulse();
                    else _fader.Show();
                }
                else
                {
                    _fader.Hide();
                }
            }

            if (!_wantVisible && !_fader.IsVisible) return;   // hidden: nothing to animate

            _model.Tick(dt);

            TunerState state = _model.State;
            if (_wantVisible && state != _pendingState)
            {
                _pendingState = state;
                _statusGate.Request();
            }
            if (_statusGate.Tick(dt) && _wantVisible && _shownString == _wantString) ApplyState(_pendingState);

            float degrees = _model.NeedleDegrees;
            if (float.IsNaN(_needleDegreesShown) || Mathf.Abs(degrees - _needleDegreesShown) > 0.02f)
            {
                _needleDegreesShown = degrees;
                _needle.transform.localRotation = TunerMath.NeedleRotation(degrees);
            }

            float needleTarget = !_model.HasReading ? 0f : (_model.NeedleLive ? 1f : StaleNeedleOpacity);
            _needleLevel = LearnTiming.ExpSmooth(_needleLevel, needleTarget, dt, NeedleFadeSeconds);
            if (Mathf.Abs(_needleLevel - needleTarget) < 0.01f) _needleLevel = needleTarget;

            bool debug = FlagStore.Current.debugOverlay;
            bool faded = _fader.Tick(dt);
            if (faded || debug != _debugOn || Mathf.Abs(_needleLevel - _needleLevelShown) > 0.004f)
            {
                _debugOn = debug;
                ApplyOpacity();
            }

            if (_debugOn)
            {
                string text = _model.HasReading ? LearnText.CentsText(_lastCents) : string.Empty;
                if (!ReferenceEquals(text, _debugTextShown))
                {
                    _debugTextShown = text;
                    _debug.SetText(text);
                }
            }
        }

        // Words, icon and colours for a state. Called only when the state shown changes (at most 3 times a second).
        void ApplyState(TunerState state)
        {
            _shownState = state;
            LearnGlyph icon = LearnGlyph.Check;
            Color textColor = LearnPalette.Text;
            Color iconColor = LearnPalette.Caution;
            _statusHasIcon = true;
            bool inTune = false;

            switch (state)
            {
                case TunerState.TooLow:
                    _statusText = TooLowText;
                    icon = LearnGlyph.ArrowUp;       // the pitch has to go up
                    break;
                case TunerState.TooHigh:
                    _statusText = TooHighText;
                    icon = LearnGlyph.ArrowDown;
                    break;
                case TunerState.InTune:
                    _statusText = InTuneText;
                    icon = LearnGlyph.Check;
                    textColor = LearnPalette.Good;
                    iconColor = LearnPalette.Good;
                    inTune = true;
                    break;
                default:
                    _statusText = LearnText.PluckPrompt(_wantString);
                    textColor = LearnPalette.Info;
                    _statusHasIcon = false;
                    break;
            }

            _status.SetText(_statusText);
            _status.Text.Color = textColor;
            if (_statusHasIcon)
            {
                _statusIcon.Glyph = icon;
                _statusIcon.Color = iconColor;
            }
            _needle.Color = inTune ? LearnPalette.Good : LearnPalette.Text;
            _zoneLevel = inTune ? 1f : ZoneRestOpacity;
            LayoutStatus();
            ApplyOpacity();
        }

        // The icon and the words as one centred row.
        void LayoutStatus()
        {
            float cap = _status.Text.CapHeightMeters;
            float icon = _statusHasIcon ? Meters(StatusIconDegrees) : 0f;
            float gap = _statusHasIcon ? Meters(StatusGapDegrees) : 0f;
            float text = LearnText.WidthInCaps(_statusText) * cap;
            float left = -0.5f * (icon + gap + text);
            float y = Meters(PivotY + StatusY);
            _statusIcon.transform.localPosition = new Vector3(left + 0.5f * icon, y, 0f);
            _status.Transform.localPosition = new Vector3(left + icon + gap, y, 0f);
        }

        void ApplyOpacity()
        {
            float o = _fader.Opacity;
            bool on = o > 0f;
            _track.Opacity = o;
            _track.Visible = on;
            _zone.Opacity = o * _zoneLevel;
            _zone.Visible = on;
            _needleLevelShown = _needleLevel;
            _needle.Opacity = o * _needleLevel;
            _needle.Visible = on && _needleLevel > 0f;
            _note.Opacity = o;
            _string.Opacity = o;
            _status.Opacity = o;
            _statusIcon.Opacity = o;
            _statusIcon.Visible = on && _statusHasIcon;
            _debug.Opacity = _debugOn ? o : 0f;
        }
    }
}
