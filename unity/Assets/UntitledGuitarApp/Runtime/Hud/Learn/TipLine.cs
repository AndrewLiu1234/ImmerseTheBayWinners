using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// One short tip, readable at a glance: an icon first, then 7 words or fewer, for example an arch icon and
    /// "Arch finger 3, B string muted". Kind and specific; it says what to do, never just "error".
    ///
    /// The words are set on at most two short lines (broken where the sentence breaks), bold, 1.3 degrees tall, white; the
    /// icon carries the colour. The block is centred on this object's origin, which the Learn HUD puts on the neck card
    /// spot. A tip stays until <see cref="LearnMessage.Hide"/> or the next tip (the player needs it while fixing a
    /// finger); it eases in and never changes more than 3 times a second. Showing the same tip again changes nothing, so
    /// it is safe to call every frame. Faces +Z. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TipLine : LearnMessage
    {
        /// <summary>Cap height of the tip's words, degrees (a little above the 1.2 degree minimum), before the largeText multiplier.</summary>
        public const float TextDegrees = 1.3f;

        /// <summary>Size of the icon, degrees.</summary>
        public const float IconDegrees = 2.2f;

        /// <summary>Gap between the icon and the words, degrees.</summary>
        public const float GapDegrees = 0.6f;

        /// <summary>Distance between the two lines' centres, in cap heights.</summary>
        public const float LineStepInCaps = 1.8f;

        HudShape _icon;
        LearnLabel _line1;
        LearnLabel _line2;

        string _tip = string.Empty;
        TipIcon _tipIcon;
        string _shown1 = string.Empty;
        string _shown2 = string.Empty;
        bool _hasIcon;
        string _lastWarned;

        /// <summary>The tip last asked for.</summary>
        public string Tip { get { return _tip; } }

        /// <summary>The icon last asked for.</summary>
        public TipIcon Icon { get { return _tipIcon; } }

        /// <summary>Creates the tip line under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static TipLine Create(Transform parent, float viewingDistanceM)
        {
            return CreateWidget<TipLine>(parent, "TipLine", viewingDistanceM);
        }

        protected override void BuildContent()
        {
            _icon = HudShape.Create(Content, "Icon", LearnGlyph.Arch);
            _icon.SortingOrder = 1;
            _line1 = LearnLabel.Create(Content, "Line1", TextAnchor.MiddleLeft, LearnPalette.Text);
            _line2 = LearnLabel.Create(Content, "Line2", TextAnchor.MiddleLeft, LearnPalette.Text);
        }

        /// <summary>
        /// Shows <paramref name="tip"/> with <paramref name="icon"/> before it. An empty tip hides the line. A tip longer
        /// than 7 words is still shown whole (never cut), and a warning is logged once for it.
        /// </summary>
        public void Show(string tip, TipIcon icon)
        {
            EnsureBuilt();
            string t = tip == null ? string.Empty : tip.Trim();
            if (t.Length == 0)
            {
                Hide();
                return;
            }
            if (IsShowing && icon == _tipIcon && !HudText.NeedsRebuild(_tip, t)) return;

            _tip = t;
            _tipIcon = icon;
            RequestShow(float.PositiveInfinity);

            if (!LearnText.WithinWordLimit(t) && HudText.NeedsRebuild(_lastWarned, t))
            {
                _lastWarned = t;
                UGALog.Warn("tip_too_long", "more than " + LearnText.MaxTipWords + " words: " + t);
            }
        }

        protected override void ApplyContent()
        {
            LearnText.WrapTwoLines(_tip, out _shown1, out _shown2);
            _line1.SetText(_shown1);
            _line2.SetText(_shown2);

            _hasIcon = TipIconStyle.HasGlyph(_tipIcon);
            if (_hasIcon)
            {
                _icon.Glyph = TipIconStyle.Glyph(_tipIcon);
                _icon.Color = TipIconStyle.Color(_tipIcon);
            }
            Layout();
        }

        protected override void ApplySizes()
        {
            SizeText(_line1, TextDegrees);
            SizeText(_line2, TextDegrees);
            _icon.SetSize(Meters(IconDegrees));
            Layout();
        }

        void Layout()
        {
            float cap = _line1.Text.CapHeightMeters;
            float icon = _hasIcon ? Meters(IconDegrees) : 0f;
            float gap = _hasIcon ? Meters(GapDegrees) : 0f;
            float text = BlockWidthInCaps(_shown1, _shown2) * cap;
            float left = -0.5f * (icon + gap + text);
            float textX = left + icon + gap;
            bool two = _shown2.Length > 0;
            float half = two ? 0.5f * LineStepInCaps * cap : 0f;

            _icon.transform.localPosition = new Vector3(left + 0.5f * icon, 0f, 0f);
            _line1.Transform.localPosition = new Vector3(textX, half, 0f);
            _line2.Transform.localPosition = new Vector3(textX, -half, 0f);
        }

        protected override void ApplyVisual(float opacity)
        {
            _line1.Opacity = opacity;
            _line2.Opacity = _shown2.Length > 0 ? opacity : 0f;
            _icon.Opacity = opacity;
            _icon.Visible = _hasIcon && opacity > 0f;
        }

        /// <summary>Estimated width of a two-line block of text, in cap heights: the wider of the two lines.</summary>
        public static float BlockWidthInCaps(string line1, string line2)
        {
            return Mathf.Max(LearnText.WidthInCaps(line1), LearnText.WidthInCaps(line2));
        }
    }
}
