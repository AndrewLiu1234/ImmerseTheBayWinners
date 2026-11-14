using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// The per-string verdicts of the pluck check: one mark per string at the nut, attached to the neck in space (not in a
    /// panel). A green check, a red-orange cross, a flat bar for a muted string, a question mark, sound waves for "let it
    /// ring": each has its own shape as well as its own colour.
    ///
    /// WHERE THEY SIT. The real strings are 7 mm apart at the nut, which is under half a degree of view and less than the
    /// alignment error, so marks drawn exactly on the string ends would pile up and could not say which string they
    /// mean. Instead the six marks stand in a row across the nut, just beyond it, 1.8 degrees apart, in the order the
    /// strings appear to the player (the layout of the X / O row of a chord diagram). While any mark is showing, the
    /// other strings show their number in pale blue, so a lone mark is never ambiguous: it stands where its number was.
    ///
    /// The marks follow the neck socket in world space every frame and turn to face the head. They are deliberately NOT
    /// parented under the socket, so a mirrored (left-handed) or scaled socket cannot mirror or resize them. Keep this
    /// object under an unscaled parent. Marks ease in; a passed string fades by itself after 1.6 s, a problem stays until
    /// it is replaced or cleared; no mark changes more than 3 times a second. Runs late in the frame so it uses this
    /// frame's head and neck poses. Main thread only.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    [DisallowMultipleComponent]
    public sealed class StringMarks : LearnWidget
    {
        public const int StringCount = 6;

        /// <summary>Cap height of the dim string numbers, degrees (the project's minimum text size).</summary>
        public const float DigitDegrees = HudText.MinTextDegrees;

        /// <summary>
        /// Brightness of the string numbers shown beside a mark. They are what makes a lone mark readable ("this is string
        /// 2"), and on the additive glasses a dim number simply vanishes in a bright room, so they are only a step below
        /// full: about 57% luminance in their pale blue, against 33% at the old 0.7. They stay quieter than the marks by
        /// colour (pale blue against saturated green, red-orange and amber), by size (1.2 against 1.5 degrees) and by
        /// being thin text against heavy strokes.
        /// </summary>
        public const float LegendOpacity = 0.9f;

        const float ResizeFraction = 0.08f;

        readonly Transform[] _slots = new Transform[StringCount];
        readonly HudShape[] _glyphs = new HudShape[StringCount];
        readonly LearnLabel[] _digits = new LearnLabel[StringCount];
        readonly StringMark[] _wanted = new StringMark[StringCount];
        readonly StringMark[] _shown = new StringMark[StringCount];
        readonly float[] _wantedHold = new float[StringCount];
        readonly float[] _holdLeft = new float[StringCount];
        readonly HudFader[] _faders = new HudFader[StringCount];
        readonly ChangeGate[] _gates = new ChangeGate[StringCount];

        HudFader _legend;
        ChangeGate _legendGate;
        bool _legendWanted;

        Transform _socket;
        Vector3 _nutLocal;
        bool _hasAnchors;
        float _glyphSize = 0.01f;
        bool _active;
        bool _warnedString;

        /// <summary>The wearer's head (StereoRig's Head). The marks face it and are sized for its distance. May be null: a typical player's eye position is then assumed.</summary>
        public Transform Head { get; set; }

        /// <summary>Show the other strings' numbers dimly while any mark is showing. Default true.</summary>
        public bool ShowLegend { get; set; } = true;

        /// <summary>How far beyond the nut the row sits, metres.</summary>
        public float BeyondNutM { get; set; } = LearnHudLayout.MarkBeyondNutM;

        /// <summary>Creates the marks under <paramref name="parent"/> (may be null; must be unscaled). <paramref name="viewingDistanceM"/> is used until a head and a neck are known (0 = the default half metre).</summary>
        public static StringMarks Create(Transform parent, float viewingDistanceM)
        {
            return CreateWidget<StringMarks>(parent, "StringMarks", viewingDistanceM);
        }

        protected override void Build()
        {
            for (int i = 0; i < StringCount; i++)
            {
                var slot = new GameObject("String" + (i + 1));
                slot.transform.SetParent(transform, false);
                _slots[i] = slot.transform;

                _digits[i] = LearnLabel.Create(slot.transform, "Number", TextAnchor.MiddleCenter, LearnPalette.Info);
                _digits[i].SetText(LearnText.Digit(i + 1));
                _digits[i].HideNow();

                _glyphs[i] = HudShape.Create(slot.transform, "Mark", LearnGlyph.Check);
                _glyphs[i].SortingOrder = 2;
                _glyphs[i].Opacity = 0f;
                _glyphs[i].Visible = false;

                _wantedHold[i] = float.PositiveInfinity;
                _holdLeft[i] = float.PositiveInfinity;
            }
        }

        protected override void ApplySizes()
        {
            _glyphSize = Meters(LearnHudLayout.MarkDegrees);
            float pitch = Meters(LearnHudLayout.MarkPitchDegrees);
            for (int i = 0; i < StringCount; i++)
            {
                SizeText(_digits[i], DigitDegrees);
                _glyphs[i].SetSize(_glyphSize * _faders[i].Scale);
                // Until a neck socket is given, the row simply runs along this object's own X axis (string 1 on the viewer's right).
                if (!_hasAnchors) _slots[i].localPosition = new Vector3(LearnHudLayout.MarkSlotOffset(i + 1) * pitch, 0f, 0f);
            }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Attaches the marks to the neck. <paramref name="neckSocket"/> is the pinned neck frame (origin at the nut on the
        /// centre line, +x toward the bridge, +y out of the fretboard, +z toward string 1); <paramref name="nutEndLocal"/>
        /// holds the nut end of each string in that frame, in metres, index 0 = string 1 ... index 5 = string 6. The values
        /// are copied. Pass null to detach.
        /// </summary>
        public void SetStringAnchors(Transform neckSocket, Vector3[] nutEndLocal)
        {
            EnsureBuilt();
            _socket = neckSocket;
            _hasAnchors = false;
            if (neckSocket == null || nutEndLocal == null || nutEndLocal.Length == 0)
            {
                ApplySizes();
                return;
            }

            int n = Mathf.Min(nutEndLocal.Length, StringCount);
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < n; i++) sum += nutEndLocal[i];
            _nutLocal = sum / n;
            _hasAnchors = true;
            Place();
        }

        /// <summary>Shows <paramref name="mark"/> for a string (1 = high E ... 6 = low E) for that mark's usual time; None hides it.</summary>
        public void Show(int stringNumber, StringMark mark)
        {
            Show(stringNumber, mark, StringMarkStyle.DefaultHoldSeconds(mark));
        }

        /// <summary>
        /// Shows <paramref name="mark"/> for a string and lets it fade after <paramref name="holdSeconds"/> (infinity or 0
        /// = stay until replaced or cleared). Calling it again with the same values changes nothing, so it is safe to
        /// call every frame.
        /// </summary>
        public void Show(int stringNumber, StringMark mark, float holdSeconds)
        {
            EnsureBuilt();
            if (stringNumber < 1 || stringNumber > StringCount)
            {
                if (!_warnedString)
                {
                    _warnedString = true;
                    UGALog.Warn("string_marks", "ignored a mark for string " + stringNumber + " (strings are 1 to 6)");
                }
                return;
            }

            int i = stringNumber - 1;
            float hold = holdSeconds > 0f ? holdSeconds : float.PositiveInfinity;
            if (_wanted[i] == mark && (mark == StringMark.None || _wantedHold[i] == hold)) return;
            _wanted[i] = mark;
            _wantedHold[i] = hold;
            _gates[i].Request();
            _active = true;
            OnTick(0f);   // if the gate is open the mark is on screen for this frame's render, whatever the script order
        }

        /// <summary>Hides the mark of one string.</summary>
        public void Clear(int stringNumber)
        {
            Show(stringNumber, StringMark.None);
        }

        /// <summary>Fades every mark out.</summary>
        public void ClearAll()
        {
            EnsureBuilt();
            for (int i = 0; i < StringCount; i++)
            {
                if (_wanted[i] == StringMark.None) continue;
                _wanted[i] = StringMark.None;
                _gates[i].Request();
            }
            OnTick(0f);
        }

        /// <summary>The mark last asked for on a string (None if hidden or out of range).</summary>
        public StringMark Get(int stringNumber)
        {
            if (stringNumber < 1 || stringNumber > StringCount) return StringMark.None;
            return _wanted[stringNumber - 1];
        }

        /// <summary>Jumps every mark to its settled look, skipping the animations. For previews.</summary>
        public void Settle()
        {
            EnsureBuilt();
            bool any = false;
            for (int i = 0; i < StringCount; i++)
            {
                _gates[i].Cancel();
                ApplySlot(i);
                if (_wanted[i] != StringMark.None)
                {
                    _faders[i].ShowNow();
                    any = true;
                }
                else
                {
                    _faders[i].HideNow();
                }
                _faders[i].Tick(0f);
            }
            _legendGate.Cancel();
            _legendWanted = any && ShowLegend;
            if (_legendWanted) _legend.ShowNow();
            else _legend.HideNow();
            _legend.Tick(0f);
            _active = any;
            RefreshVisuals();
            Place();
        }

        // ------------------------------------------------------------------ animation

        protected override void OnTick(float dt)
        {
            // Runs even while nothing shows (six trivial steps), so the 3 Hz gates keep counting down and the first mark
            // after a quiet spell appears on the very next frame.
            bool changed = false;
            bool anyShown = false;
            bool anyBusy = false;
            for (int i = 0; i < StringCount; i++)
            {
                if (_gates[i].Tick(dt))
                {
                    ApplySlot(i);
                    changed = true;
                }

                if (_faders[i].Target && !float.IsPositiveInfinity(_holdLeft[i]))
                {
                    _holdLeft[i] -= dt;
                    if (_holdLeft[i] <= 0f)
                    {
                        _holdLeft[i] = float.PositiveInfinity;
                        if (_wanted[i] == _shown[i])
                        {
                            _wanted[i] = StringMark.None;
                            _gates[i].Request();
                        }
                    }
                }

                if (_faders[i].Tick(dt)) changed = true;
                if (_faders[i].Target) anyShown = true;
                if (_faders[i].IsVisible || _gates[i].Pending) anyBusy = true;
            }

            bool legendWanted = anyShown && ShowLegend;
            if (legendWanted != _legendWanted)
            {
                _legendWanted = legendWanted;
                _legendGate.Request();
            }
            if (_legendGate.Tick(dt))
            {
                if (_legendWanted) _legend.Show();
                else _legend.Hide();
            }
            if (_legend.Tick(dt)) changed = true;

            if (changed) RefreshVisuals();
            _active = anyBusy || _legend.IsVisible || _legendGate.Pending;
        }

        void ApplySlot(int i)
        {
            StringMark mark = _wanted[i];
            if (mark == StringMark.None)
            {
                _faders[i].Hide();
                _shown[i] = StringMark.None;
                _holdLeft[i] = float.PositiveInfinity;
                return;
            }

            _glyphs[i].Glyph = StringMarkStyle.Glyph(mark);
            _glyphs[i].Color = StringMarkStyle.Color(mark);
            _shown[i] = mark;
            _holdLeft[i] = _wantedHold[i];
            if (_faders[i].Target) _faders[i].Pulse();
            else _faders[i].Show();
        }

        void RefreshVisuals()
        {
            float legend = _legend.Opacity * LegendOpacity;
            for (int i = 0; i < StringCount; i++)
            {
                float o = _faders[i].Opacity;
                _glyphs[i].Opacity = o;
                _glyphs[i].Visible = o > 0f;
                _glyphs[i].SetSize(_glyphSize * _faders[i].Scale);
                _digits[i].Opacity = legend * (1f - o);   // a mark stands where its string's number was
            }
        }

        // ------------------------------------------------------------------ following the neck

        void LateUpdate()
        {
            if (_active) Place();
        }

        /// <summary>Places the row now. The component does this itself every frame in Play Mode; previews in Edit Mode call it.</summary>
        public void PlaceNow()
        {
            EnsureBuilt();
            Place();
        }

        /// <summary>Moves the row onto the neck and turns every mark to face the eyes. Called every frame while anything shows.</summary>
        void Place()
        {
            if (!_hasAnchors || _socket == null) return;

            Vector3 nut = _socket.TransformPoint(_nutLocal);
            Vector3 towardBridge = _socket.TransformVector(Vector3.right);
            Vector3 outOfFretboard = _socket.TransformVector(Vector3.up);
            Vector3 towardString1 = _socket.TransformVector(Vector3.forward);

            Transform head = Head;
            Vector3 eye = head != null ? head.position : _socket.TransformPoint(LearnHudLayout.TypicalEyeInNeckFrame);
            Vector3 upHint = head != null ? head.up : _socket.TransformVector(LearnHudLayout.TypicalUpInNeckFrame);

            Vector3 centre, row;
            LearnHudLayout.TryMarkRow(eye, nut, towardBridge, towardString1, outOfFretboard, BeyondNutM, out centre, out row);

            float distance = (centre - eye).magnitude;
            if (LearnHudLayout.DistanceChanged(ViewingDistanceM, distance, ResizeFraction)) SetViewingDistance(distance);

            float pitch = Meters(LearnHudLayout.MarkPitchDegrees);
            for (int i = 0; i < StringCount; i++)
            {
                Vector3 p = LearnHudLayout.MarkSlot(centre, row, i + 1, pitch);
                Vector3 forward, up;
                if (LearnHudLayout.TryFacing(eye, p, upHint, out forward, out up))
                    _slots[i].SetPositionAndRotation(p, Quaternion.LookRotation(forward, up));
                else
                    _slots[i].position = p;
            }
        }
    }
}
