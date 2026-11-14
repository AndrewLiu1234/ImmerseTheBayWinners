using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// The verdict on a strum: a big green check and "Clean!", or the single top issue with the whole chord ("Heard C,
    /// target is G" / "Don't strum string 6") with an icon before it. One message, never a list.
    ///
    /// It is on screen on the frame after <see cref="Show(bool, string)"/> (already half bright, fully bright 0.12 s
    /// later), which leaves the strum-to-verdict budget of 350 ms to the listening. It then stays long enough to read
    /// (1.3 s for "Clean!", a second plus 0.3 s per word for an issue) and fades by itself. Every call is a new strum: a
    /// second clean strum while the first verdict still shows is acknowledged by one gentle swell, not by a flash, and
    /// nothing changes more than 3 times a second. Faces +Z; the Learn HUD puts it on the neck card spot. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StrumVerdict : LearnMessage
    {
        public const string CleanText = "Clean!";

        /// <summary>Shown when a strum was not clean but no issue text was given: still kind, still specific about what to do.</summary>
        public const string DefaultIssueText = "Almost: strum again";

        /// <summary>Size of the check for a clean strum, degrees (before the largeText multiplier, like every size here).</summary>
        public const float CleanMarkDegrees = 3.6f;

        /// <summary>Cap height of "Clean!", degrees.</summary>
        public const float CleanTextDegrees = 2.6f;

        public const float IssueIconDegrees = 2.4f;

        /// <summary>Cap height of an issue's words, degrees.</summary>
        public const float IssueTextDegrees = 1.4f;

        const float CleanGapDegrees = 0.8f;
        const float IssueGapDegrees = 0.6f;
        const float LineStepInCaps = 1.8f;

        HudShape _mark;
        LearnLabel _cleanLabel;
        LearnLabel _line1;
        LearnLabel _line2;

        bool _clean;
        string _issue = string.Empty;
        TipIcon _icon = TipIcon.Mismatch;
        bool _shownClean;
        string _shown1 = string.Empty;
        string _shown2 = string.Empty;

        /// <summary>True when the verdict last shown was a clean strum.</summary>
        public bool LastWasClean { get { return _clean; } }

        /// <summary>The issue text last shown (empty for a clean strum).</summary>
        public string LastIssue { get { return _issue; } }

        /// <summary>Creates the verdict under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static StrumVerdict Create(Transform parent, float viewingDistanceM)
        {
            return CreateWidget<StrumVerdict>(parent, "StrumVerdict", viewingDistanceM);
        }

        protected override void BuildContent()
        {
            _mark = HudShape.Create(Content, "Mark", LearnGlyph.Check);
            _mark.SortingOrder = 1;
            _cleanLabel = LearnLabel.Create(Content, "Clean", TextAnchor.MiddleLeft, LearnPalette.Good);
            _cleanLabel.SetText(CleanText);
            _line1 = LearnLabel.Create(Content, "Issue1", TextAnchor.MiddleLeft, LearnPalette.Text);
            _line2 = LearnLabel.Create(Content, "Issue2", TextAnchor.MiddleLeft, LearnPalette.Text);
        }

        /// <summary>
        /// Shows the verdict on one strum: <paramref name="clean"/> = the big check and "Clean!"; otherwise
        /// <paramref name="issue"/>, the single top chord-level issue in 7 words or fewer (ignored when clean).
        /// </summary>
        public void Show(bool clean, string issue)
        {
            Show(clean, issue, clean ? TipIcon.Good : TipIcon.Mismatch);
        }

        /// <summary>As <see cref="Show(bool, string)"/>, choosing the icon drawn before the issue (for example Skip for "Don't strum string 6").</summary>
        public void Show(bool clean, string issue, TipIcon icon)
        {
            EnsureBuilt();
            _clean = clean;
            _issue = clean ? string.Empty : NormalizeIssue(issue);
            _icon = clean ? TipIcon.Good : (icon == TipIcon.None || icon == TipIcon.Good ? TipIcon.Mismatch : icon);
            RequestShow(HoldSeconds(clean, _issue));
        }

        /// <summary>A clean strum.</summary>
        public void ShowClean()
        {
            Show(true, null, TipIcon.Good);
        }

        protected override void ApplyContent()
        {
            _shownClean = _clean;
            if (_clean)
            {
                _shown1 = string.Empty;
                _shown2 = string.Empty;
                _mark.Glyph = LearnGlyph.Check;
                _mark.Color = LearnPalette.Good;
            }
            else
            {
                LearnText.WrapTwoLines(_issue, out _shown1, out _shown2);
                _line1.SetText(_shown1);
                _line2.SetText(_shown2);
                _mark.Glyph = TipIconStyle.Glyph(_icon);
                _mark.Color = TipIconStyle.Color(_icon);
            }
            Layout();
        }

        protected override void ApplySizes()
        {
            SizeText(_cleanLabel, CleanTextDegrees);
            SizeText(_line1, IssueTextDegrees);
            SizeText(_line2, IssueTextDegrees);
            Layout();
        }

        void Layout()
        {
            if (_shownClean)
            {
                float mark = Meters(CleanMarkDegrees);
                float gap = Meters(CleanGapDegrees);
                float text = LearnText.WidthInCaps(CleanText) * _cleanLabel.Text.CapHeightMeters;
                float left = -0.5f * (mark + gap + text);
                _mark.SetSize(mark);
                _mark.transform.localPosition = new Vector3(left + 0.5f * mark, 0f, 0f);
                _cleanLabel.Transform.localPosition = new Vector3(left + mark + gap, 0f, 0f);
            }
            else
            {
                float cap = _line1.Text.CapHeightMeters;
                float icon = Meters(IssueIconDegrees);
                float gap = Meters(IssueGapDegrees);
                float text = TipLine.BlockWidthInCaps(_shown1, _shown2) * cap;
                float left = -0.5f * (icon + gap + text);
                float textX = left + icon + gap;
                float half = _shown2.Length > 0 ? 0.5f * LineStepInCaps * cap : 0f;
                _mark.SetSize(icon);
                _mark.transform.localPosition = new Vector3(left + 0.5f * icon, 0f, 0f);
                _line1.Transform.localPosition = new Vector3(textX, half, 0f);
                _line2.Transform.localPosition = new Vector3(textX, -half, 0f);
            }
        }

        protected override void ApplyVisual(float opacity)
        {
            _mark.Opacity = opacity;
            _mark.Visible = opacity > 0f;
            _cleanLabel.Opacity = _shownClean ? opacity : 0f;
            _line1.Opacity = _shownClean ? 0f : opacity;
            _line2.Opacity = !_shownClean && _shown2.Length > 0 ? opacity : 0f;
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>The issue text to show: trimmed, or <see cref="DefaultIssueText"/> when none was given.</summary>
        public static string NormalizeIssue(string issue)
        {
            if (issue == null) return DefaultIssueText;
            string t = issue.Trim();
            return t.Length == 0 ? DefaultIssueText : t;
        }

        /// <summary>How long a verdict stays before it fades: the reading time of "Clean!" (one word) or of the issue's words.</summary>
        public static float HoldSeconds(bool clean, string issue)
        {
            return LearnTiming.ReadSeconds(clean ? 1 : LearnText.CountWords(NormalizeIssue(issue)));
        }
    }
}
