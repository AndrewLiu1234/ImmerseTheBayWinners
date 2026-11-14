// NeckLabel: one finger number on the neck (a bold world-space TextMesh in the built-in font).
//
// SEAM: this is the ONLY text code in Runtime/Neck, kept small on purpose. The HUD worker's HudText (Runtime/Hud) does the
// same job and has the same five members (Create(parent, name), SetText, SetAngularHeight(capDegrees, distanceM), Color,
// Visible), but do NOT swap it in yet: in the full-app frame of 14 Nov (docs/img/walking-skeleton-sbs.png) its text also
// comes out at about 0.8 of the asked height (the chord name "G", asked for 4 degrees by ChordLabel.CurrentDegrees,
// measures 3.28 including the G's overshoot). Swap only once HudText draws text at the size it is asked for.
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
//    whole text)": the docs give NO formula from fontSize / characterSize to world units. So the size comes from a
//    MEASUREMENT in Unity's own renders (DigitUnitsPerFontSize below), and the transform is scaled to fit.
//  * NOT used any more: Renderer.localBounds (Documentation/en/ScriptReference/Renderer-localBounds.html). In the 14 Nov
//    previews the old code got two sizes for the same request from it: before anything had been drawn it fell back to the
//    community figure (digits at 0.769 of the asked size); in the left-handed preview, made after three renders, it used
//    the bounds and the digits came out at 0.64. That bounds height matches Arial's line height (ascent + descent,
//    1.117 em), not its digit height (0.716 em).
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Neck
{
    [DisallowMultipleComponent]
    public sealed class NeckLabel : MonoBehaviour
    {
        /// <summary>Glasses pixels per degree (1080 px over about 27 degrees).</summary>
        const float PixelsPerDegree = 40f;

        /// <summary>
        /// MEASURED, not documented: how tall a digit is in the TextMesh's own units, per unit of fontSize, at characterSize 1.
        /// The usual community figure (0.1 unit per font pixel, times Arial's digit height of 0.716 em) gives 0.0716, but in
        /// the Unity renders of 14 Nov (build/previews/neck, 10:07, and docs/img/walking-skeleton-sbs.png) a "1" sized with
        /// that figure came out at 0.769 of the asked size: 14.50 mm instead of 18.85 mm, from its 50% edges, at its real
        /// distance from the eye (0.869 m). The three renders agree within half a percent. 0.0716 x 0.769 = 0.0551.
        /// </summary>
        public const float DigitUnitsPerFontSize = 0.0551f;

        /// <summary>A digit's height in font pixels per unit of fontSize, if one TextMesh unit is ten font pixels (the usual figure).</summary>
        const float DigitPixelsPerFontSize = DigitUnitsPerFontSize * 10f;

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
        /// Sizes the text so its digits span <paramref name="capHeightDegrees"/> of view for a viewer <paramref name="distanceM"/>
        /// metres away, whatever the scale of its parents. The same size every time: no measurement that changes once the text
        /// has been drawn (see the file header).
        /// </summary>
        public void SetAngularHeight(float capHeightDegrees, float distanceM)
        {
            float meters = NeckMath.SizeForAngle(capHeightDegrees, distanceM);
            if (!(meters > 0f)) return;
            _targetCapM = meters;
            // Raster the font so a digit has at least as many font pixels as it covers on the glasses (sharp, never blown up).
            int size = Mathf.Clamp(Mathf.RoundToInt(capHeightDegrees * PixelsPerDegree / DigitPixelsPerFontSize), 24, 256);
            if (size != _fontSize)
            {
                _fontSize = size;
                _mesh.fontSize = size;
            }
            ApplyScale();
        }

        /// <summary>The digit height in the TextMesh's own units at characterSize 1 and this font size (the measured constant).</summary>
        public static float DigitHeightUnits(int fontSize)
        {
            return fontSize * DigitUnitsPerFontSize;
        }

        void ApplyScale()
        {
            if (!(_targetCapM > 0f)) return;
            float capLocal = DigitHeightUnits(_fontSize);
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
