using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// What every Learn HUD element shares: it is built from code (by its Create method, or lazily after a bare
    /// AddComponent), it is sized in degrees of view for a viewing distance, it follows the largeText flag, and it
    /// advances through <see cref="Tick"/> (called from Update with unscaled time; previews and tests can call it
    /// directly, so no wall-clock waiting is ever needed). Faces +Z like HudText. Main thread only.
    /// </summary>
    public abstract class LearnWidget : MonoBehaviour
    {
        /// <summary>Eye-to-neck distance assumed until a real one is given: a seated player's eyes are about half a metre from fret 5.</summary>
        public const float DefaultViewingDistanceM = 0.5f;

        float _distanceM = DefaultViewingDistanceM;
        bool _large;
        bool _built;

        /// <summary>The distance from the eyes this element is sized for, metres.</summary>
        public float ViewingDistanceM { get { return _distanceM; } }

        protected static T CreateWidget<T>(Transform parent, string objectName, float viewingDistanceM) where T : LearnWidget
        {
            var go = new GameObject(objectName);
            if (parent != null) go.transform.SetParent(parent, false);
            T widget = go.AddComponent<T>();
            if (viewingDistanceM > 0f && !float.IsInfinity(viewingDistanceM)) widget._distanceM = viewingDistanceM;
            widget.EnsureBuilt();
            return widget;
        }

        /// <summary>Builds the element's children once. Every public method calls this first, so a bare AddComponent works too.</summary>
        protected void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            _large = FlagStore.Current.largeText;
            Build();
            ApplySizes();
        }

        /// <summary>
        /// Re-sizes for a new viewing distance (metres), keeping every angular size. Call it when the element moves nearer
        /// or farther, not every frame: it re-measures the texts.
        /// </summary>
        public void SetViewingDistance(float meters)
        {
            if (!(meters > 0f) || float.IsInfinity(meters)) return;
            EnsureBuilt();
            if (meters == _distanceM) return;
            _distanceM = meters;
            ApplySizes();
        }

        /// <summary>Applies every size again (for a preview, after the first render has filled the font texture).</summary>
        public void RefreshSizes()
        {
            EnsureBuilt();
            ApplySizes();
        }

        /// <summary>Advances the element by <paramref name="dt"/> seconds. Update calls it; previews and tests may call it directly.</summary>
        public void Tick(float dt)
        {
            EnsureBuilt();
            bool large = FlagStore.Current.largeText;
            if (large != _large)
            {
                _large = large;
                ApplySizes();
            }
            if (!(dt > 0f)) dt = 0f;
            OnTick(dt);
        }

        void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>1 normally, 1.35 with the largeText flag: every size of the element is multiplied by it.</summary>
        protected float SizeScale { get { return HudText.TextScale(_large); } }

        /// <summary>Metres that span <paramref name="degrees"/> of view at the viewing distance, including the largeText multiplier.</summary>
        protected float Meters(float degrees)
        {
            return HudText.SizeForAngularHeight(degrees * SizeScale, _distanceM);
        }

        /// <summary>Sizes a text to <paramref name="capDegrees"/> of cap height (times the largeText multiplier) and returns that cap height in metres.</summary>
        protected float SizeText(LearnLabel label, float capDegrees)
        {
            label.Text.SetAngularHeight(capDegrees * SizeScale, _distanceM);
            return label.Text.CapHeightMeters;
        }

        /// <summary>Creates the child objects. Called once.</summary>
        protected abstract void Build();

        /// <summary>Applies all sizes and positions for the current viewing distance and text scale.</summary>
        protected abstract void ApplySizes();

        /// <summary>One animation step; <paramref name="dt"/> is never negative.</summary>
        protected abstract void OnTick(float dt);
    }
}
