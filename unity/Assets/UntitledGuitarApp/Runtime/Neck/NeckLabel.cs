// NeckLabel: one finger number on the neck (a bold world-space TextMesh in the built-in font).
//
// SEAM: this is the ONLY text code in Runtime/Neck, kept small on purpose. The HUD worker's HudText (Runtime/Hud, branch
// person1/sl0-spectator-hud) does the same job more thoroughly. At integration, replace "NeckLabel" with "HudText" in
// FloatingNeck.cs and delete this file: the five members used there have the same names and signatures
// (Create(parent, name), SetText, SetAngularHeight(capDegrees, distanceM), Color, Visible).
//
// SOURCES (Unity 2021.3.3f1, read offline under /Applications/Unity/Hub/Editor/2021.3.3f1/):
//  * Built-in font name: "Arial.ttf". Unity.app/Contents/Resources/PackageManager/BuiltInPackages/com.unity.ugui/Runtime/UI/
//    Core/Text.cs lines 593 and 599 (uGUI 1.0.0): font = Resources.GetBuiltinResource<Font>("Arial.ttf");
//    "LegacyRuntime.ttf" (the name in Unity 2022.2 and later) does not exist in this install; it is tried second in case the
//    project is ever upgraded. Resources.GetBuiltinResource has no page in the offline scripting reference.
//  * Documentation/en/ScriptReference/TextMesh.html: text, font, fontSize, fontStyle, characterSize, anchor, alignment, color.
//  * Documentation/en/Manual/class-TextMesh.html: when setting the font from a script, "set the component's font property and
//    also set the texture of the font material": the MeshRenderer gets font.material.
//  * Documentation/en/ScriptReference/TextMesh-characterSize.html says only "The size of each character (This scales the
//    whole text)": the docs give NO formula from fontSize / characterSize to world units. So the size is measured from
//    Renderer.localBounds (Documentation/en/ScriptReference/Renderer-localBounds.html) and the transform is scaled to fit.
//    UNVERIFIED until someone runs it in Unity: that localBounds is up to date right after TextMesh.text changes. If it is
//    not, the fallback constant below (one font pixel = 0.1 units at characterSize 1) is used; that constant is the usual
//    community figure, not a documented one.
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Neck
{
    [DisallowMultipleComponent]
    public sealed class NeckLabel : MonoBehaviour
    {
        /// <summary>Glasses pixels per degree (1080 px over about 27 degrees): the font is rasterised about 1:1 with the display.</summary>
        const float PixelsPerDegree = 40f;

        /// <summary>Arial: capital letters and digits are 1466/2048 em tall.</summary>
        const float CapHeightEm = 0.716f;

        /// <summary>UNVERIFIED fallback (see the file header): world units per font pixel at characterSize 1.</summary>
        const float FallbackUnitsPerFontPixel = 0.1f;

        static readonly string[] BuiltinFontNames = { "Arial.ttf", "LegacyRuntime.ttf" };
        static readonly string[] OsFontNames = { "Arial", "Helvetica Neue", "Helvetica" };
        static Font s_Font;
        static bool s_FontFailed;

        TextMesh _mesh;
        MeshRenderer _renderer;
        string _text = string.Empty;
        Color _color = Color.white;
        int _fontSize = 64;
        float _targetCapM;

        /// <summary>Builds a label on a new child of <paramref name="parent"/>. The text is read looking along its +z.</summary>
        public static NeckLabel Create(Transform parent, string objectName)
        {
            var go = new GameObject(string.IsNullOrEmpty(objectName) ? "NeckLabel" : objectName);
            if (parent != null) go.transform.SetParent(parent, false);
            NeckLabel label = go.AddComponent<NeckLabel>();
            label.Init();
            return label;
        }

        void Init()
        {
            _mesh = gameObject.AddComponent<TextMesh>();
            _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            Font font = BuiltinFont;
            if (font != null)
            {
                _mesh.font = font;
                _renderer.sharedMaterial = font.material;
            }
            _mesh.fontStyle = FontStyle.Bold;
            _mesh.richText = false;
            _mesh.characterSize = 1f;
            _mesh.anchor = TextAnchor.MiddleCenter;
            _mesh.alignment = TextAlignment.Center;
            _mesh.fontSize = _fontSize;
            _mesh.color = _color;
            _mesh.text = _text;
        }

        /// <summary>The engine's built-in font, loaded once and defensively; null (logged once) when nothing could be loaded.</summary>
        public static Font BuiltinFont
        {
            get
            {
                if (s_Font == null && !s_FontFailed)
                {
                    s_Font = LoadFont();
                    if (s_Font == null)
                    {
                        s_FontFailed = true;
                        UGALog.Error("neck_font", "no built-in font could be loaded; finger numbers will not show");
                    }
                }
                return s_Font;
            }
        }

        static Font LoadFont()
        {
            for (int i = 0; i < BuiltinFontNames.Length; i++)
            {
                try
                {
                    Font font = Resources.GetBuiltinResource<Font>(BuiltinFontNames[i]);
                    if (font != null) return font;
                }
                catch (Exception e)
                {
                    UGALog.Warn("neck_font", BuiltinFontNames[i] + " failed: " + e.GetType().Name);
                }
            }
            try
            {
                // Documentation/en/ScriptReference/Font.CreateDynamicFontFromOSFont.html (string[] fontnames, int size)
                return Font.CreateDynamicFontFromOSFont(OsFontNames, 64);
            }
            catch (Exception e)
            {
                UGALog.Warn("neck_font", "OS font fallback failed: " + e.GetType().Name);
                return null;
            }
        }

        /// <summary>Sets the words. The TextMesh is rebuilt only when they changed; returns true when it was.</summary>
        public bool SetText(string text)
        {
            if (text == null) text = string.Empty;
            if (string.Equals(_text, text, StringComparison.Ordinal)) return false;
            _text = text;
            _mesh.text = text;
            ApplyScale();
            return true;
        }

        /// <summary>Text colour; alpha fades it (use bright colours: black is invisible on the glasses).</summary>
        public Color Color
        {
            get { return _color; }
            set
            {
                if (value == _color) return;
                _color = value;
                _mesh.color = value;
            }
        }

        /// <summary>Shows or hides the text without touching its content.</summary>
        public bool Visible
        {
            get { return _renderer.enabled; }
            set { if (_renderer.enabled != value) _renderer.enabled = value; }
        }

        /// <summary>
        /// Sizes the text so capital letters and digits span <paramref name="capHeightDegrees"/> of view for a viewer
        /// <paramref name="distanceM"/> metres away, whatever the scale of its parents.
        /// </summary>
        public void SetAngularHeight(float capHeightDegrees, float distanceM)
        {
            float meters = NeckMath.SizeForAngle(capHeightDegrees, distanceM);
            if (!(meters > 0f)) return;
            _targetCapM = meters;
            int size = Mathf.Clamp(Mathf.RoundToInt(capHeightDegrees * PixelsPerDegree / CapHeightEm), 24, 256);
            if (size != _fontSize)
            {
                _fontSize = size;
                _mesh.fontSize = size;
            }
            ApplyScale();
        }

        void ApplyScale()
        {
            if (!(_targetCapM > 0f)) return;
            float capLocal = _fontSize * FallbackUnitsPerFontPixel * CapHeightEm;
            // Prefer the real size of the generated glyphs: a single digit or capital is exactly one cap height tall. Accept the
            // measurement only when it is plausible, so a stale or empty bounding box can never produce an absurd size.
            if (_text.Length == 1 && _renderer.enabled)
            {
                float measured = _renderer.localBounds.size.y;
                if (measured > capLocal * 0.5f && measured < capLocal * 2f) capLocal = measured;
            }
            float parentScale = 1f;
            Transform parent = transform.parent;
            if (parent != null)
            {
                float y = Mathf.Abs(parent.lossyScale.y);
                if (y > 1e-6f) parentScale = y;
            }
            float s = _targetCapM / (capLocal * parentScale);
            if (s > 0f && !float.IsInfinity(s)) transform.localScale = new Vector3(s, s, s);
        }
    }
}
