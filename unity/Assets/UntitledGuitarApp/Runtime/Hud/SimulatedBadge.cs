using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Hud
{
    /// <summary>
    /// The permanent "SIMULATED INPUT" badge: shown whenever the app is not listening to the real microphone
    /// (FlagStore.Current.audioSource is "wav"). This is an honesty rule of the project (docs/MASTER_PLAN.md section 18):
    /// nobody should ever mistake a replayed recording for a live guitar.
    ///
    /// It is deliberately not hideable. Its visibility is a function of the audioSource flag alone (see
    /// <see cref="ShouldShow"/>, which takes nothing else); there is no Hide method, and debugOverlay, spectator and
    /// largeText cannot affect it (largeText can only make it bigger). Do not parent it under anything that other code
    /// deactivates. Bright amber bold text, no plate, never flashing; it is part of the virtual layer, so the spectator
    /// view shows it too. Main thread only. Faces +Z: parent it to the head or eye rig, identity rotation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimulatedBadge : MonoBehaviour
    {
        public const string BadgeText = "SIMULATED INPUT";

        /// <summary>Cap height, degrees (above the 1.2 degree minimum so it is noticed), before the largeText multiplier.</summary>
        public const float Degrees = 1.8f;

        static readonly Color BadgeColor = new Color(1f, 0.757f, 0.027f, 1f);   // #FFC107

        HudText _text;
        float _distanceM = 1f;
        string _lastSource;
        bool _show;
        bool _large;

        /// <summary>Creates the badge under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static SimulatedBadge Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("SimulatedBadge");
            if (parent != null) go.transform.SetParent(parent, false);
            SimulatedBadge badge = go.AddComponent<SimulatedBadge>();
            badge.Init(viewingDistanceM);
            return badge;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : 1f;
            _text = HudText.Create(transform, "SimulatedText");
            _text.Color = BadgeColor;
            _text.SetText(BadgeText);

            _large = FlagStore.Current.largeText;
            ApplySize();

            // Evaluate the flag immediately, so the badge is right from the very first frame.
            _lastSource = FlagStore.Current.audioSource;
            _show = ShouldShow(_lastSource);
            _text.Visible = _show;
        }

        /// <summary>True while the badge is visible.</summary>
        public bool IsShowing { get { return _show; } }

        void Update()
        {
            FlagSet flags = FlagStore.Current;

            // Re-evaluate only when the flag string changed (a new instance), never from anything else.
            string source = flags.audioSource;
            if (!ReferenceEquals(source, _lastSource))
            {
                _lastSource = source;
                bool show = ShouldShow(source);
                if (show != _show)
                {
                    _show = show;
                    UGALog.Event("simulated_badge", "show", show, "audioSource", source ?? "null");
                }
            }

            // Enforced every frame, so nothing else can leave it hidden while the input is simulated.
            if (_text.Visible != _show) _text.Visible = _show;

            bool large = flags.largeText;
            if (large != _large)
            {
                _large = large;
                ApplySize();
            }
        }

        void ApplySize()
        {
            _text.SetAngularHeight(Degrees * HudText.TextScale(_large), _distanceM);
        }

        /// <summary>
        /// Whether the badge must be visible for this audioSource value. "wav" (any case, surrounding spaces ignored) shows
        /// it. So does any value the app does not recognise: if the flag file has a typo, the fail-safe is to disclose
        /// rather than to hide. Only a missing value or "mic" hides it. Pure; takes the audioSource and nothing else, on
        /// purpose.
        /// </summary>
        public static bool ShouldShow(string audioSource)
        {
            if (string.IsNullOrEmpty(audioSource)) return false;   // missing = the default, "mic"
            string source = audioSource.Trim();
            if (source.Length == 0) return false;
            return !string.Equals(source, "mic", StringComparison.OrdinalIgnoreCase);
        }
    }
}
