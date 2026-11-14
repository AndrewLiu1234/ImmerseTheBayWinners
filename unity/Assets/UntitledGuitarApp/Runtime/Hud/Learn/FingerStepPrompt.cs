using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>How one progress pip of the finger-by-finger build is drawn. Each state has its own shape, not only its own colour.</summary>
    public enum FingerPip
    {
        /// <summary>Not part of this chord's build: not drawn.</summary>
        Hidden = 0,
        /// <summary>A finger already placed and confirmed: a filled dot.</summary>
        Done = 1,
        /// <summary>The finger being placed now: a ring around a dot, larger and brightest.</summary>
        Active = 2,
        /// <summary>A finger still to come: an empty ring.</summary>
        Todo = 3,
    }

    /// <summary>
    /// The finger-by-finger build of a chord: "Finger 2" / "string 5, fret 2", with the finger number in a ring before
    /// the words (the same number the ghost hand shows on that fingertip) and progress pips underneath, one per finger of
    /// the chord (1 to 4): done, now, still to come. One hard shape becomes a few small wins.
    ///
    /// The active finger is highlighted in the ghost-hand blue, or in that finger's own colour when the
    /// ghost.fingerColors flag is on (1 yellow, 2 magenta, 3 lime, 4 orange). The prompt stays until the next step or
    /// <see cref="LearnMessage.Hide"/>; calling <see cref="Show"/> again with the same values changes nothing. Faces +Z; the
    /// Learn HUD puts it on the neck card spot. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FingerStepPrompt : LearnMessage
    {
        /// <summary>A chord is built in at most this many steps (four fretting fingers).</summary>
        public const int MaxSteps = 4;

        /// <summary>Size of the ring that holds the finger number, degrees (before the largeText multiplier, like every size here).</summary>
        public const float BadgeDegrees = 3.0f;

        /// <summary>Cap height of the number in the ring and of "Finger 2", degrees.</summary>
        public const float HeadlineDegrees = 1.7f;

        /// <summary>Cap height of "string 5, fret 2", degrees.</summary>
        public const float DetailDegrees = 1.3f;

        public const float PipDegrees = 0.9f;
        public const float ActivePipDegrees = 1.3f;
        public const float PipPitchDegrees = 1.7f;

        const float GapDegrees = 0.7f;
        const float HeadlineY = 2.0f;
        const float DetailY = -0.5f;
        const float PipY = -2.6f;
        const float BadgeY = 0.75f;

        HudShape _badge;
        LearnLabel _badgeNumber;
        LearnLabel _headline;
        LearnLabel _detail;
        readonly HudShape[] _pips = new HudShape[MaxSteps];
        readonly FingerPip[] _pipKinds = new FingerPip[MaxSteps];

        int _finger;
        int _string;
        int _fret;
        int _step;
        int _stepCount;
        bool _fingerColors;
        string _headlineText = string.Empty;
        string _detailText = string.Empty;

        /// <summary>The finger (1-4) last asked for.</summary>
        public int Finger { get { return _finger; } }

        /// <summary>The step (1-based) last asked for.</summary>
        public int Step { get { return _step; } }

        /// <summary>Creates the prompt under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static FingerStepPrompt Create(Transform parent, float viewingDistanceM)
        {
            return CreateWidget<FingerStepPrompt>(parent, "FingerStepPrompt", viewingDistanceM);
        }

        protected override void BuildContent()
        {
            _badge = HudShape.Create(Content, "FingerRing", LearnGlyph.Ring);
            _badge.SortingOrder = 1;
            _badgeNumber = LearnLabel.Create(Content, "FingerNumber", TextAnchor.MiddleCenter, LearnPalette.Text);
            _headline = LearnLabel.Create(Content, "Headline", TextAnchor.MiddleLeft, LearnPalette.Text);
            _detail = LearnLabel.Create(Content, "Detail", TextAnchor.MiddleLeft, LearnPalette.Text);
            for (int i = 0; i < MaxSteps; i++)
            {
                _pips[i] = HudShape.Create(Content, "Pip" + (i + 1), LearnGlyph.Ring);
                _pips[i].SortingOrder = 1;
            }
        }

        /// <summary>
        /// Shows one step of the build: put <paramref name="finger"/> (1 = index ... 4 = little finger) on
        /// <paramref name="stringNumber"/> (1 = high E ... 6 = low E) at <paramref name="fret"/>. This is step
        /// <paramref name="step"/> of <paramref name="stepCount"/> (1-based; at most 4 pips are drawn).
        /// </summary>
        public void Show(int finger, int stringNumber, int fret, int step, int stepCount)
        {
            EnsureBuilt();
            bool colors = FlagStore.Current.ghost != null && FlagStore.Current.ghost.fingerColors;
            if (IsShowing && finger == _finger && stringNumber == _string && fret == _fret && step == _step
                && stepCount == _stepCount && colors == _fingerColors) return;

            _finger = finger;
            _string = stringNumber;
            _fret = fret;
            _step = step;
            _stepCount = stepCount;
            _fingerColors = colors;
            RequestShow(float.PositiveInfinity);
        }

        protected override void ApplyContent()
        {
            Color fingerColor = LearnPalette.Finger(_finger, _fingerColors);

            _headlineText = LearnText.FingerHeadline(_finger);
            _detailText = LearnText.StringFret(_string, _fret);
            _headline.SetText(_headlineText);
            _headline.Text.Color = fingerColor;
            _detail.SetText(_detailText);
            _badgeNumber.SetText(LearnText.Digit(_finger));
            _badge.Color = fingerColor;

            for (int i = 0; i < MaxSteps; i++)
            {
                FingerPip kind = PipFor(i, _step, _stepCount);
                _pipKinds[i] = kind;
                switch (kind)
                {
                    case FingerPip.Done:
                        _pips[i].Glyph = LearnGlyph.Disc;
                        _pips[i].Color = LearnPalette.Good;
                        break;
                    case FingerPip.Active:
                        _pips[i].Glyph = LearnGlyph.Target;
                        _pips[i].Color = fingerColor;
                        break;
                    default:
                        _pips[i].Glyph = LearnGlyph.Ring;
                        _pips[i].Color = LearnPalette.Quiet;
                        break;
                }
            }
            Layout();
        }

        protected override void ApplySizes()
        {
            SizeText(_headline, HeadlineDegrees);
            SizeText(_detail, DetailDegrees);
            SizeText(_badgeNumber, HeadlineDegrees);
            _badge.SetSize(Meters(BadgeDegrees));
            Layout();
        }

        void Layout()
        {
            float badge = Meters(BadgeDegrees);
            float gap = Meters(GapDegrees);
            float text = Mathf.Max(
                LearnText.WidthInCaps(_headlineText) * _headline.Text.CapHeightMeters,
                LearnText.WidthInCaps(_detailText) * _detail.Text.CapHeightMeters);
            float left = -0.5f * (badge + gap + text);
            float textX = left + badge + gap;

            var badgeCentre = new Vector3(left + 0.5f * badge, Meters(BadgeY), 0f);
            _badge.transform.localPosition = badgeCentre;
            _badgeNumber.Transform.localPosition = badgeCentre;
            _headline.Transform.localPosition = new Vector3(textX, Meters(HeadlineY), 0f);
            _detail.Transform.localPosition = new Vector3(textX, Meters(DetailY), 0f);

            float pitch = Meters(PipPitchDegrees);
            float pipY = Meters(PipY);
            float firstX = textX + 0.5f * Meters(ActivePipDegrees);
            for (int i = 0; i < MaxSteps; i++)
            {
                _pips[i].SetSize(Meters(_pipKinds[i] == FingerPip.Active ? ActivePipDegrees : PipDegrees));
                _pips[i].transform.localPosition = new Vector3(firstX + i * pitch, pipY, 0f);
            }
        }

        protected override void ApplyVisual(float opacity)
        {
            bool on = opacity > 0f;
            _badge.Opacity = opacity;
            _badge.Visible = on;
            _badgeNumber.Opacity = opacity;
            _headline.Opacity = opacity;
            _detail.Opacity = opacity;
            for (int i = 0; i < MaxSteps; i++)
            {
                bool drawn = _pipKinds[i] != FingerPip.Hidden;
                _pips[i].Opacity = drawn ? opacity : 0f;
                _pips[i].Visible = drawn && on;
            }
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>
        /// The state of pip <paramref name="pipIndex"/> (0-based) when the player is on step <paramref name="step"/>
        /// (1-based) of <paramref name="stepCount"/>. The count is limited to 1..4 and the step to 1..count, so a bad
        /// value still draws a sensible row.
        /// </summary>
        public static FingerPip PipFor(int pipIndex, int step, int stepCount)
        {
            int count = Mathf.Clamp(stepCount, 1, MaxSteps);
            if (pipIndex < 0 || pipIndex >= count) return FingerPip.Hidden;
            int active = Mathf.Clamp(step, 1, count) - 1;
            if (pipIndex < active) return FingerPip.Done;
            return pipIndex == active ? FingerPip.Active : FingerPip.Todo;
        }
    }
}
