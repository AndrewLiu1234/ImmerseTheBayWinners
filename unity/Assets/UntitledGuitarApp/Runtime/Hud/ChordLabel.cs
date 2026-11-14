using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud
{
    /// <summary>
    /// The current chord large, with the next chord small underneath. Two <see cref="HudText"/> objects, bright and bold,
    /// no plate and no outline (black is invisible on the additive glasses). Each text is rebuilt only when its words change.
    ///
    /// The chord name is at least <see cref="HudText.ChordNameMinDegrees"/> (3 degrees) tall, the next chord at least
    /// <see cref="HudText.MinTextDegrees"/> (1.2 degrees); both grow by <see cref="HudText.LargeTextScale"/> while the
    /// largeText flag is on. The object's origin is the centre of the current chord; the next chord hangs below it.
    /// Main thread only. Faces +Z: parent it to the head or eye rig with identity rotation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChordLabel : MonoBehaviour
    {
        /// <summary>Cap height of the current chord, degrees (before the largeText multiplier).</summary>
        public const float CurrentDegrees = 4f;

        /// <summary>Cap height of the next chord, degrees (before the largeText multiplier).</summary>
        public const float NextDegrees = 1.8f;

        public const string NextPrefix = "next  ";

        static readonly Color CurrentColor = Color.white;
        static readonly Color NextColor = new Color(0.702f, 0.898f, 0.988f, 1f);   // #B3E5FC, the ghost-hand rim colour

        HudText _current;
        HudText _next;
        string _currentRaw = string.Empty;
        string _nextRaw = string.Empty;
        float _distanceM = 1f;
        bool _large;

        /// <summary>
        /// Creates the label under <paramref name="parent"/> (may be null). <paramref name="viewingDistanceM"/> is the distance
        /// from the eyes at which it will sit; it fixes the physical size so the angular size is right.
        /// </summary>
        public static ChordLabel Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("ChordLabel");
            if (parent != null) go.transform.SetParent(parent, false);
            ChordLabel label = go.AddComponent<ChordLabel>();
            label.Init(viewingDistanceM);
            return label;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : 1f;

            _current = HudText.Create(transform, "CurrentChord");
            _current.Color = CurrentColor;

            _next = HudText.Create(transform, "NextChord");
            _next.Color = NextColor;
            _next.Visible = false;

            _large = FlagStore.Current.largeText;
            ApplySizes();
        }

        /// <summary>The current chord as last set.</summary>
        public string Current { get { return _currentRaw; } }

        /// <summary>The next chord as last set (without the "next" prefix).</summary>
        public string Next { get { return _nextRaw; } }

        /// <summary>
        /// Shows <paramref name="current"/> large and <paramref name="next"/> small. Null or empty hides that part. A text is
        /// rebuilt only if its words changed, so calling this every frame is free.
        /// </summary>
        public void SetChords(string current, string next)
        {
            if (HudText.NeedsRebuild(_currentRaw, current))
            {
                _currentRaw = current ?? string.Empty;
                _current.SetText(_currentRaw);
            }

            if (HudText.NeedsRebuild(_nextRaw, next))
            {
                _nextRaw = next ?? string.Empty;
                _next.SetText(FormatNext(_nextRaw));
                _next.Visible = _nextRaw.Length > 0;
            }
        }

        /// <summary>Re-sizes for a new viewing distance (metres), e.g. after the label is moved from the neck card to a floating panel.</summary>
        public void SetViewingDistance(float meters)
        {
            if (!(meters > 0f)) return;
            _distanceM = meters;
            ApplySizes();
        }

        void Update()
        {
            bool large = FlagStore.Current.largeText;
            if (large == _large) return;
            _large = large;
            ApplySizes();
        }

        void ApplySizes()
        {
            float scale = HudText.TextScale(_large);
            _current.SetAngularHeight(Mathf.Max(CurrentDegrees * scale, HudText.ChordNameMinDegrees), _distanceM);
            _next.SetAngularHeight(Mathf.Max(NextDegrees * scale, HudText.MinTextDegrees), _distanceM);
            float offset = NextOffsetMeters(_current.CapHeightMeters, _next.CapHeightMeters);
            _next.transform.localPosition = new Vector3(0f, -offset, 0f);
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>The words shown for the next chord: "next  C", or an empty string when there is none.</summary>
        public static string FormatNext(string next)
        {
            return string.IsNullOrEmpty(next) ? string.Empty : NextPrefix + next;
        }

        /// <summary>
        /// How far below the current chord's centre the next chord's centre sits, from the two cap heights: half a text line
        /// (about 1.6 cap heights tall) of each, plus a gap of a tenth of the big one. Tune on the glasses if it looks cramped.
        /// </summary>
        public static float NextOffsetMeters(float capCurrentM, float capNextM)
        {
            return 0.8f * (capCurrentM + capNextM) + 0.1f * capCurrentM;
        }
    }
}
