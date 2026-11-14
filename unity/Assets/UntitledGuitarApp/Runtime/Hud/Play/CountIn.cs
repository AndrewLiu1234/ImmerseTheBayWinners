using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// The visual count-in before a song: 4, 3, 2, 1, one large number per beat (docs/MASTER_PLAN.md section 16: "a one-bar
    /// visual count-in"). Call <see cref="Show"/> with the beats that are left; 0 or less hides it and the song starts.
    /// Each new number eases in from slightly larger (a swell, not a flash); the number itself is the cue, so no colour is
    /// involved. The text is rebuilt only when the number changes.
    ///
    /// On the neck card it takes the place of the "next chord" block, beside the first chord of the song, so it never covers
    /// the fretting ghost hand while the player is finding the first shape. Used alone, the object's origin is the centre of
    /// the number. Main thread only. Faces +Z.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CountIn : MonoBehaviour
    {
        /// <summary>A new number settles to its size over this long.</summary>
        public const float PopSeconds = 0.2f;

        /// <summary>A new number starts this much larger.</summary>
        public const float PopGain = 0.28f;

        /// <summary>Numbers above this are shown as this (a count-in is one bar).</summary>
        public const int MaxCount = 16;

        static readonly Color NumberColor = Color.white;
        static string[] s_Numbers;

        Transform _pivot;
        HudText _number;
        float _distanceM = PlayHudLayout.DesignDistanceM;
        bool _large;
        int _beatsLeft;
        float _sinceChange;
        bool _popping;

        /// <summary>The beats left as last shown; 0 when hidden.</summary>
        public int BeatsLeft { get { return _beatsLeft; } }

        /// <summary>True while a number is on screen.</summary>
        public bool IsShowing { get { return _beatsLeft > 0; } }

        /// <summary>Creates the count-in under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static CountIn Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("CountIn");
            if (parent != null) go.transform.SetParent(parent, false);
            CountIn countIn = go.AddComponent<CountIn>();
            countIn.Init(viewingDistanceM);
            return countIn;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : PlayHudLayout.DesignDistanceM;
            var pivot = new GameObject("Pivot");
            pivot.transform.SetParent(transform, false);
            _pivot = pivot.transform;
            _number = HudText.Create(_pivot, "Number");
            _number.Color = NumberColor;
            _number.Visible = false;
            _large = FlagStore.Current.largeText;
            ApplySize();
            enabled = false;   // Update only runs while a number is settling
        }

        /// <summary>
        /// Shows the number of beats left before the song starts (4, 3, 2, 1). 0 or less hides the count-in. Call it every
        /// frame or only when the number changes; the text is rebuilt only when it changes.
        /// </summary>
        public void Show(int beatsLeft)
        {
            int n = Mathf.Clamp(beatsLeft, 0, MaxCount);

            bool large = FlagStore.Current.largeText;
            if (large != _large)
            {
                _large = large;
                ApplySize();
            }

            if (n == _beatsLeft) return;
            _beatsLeft = n;
            if (n <= 0)
            {
                _number.Visible = false;
                _popping = false;
                _pivot.localScale = Vector3.one;
                enabled = false;
                return;
            }

            _number.SetText(TextFor(n));
            _number.Visible = true;
            _sinceChange = 0f;
            _popping = true;
            ApplyPop();
            enabled = true;
        }

        /// <summary>Hides the count-in (same as Show(0)).</summary>
        public void Hide()
        {
            Show(0);
        }

        void Update()
        {
            Advance(Time.unscaledDeltaTime);
        }

        /// <summary>Advances the settle animation by <paramref name="deltaSeconds"/>. Update calls it; Editor code may call it directly.</summary>
        public void Advance(float deltaSeconds)
        {
            if (!_popping) return;
            if (deltaSeconds > 0f) _sinceChange += deltaSeconds;
            ApplyPop();
            if (_sinceChange >= PopSeconds)
            {
                _popping = false;
                enabled = false;
            }
        }

        void ApplyPop()
        {
            float s = PopScale(_sinceChange, PopSeconds, PopGain);
            _pivot.localScale = new Vector3(s, s, 1f);
        }

        void ApplySize()
        {
            // Size the text while the pivot is at rest, so the swell is not baked into it.
            Vector3 saved = _pivot.localScale;
            _pivot.localScale = Vector3.one;
            float s = transform.lossyScale.y;
            float world = _distanceM * (s > 1e-6f ? s : 1f);
            _number.SetAngularHeight(PlayHudLayout.TextDeg(PlayHudLayout.CountInCapDeg, _large), world);
            _pivot.localScale = saved;
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>The text for a count: "4", "3", "2", "1"; empty for 0 or less. Cached, so showing a number allocates nothing.</summary>
        public static string TextFor(int beatsLeft)
        {
            if (beatsLeft <= 0) return string.Empty;
            if (s_Numbers == null)
            {
                s_Numbers = new string[MaxCount + 1];
                for (int i = 0; i <= MaxCount; i++) s_Numbers[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return s_Numbers[Mathf.Min(beatsLeft, MaxCount)];
        }

        /// <summary>
        /// Scale of a number <paramref name="secondsSinceChange"/> after it appeared: 1 + gain at first, easing out to
        /// exactly 1 at <paramref name="popSeconds"/> and staying there.
        /// </summary>
        public static float PopScale(float secondsSinceChange, float popSeconds, float gain)
        {
            if (!(popSeconds > 0f) || float.IsNaN(secondsSinceChange) || secondsSinceChange >= popSeconds) return 1f;
            float t = 1f - Mathf.Max(0f, secondsSinceChange) / popSeconds;
            return 1f + gain * t * t;
        }
    }
}
