using System.Globalization;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// The card at the end of a play-along run: how many chord changes were clean, how the timing was, and one encouraging
    /// line. Head-locked and centred (parent it under the head, e.g. StereoRig.Head, about a metre ahead): the player has
    /// stopped playing and looked up.
    ///
    /// Clean and not-clean changes are a row of marks that differ in SHAPE (a filled dot for clean, a small ring for the
    /// rest), so the score reads without colour. Bright text on nothing: no plate, no frame. Everything is built once in
    /// <see cref="Show"/> and then left alone; nothing runs per frame.
    ///
    /// The numbers and the encouraging line come from Core (scoring, tips). If no line is given, a plain one is chosen from
    /// the score (<see cref="DefaultEncouragement"/>); the wording belongs to the music-teacher agent and may be replaced.
    /// The object's origin is the centre of the card. Main thread only. Faces +Z.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ResultsCard : MonoBehaviour
    {
        /// <summary>Most marks in the row; longer songs are scaled down to this many.</summary>
        public const int MaxMarks = 16;

        /// <summary>Timing within this many milliseconds of the beat counts as on the beat.</summary>
        public const float OnBeatMs = 60f;

        /// <summary>Beyond this many milliseconds the word "a little" is dropped.</summary>
        public const float ClearlyOffMs = 150f;

        public const float TitleRowDeg = 5.6f;
        public const float TitleCapDeg = 1.5f;
        public const float ScoreRowDeg = 2.2f;
        public const float ScoreCapDeg = 3.2f;
        public const float LabelRowDeg = -0.9f;
        public const float LabelCapDeg = 1.4f;
        public const float MarksRowDeg = -3.1f;
        public const float MarkPitchDeg = 1.25f;
        public const float MarkDeg = 0.8f;
        public const float TimingRowDeg = -5.3f;
        public const float TimingCapDeg = 1.4f;
        public const float LineRowDeg = -8.2f;
        public const float LineCapDeg = 1.7f;

        /// <summary>Lines are shrunk (never below the minimum text size) to fit this width.</summary>
        public const float MaxWidthDeg = 36f;

        public const string DefaultTitle = "song complete";
        public const string HitsLabel = "chord changes clean";

        static readonly Color StrongColor = Color.white;
        static readonly Color InfoColor = new Color(0.702f, 0.898f, 0.988f, 1f);      // #B3E5FC

        readonly HudShape[] _marks = new HudShape[MaxMarks];
        HudText _title;
        HudText _score;
        HudText _label;
        HudText _timing;
        HudText _line;
        float _distanceM = 1f;
        bool _showing;

        /// <summary>The heading. Set it before <see cref="Show"/> (for example to the song's title).</summary>
        public string Title = DefaultTitle;

        /// <summary>True while the card is visible.</summary>
        public bool IsShowing { get { return _showing; } }

        /// <summary>Creates the card under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away. Hidden until <see cref="Show"/>.</summary>
        public static ResultsCard Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("ResultsCard");
            if (parent != null) go.transform.SetParent(parent, false);
            ResultsCard card = go.AddComponent<ResultsCard>();
            card.Init(viewingDistanceM);
            return card;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : 1f;
            _title = MakeText("Title", InfoColor);
            _score = MakeText("Score", StrongColor);
            _label = MakeText("Label", InfoColor);
            _timing = MakeText("Timing", InfoColor);
            _line = MakeText("Encouragement", StrongColor);
            for (int i = 0; i < MaxMarks; i++)
            {
                _marks[i] = HudShape.Create(transform, "Mark" + i, HudShape.Disc);
                _marks[i].Visible = false;
            }
            enabled = false;   // nothing to do per frame
        }

        HudText MakeText(string objectName, Color color)
        {
            HudText text = HudText.Create(transform, objectName);
            text.Color = color;
            text.Visible = false;
            return text;
        }

        /// <summary>
        /// Shows the results of a run. <paramref name="chordsHit"/> of <paramref name="chordsTotal"/> chord changes were
        /// clean. <paramref name="meanTimingMs"/> is the average distance of the strums from the beat in milliseconds,
        /// positive = late, negative = early, NaN = not measured (the timing line is then left out).
        /// <paramref name="encouragement"/> is one short line (7 words or fewer); null or empty picks a default.
        /// </summary>
        public void Show(int chordsHit, int chordsTotal, float meanTimingMs, string encouragement)
        {
            bool large = FlagStore.Current.largeText;
            float scale = HudText.TextScale(large);
            float d = _distanceM;
            float s = transform.lossyScale.y;
            float world = d * (s > 1e-6f ? s : 1f);

            int total = Mathf.Max(0, chordsTotal);
            int hit = Mathf.Clamp(chordsHit, 0, total);

            Place(_title, string.IsNullOrEmpty(Title) ? DefaultTitle : Title, TitleRowDeg, TitleCapDeg, scale, d, world);
            Place(_score, ScoreText(hit, total), ScoreRowDeg, ScoreCapDeg, scale, d, world);
            Place(_label, total > 0 ? HitsLabel : string.Empty, LabelRowDeg, LabelCapDeg, scale, d, world);
            Place(_timing, TimingText(meanTimingMs), TimingRowDeg, TimingCapDeg, scale, d, world);
            Place(_line, string.IsNullOrEmpty(encouragement) ? DefaultEncouragement(hit, total) : encouragement,
                LineRowDeg, LineCapDeg, scale, d, world);

            // Marks: clean changes first (filled dots), then the others (small rings).
            int marks = MarkCount(total);
            int filled = FilledMarks(hit, total);
            float pitch = PlayHudLayout.Size(MarkPitchDeg * scale, d);
            float size = PlayHudLayout.Size(MarkDeg * scale, d);
            float y = PlayHudLayout.Offset(MarksRowDeg * scale, d);
            float x0 = -0.5f * (marks - 1) * pitch;
            for (int i = 0; i < MaxMarks; i++)
            {
                HudShape mark = _marks[i];
                bool used = i < marks;
                mark.Visible = used;
                if (!used) continue;
                bool clean = i < filled;
                mark.Mesh = clean ? HudShape.Disc : HudShape.Ring;
                mark.SetPosition(x0 + i * pitch, y);
                mark.SetSize(clean ? size : size * 0.85f);
                mark.SetColor(clean ? StrongColor : InfoColor, clean ? 1f : 0.75f);
            }

            _showing = true;
        }

        /// <summary>Hides the card.</summary>
        public void Hide()
        {
            _showing = false;
            _title.Visible = false;
            _score.Visible = false;
            _label.Visible = false;
            _timing.Visible = false;
            _line.Visible = false;
            for (int i = 0; i < MaxMarks; i++) _marks[i].Visible = false;
        }

        static void Place(HudText text, string words, float rowDeg, float capDeg, float scale, float d, float world)
        {
            bool show = !string.IsNullOrEmpty(words);
            text.Visible = show;
            if (!show) return;
            text.SetText(words);
            float fitted = PlayHudLayout.FitCapDegrees(HudTextMetrics.WidthEm(words), capDeg * scale, MaxWidthDeg,
                HudText.MinTextDegrees, HudTextMetrics.CapPerEm);
            text.transform.localPosition = new Vector3(0f, PlayHudLayout.Offset(rowDeg * scale, d), 0f);
            text.SetAngularHeight(fitted, world);
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>"7 of 8"; "done" when nothing was scored.</summary>
        public static string ScoreText(int chordsHit, int chordsTotal)
        {
            if (chordsTotal <= 0) return "done";
            int hit = Mathf.Clamp(chordsHit, 0, chordsTotal);
            return hit.ToString(CultureInfo.InvariantCulture) + " of " + chordsTotal.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Share of clean changes, 0..1 (0 when nothing was scored).</summary>
        public static float HitFraction(int chordsHit, int chordsTotal)
        {
            if (chordsTotal <= 0) return 0f;
            return Mathf.Clamp01(chordsHit / (float)chordsTotal);
        }

        /// <summary>How many marks are drawn for a song with <paramref name="chordsTotal"/> changes (at most <see cref="MaxMarks"/>).</summary>
        public static int MarkCount(int chordsTotal)
        {
            return Mathf.Clamp(chordsTotal, 0, MaxMarks);
        }

        /// <summary>
        /// How many of the marks are filled. Exact for songs of up to <see cref="MaxMarks"/> changes; for longer songs the
        /// share is kept, but a run that was not perfect never shows all marks filled, and one with any clean change shows
        /// at least one.
        /// </summary>
        public static int FilledMarks(int chordsHit, int chordsTotal)
        {
            int marks = MarkCount(chordsTotal);
            if (marks == 0) return 0;
            int hit = Mathf.Clamp(chordsHit, 0, chordsTotal);
            if (chordsTotal <= MaxMarks) return hit;
            if (hit == chordsTotal) return marks;
            if (hit == 0) return 0;
            int filled = Mathf.RoundToInt(marks * (hit / (float)chordsTotal));
            return Mathf.Clamp(filled, 1, marks - 1);
        }

        /// <summary>
        /// The timing line in plain words, with the number for the curious: "timing: on the beat",
        /// "timing: a little late (85 ms)", "timing: early (210 ms)". Empty for NaN (not measured).
        /// </summary>
        public static string TimingText(float meanTimingMs)
        {
            if (float.IsNaN(meanTimingMs) || float.IsInfinity(meanTimingMs)) return string.Empty;
            float abs = Mathf.Abs(meanTimingMs);
            if (abs <= OnBeatMs) return "timing: on the beat";
            string direction = meanTimingMs > 0f ? "late" : "early";
            string ms = Mathf.RoundToInt(abs).ToString(CultureInfo.InvariantCulture);
            return "timing: " + (abs <= ClearlyOffMs ? "a little " : string.Empty) + direction + " (" + ms + " ms)";
        }

        /// <summary>
        /// One encouraging line chosen from the score when Core gives none. Always kind, always 7 words or fewer, and it
        /// names the next step rather than the mistakes.
        /// </summary>
        public static string DefaultEncouragement(int chordsHit, int chordsTotal)
        {
            if (chordsTotal <= 0) return "You played it through. Nice.";
            float fraction = HitFraction(chordsHit, chordsTotal);
            if (fraction >= 0.9f) return "Clean changes. Try a faster tempo.";
            if (fraction >= 0.6f) return "Good run. Play it once more.";
            return "You kept going. That is the skill.";
        }
    }
}
