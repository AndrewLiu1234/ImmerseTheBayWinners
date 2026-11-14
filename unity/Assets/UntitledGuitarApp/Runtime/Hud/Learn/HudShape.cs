using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// One drawn shape of the Learn HUD (a check, a cross, an icon, a part of the tuner dial): a shared mesh, the shared
    /// material, and its own colour and brightness through a property block (no material copies, no allocation).
    /// Create it with <see cref="Create"/>. Like HudText it faces +Z. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudShape : MonoBehaviour
    {
        MeshFilter _filter;
        MeshRenderer _renderer;
        LearnGlyph _glyph;
        bool _hasGlyph;
        Color _color = Color.white;
        float _opacity = 1f;
        float _size = 1f;

        /// <summary>Builds a shape on a new child of <paramref name="parent"/> (may be null). No Inspector wiring needed.</summary>
        public static HudShape Create(Transform parent, string objectName, LearnGlyph glyph)
        {
            var go = new GameObject(string.IsNullOrEmpty(objectName) ? "HudShape" : objectName);
            if (parent != null) go.transform.SetParent(parent, false);
            HudShape shape = go.AddComponent<HudShape>();
            shape.Init();
            shape.Glyph = glyph;
            return shape;
        }

        void Init()
        {
            _filter = gameObject.AddComponent<MeshFilter>();
            _renderer = gameObject.AddComponent<MeshRenderer>();
            LearnGraphics.ConfigureRenderer(_renderer);
            Material material = LearnGraphics.SharedMaterial;
            if (material != null) _renderer.sharedMaterial = material;
            else _renderer.enabled = false;
            ApplyTint();
        }

        /// <summary>Which drawing this shape shows. Swapping it swaps a shared mesh; nothing is rebuilt.</summary>
        public LearnGlyph Glyph
        {
            get { return _glyph; }
            set
            {
                if (_hasGlyph && value == _glyph) return;
                _glyph = value;
                _hasGlyph = true;
                _filter.sharedMesh = LearnGraphics.GetMesh(value);
            }
        }

        /// <summary>The shape's colour. Use bright colours: black is invisible on the glasses.</summary>
        public Color Color
        {
            get { return _color; }
            set
            {
                if (value == _color) return;
                _color = value;
                ApplyTint();
            }
        }

        /// <summary>Brightness 0..1 (multiplies the colour; 0 is invisible on an additive display).</summary>
        public float Opacity
        {
            get { return _opacity; }
            set
            {
                float v = LearnTiming.Clamp01(value);
                if (v == _opacity) return;
                _opacity = v;
                ApplyTint();
            }
        }

        /// <summary>Shows or hides the shape without changing anything else.</summary>
        public bool Visible
        {
            get { return _renderer.enabled; }
            set
            {
                if (value && LearnGraphics.SharedMaterial == null) return;   // nothing to draw with
                if (_renderer.enabled != value) _renderer.enabled = value;
            }
        }

        /// <summary>Draw order among overlapping HUD elements (higher = on top). Only matters with the fallback shader.</summary>
        public int SortingOrder
        {
            get { return _renderer.sortingOrder; }
            set { if (_renderer.sortingOrder != value) _renderer.sortingOrder = value; }
        }

        /// <summary>The size of the shape's 1 x 1 box (or of one dial unit) in metres.</summary>
        public float Size { get { return _size; } }

        /// <summary>Sets the size of the shape's 1 x 1 box (or of one dial unit), in metres of the parent's space.</summary>
        public void SetSize(float meters)
        {
            if (!(meters > 0f) || float.IsInfinity(meters) || meters == _size) return;
            _size = meters;
            transform.localScale = new Vector3(meters, meters, meters);
        }

        void ApplyTint()
        {
            LearnGraphics.SetTint(_renderer, new Color(_color.r, _color.g, _color.b, _color.a * _opacity));
        }
    }
}
