using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// A <see cref="HudText"/> that can fade without being rebuilt. HudText rebuilds its mesh whenever its colour
    /// changes, so brightness is changed here through a property block on the text's renderer instead (the font
    /// shader multiplies by _Color; see LearnGraphics.cs for the evidence). The words are rebuilt only when they change.
    /// A plain helper object, not a component. Main thread only.
    /// </summary>
    public sealed class LearnLabel
    {
        readonly HudText _text;
        readonly MeshRenderer _renderer;
        float _opacity = 1f;

        LearnLabel(HudText text)
        {
            _text = text;
            _renderer = text.GetComponent<MeshRenderer>();
        }

        /// <summary>Creates a text under <paramref name="parent"/> with the given anchor (alignment follows the anchor's side).</summary>
        public static LearnLabel Create(Transform parent, string objectName, TextAnchor anchor, Color color)
        {
            HudText text = HudText.Create(parent, objectName);
            text.Anchor = anchor;
            bool left = anchor == TextAnchor.UpperLeft || anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.LowerLeft;
            bool right = anchor == TextAnchor.UpperRight || anchor == TextAnchor.MiddleRight || anchor == TextAnchor.LowerRight;
            text.Alignment = left ? TextAlignment.Left : (right ? TextAlignment.Right : TextAlignment.Center);
            text.Color = color;
            return new LearnLabel(text);
        }

        /// <summary>The underlying text (size, colour, anchor).</summary>
        public HudText Text { get { return _text; } }

        public Transform Transform { get { return _text.transform; } }

        /// <summary>Sets the words; the mesh is rebuilt only when they changed.</summary>
        public void SetText(string words)
        {
            _text.SetText(words);
        }

        /// <summary>Brightness 0..1. Does not rebuild the text. At 0 the renderer is switched off.</summary>
        public float Opacity
        {
            get { return _opacity; }
            set
            {
                float v = LearnTiming.Clamp01(value);
                if (v == _opacity) return;
                _opacity = v;
                LearnGraphics.SetTint(_renderer, new Color(1f, 1f, 1f, v));
                bool visible = v > 0f;
                if (_text.Visible != visible) _text.Visible = visible;
            }
        }

        /// <summary>Hides the text now (brightness 0, renderer off).</summary>
        public void HideNow()
        {
            _opacity = 0f;
            LearnGraphics.SetTint(_renderer, new Color(1f, 1f, 1f, 0f));
            _text.Visible = false;
        }
    }
}
