// HudText: world-space HUD text for the additive glasses display, built only from code.
//
// Technique: the legacy built-in TextMesh component plus the engine's built-in dynamic font. No shader, material asset or
// Resources entry is needed. The font's own material draws glyphs with an alpha blend; on an additive display black is
// invisible, so bright text over the black clear colour is exactly what we want (no plates, no outlines).
//
// SOURCES (read offline on this machine; Unity 2021.3.3f1 = /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents):
//  * Built-in font name. The valid name in this version is "Arial.ttf":
//      - Resources/PackageManager/BuiltInPackages/com.unity.ugui/Runtime/UI/Core/Text.cs, lines 593 and 599 (uGUI 1.0.0, shipped
//        with this editor):   font = Resources.GetBuiltinResource<Font>("Arial.ttf");
//      - PlaybackEngines/MacStandaloneSupport/Variations/macos_arm64_player_nondevelopment_mono/UnityPlayer.app/Contents/
//        Frameworks/UnityPlayer.dylib holds the built-in resource name "Arial.ttf" (next to "Font.shader", "Sphere.fbx"),
//        so a standalone build knows it too.
//      - "LegacyRuntime.ttf" is NOT FOUND anywhere in this install (editor binary, player runtime, default-resource bundles,
//        offline docs). It is only tried second, in case the project is ever upgraded to a Unity that renamed the font.
//      - Resources.GetBuiltinResource itself is not in the offline scripting reference; the Text.cs lines are the evidence.
//  * Documentation/en/Manual/class-TextMesh.html: "set the component's font property and also set the texture of the font
//    material", so the MeshRenderer gets font.material.
//  * Documentation/en/ScriptReference/TextMesh-characterSize.html says only "The size of each character (This scales the whole
//    text)". There is NO documented formula from characterSize/fontSize to world units. So the world size is MEASURED at run
//    time (Renderer.localBounds, Documentation/en/ScriptReference/Renderer-localBounds.html, checked against the glyph metrics
//    from Font.GetCharacterInfo) and the transform scale is set from that; a clearly-labelled fallback constant is used only if
//    the measurement fails, and then a warning is logged.
//  * Font.CreateDynamicFontFromOSFont(string[] fontnames, int size): Documentation/en/ScriptReference/Font.CreateDynamicFontFromOSFont.html.
//
// UNVERIFIED (nobody has run this in Unity yet): that localBounds is fresh immediately after TextMesh.text changes. If it is
// not, the measurement fails the staleness check below, a warning is logged, and the fallback constant is used.
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Hud
{
    /// <summary>
    /// One piece of world-space HUD text. Create it with <see cref="Create"/>, give it a size with
    /// <see cref="SetAngularHeight"/> and set the words with <see cref="SetText"/> (the mesh is rebuilt only when the words
    /// change). Main thread only. The text faces +Z: parent it to the head or eye rig with identity rotation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudText : MonoBehaviour
    {
        /// <summary>Minimum text (cap) height in degrees of view: docs/MASTER_PLAN.md section 17 (about 2.5 cm at 1.2 m).</summary>
        public const float MinTextDegrees = 1.2f;

        /// <summary>The chord name in the neck HUD is at least this tall (cap height, degrees).</summary>
        public const float ChordNameMinDegrees = 3f;

        /// <summary>Multiplier applied to HUD text sizes when the largeText flag is on (judge profile). My interpretation of that flag.</summary>
        public const float LargeTextScale = 1.35f;

        /// <summary>Glasses pixels per degree: 1080 px over the 26.9 degree vertical field of view of a 52 degree diagonal 16:9 eye.</summary>
        public const float DefaultPixelsPerDegree = 40f;

        public const int MinFontSize = 24;
        public const int MaxFontSize = 256;

        // Arial cap height is 1467/2048 em. Only used to pick a font raster size (sharpness); the world size is measured.
        const float CapHeightEm = 0.716f;

        // UNVERIFIED fallback for the legacy TextMesh scale (one font pixel = 0.1 world units at characterSize 1). Used only
        // when the run-time measurement fails; a warning is logged when that happens.
        const float FallbackWorldPerFontPixel = 0.1f;

        const float MaxAngularDegrees = 170f;
        const int MaxMeasureFrames = 3;
        const string ReferenceGlyph = "H";

        static readonly string[] BuiltinFontNames = { "Arial.ttf", "LegacyRuntime.ttf" };
        static readonly string[] OsFontNames = { "Arial", "Helvetica Neue", "Helvetica" };

        static Font s_Font;
        static bool s_FontFailed;

        TextMesh _mesh;
        MeshRenderer _renderer;
        string _text = string.Empty;
        Color _color = Color.white;
        int _fontSize;
        float _targetCapM;
        float _capLocalUnit;
        int _measureFrames;
        bool _measured;

        // ------------------------------------------------------------------ creation

        /// <summary>Builds a HudText on a new child GameObject of <paramref name="parent"/> (may be null). No Inspector wiring needed.</summary>
        public static HudText Create(Transform parent, string objectName)
        {
            var go = new GameObject(string.IsNullOrEmpty(objectName) ? "HudText" : objectName);
            if (parent != null) go.transform.SetParent(parent, false);
            HudText text = go.AddComponent<HudText>();
            text.Init();
            return text;
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
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

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
            _mesh.color = _color;
            _fontSize = FontSizeForCapDegrees(MinTextDegrees, DefaultPixelsPerDegree);
            _mesh.fontSize = _fontSize;
            _mesh.text = string.Empty;

            // The component's enabled flag is used only to run LateUpdate while a size measurement is still pending.
            enabled = false;
        }

        // ------------------------------------------------------------------ the font

        /// <summary>
        /// The engine's built-in font, loaded once and defensively: "Arial.ttf" first (valid in 2021.3, see the file header), then
        /// "LegacyRuntime.ttf", then an installed OS font. Returns null (and logs once) if all of them fail.
        /// </summary>
        public static Font BuiltinFont
        {
            get
            {
                if (s_Font == null && !s_FontFailed)
                {
                    s_Font = LoadBuiltinFont();
                    if (s_Font == null)
                    {
                        s_FontFailed = true;
                        UGALog.Error("hud_font", "no built-in font could be loaded; HUD text will not render");
                    }
                }
                return s_Font;
            }
        }

        static Font LoadBuiltinFont()
        {
            for (int i = 0; i < BuiltinFontNames.Length; i++)
            {
                try
                {
                    Font f = Resources.GetBuiltinResource<Font>(BuiltinFontNames[i]);
                    if (f != null) return f;
                }
                catch (Exception e)
                {
                    UGALog.Warn("hud_font", BuiltinFontNames[i] + " failed: " + e.GetType().Name);
                }
            }

            try
            {
                Font os = Font.CreateDynamicFontFromOSFont(OsFontNames, 64);
                if (os != null) return os;
            }
            catch (Exception e)
            {
                UGALog.Warn("hud_font", "OS font fallback failed: " + e.GetType().Name);
            }
            return null;
        }

        // ------------------------------------------------------------------ content

        /// <summary>The words currently shown.</summary>
        public string Text { get { return _text; } }

        /// <summary>
        /// Sets the words. The TextMesh is rebuilt only when they actually changed; returns true when it was. No allocation
        /// when nothing changed.
        /// </summary>
        public bool SetText(string text)
        {
            if (!NeedsRebuild(_text, text)) return false;
            _text = text ?? string.Empty;
            _mesh.text = _text;
            return true;
        }

        /// <summary>Text colour. Use bright, saturated colours: black is invisible on the glasses.</summary>
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

        public TextAnchor Anchor
        {
            get { return _mesh.anchor; }
            set { if (_mesh.anchor != value) _mesh.anchor = value; }
        }

        public TextAlignment Alignment
        {
            get { return _mesh.alignment; }
            set { if (_mesh.alignment != value) _mesh.alignment = value; }
        }

        /// <summary>Enables the TextMesh rich-text tags (colour, size). Off by default so displayed words are never parsed as markup.</summary>
        public bool RichText
        {
            get { return _mesh.richText; }
            set { if (_mesh.richText != value) _mesh.richText = value; }
        }

        /// <summary>Shows or hides the text without touching its content.</summary>
        public bool Visible
        {
            get { return _renderer.enabled; }
            set { if (_renderer.enabled != value) _renderer.enabled = value; }
        }

        /// <summary>The cap height this text was last asked to have, in metres of world space.</summary>
        public float CapHeightMeters { get { return _targetCapM; } }

        /// <summary>True once the cap height was measured from the real rendered glyphs (false = fallback constant in use).</summary>
        public bool SizeMeasured { get { return _measured; } }

        // ------------------------------------------------------------------ size

        /// <summary>
        /// Sizes the text so its capital letters span <paramref name="capHeightDegrees"/> of view when the viewer is
        /// <paramref name="distanceM"/> metres away. Values below <see cref="MinTextDegrees"/> are raised to it.
        /// </summary>
        public void SetAngularHeight(float capHeightDegrees, float distanceM)
        {
            float degrees = ClampCapDegrees(capHeightDegrees, MinTextDegrees);
            float meters = SizeForAngularHeight(degrees, distanceM);
            if (meters <= 0f)
            {
                UGALog.Warn("hud_text", name + ": ignored a size request with distance " + distanceM);
                return;
            }

            _targetCapM = meters;
            int size = FontSizeForCapDegrees(degrees, DefaultPixelsPerDegree);
            if (size != _fontSize)
            {
                _fontSize = size;
                _mesh.fontSize = size;
            }

            // Best guess first, so nothing is ever shown at an absurd size while the measurement is pending.
            _capLocalUnit = FallbackCapLocal(_fontSize);
            _measured = false;
            ApplyScale();

            _measureFrames = 0;
            if (TryMeasureCap(out float measured))
            {
                _capLocalUnit = measured;
                _measured = true;
                ApplyScale();
                enabled = false;
            }
            else
            {
                enabled = true;
            }
        }

        void LateUpdate()
        {
            // Only runs while a measurement is pending (see SetAngularHeight); otherwise this component is disabled.
            _measureFrames++;
            if (TryMeasureCap(out float measured))
            {
                _capLocalUnit = measured;
                _measured = true;
                ApplyScale();
                enabled = false;
            }
            else if (_measureFrames >= MaxMeasureFrames)
            {
                UGALog.Warn("hud_text", name + ": cap height could not be measured; using the unverified fallback scale");
                enabled = false;
            }
        }

        static float FallbackCapLocal(int fontSize)
        {
            return fontSize * FallbackWorldPerFontPixel * CapHeightEm;
        }

        // Cap height of the reference glyph in the TextMesh's local units (at localScale 1, characterSize 1, current fontSize).
        bool TryMeasureCap(out float capLocal)
        {
            capLocal = 0f;
            Font font = _mesh.font;
            if (font == null) return false;

            // Glyph metrics from the font itself (pixels at this size): the quad the TextMesh builds for "H" must have these proportions.
            bool haveInfo = false;
            float glyphW = 0f, glyphH = 0f;
            font.RequestCharactersInTexture(ReferenceGlyph, _fontSize, FontStyle.Bold);
            if (font.GetCharacterInfo(ReferenceGlyph[0], out CharacterInfo info, _fontSize, FontStyle.Bold))
            {
                glyphW = Mathf.Abs(info.maxX - info.minX);
                glyphH = Mathf.Abs(info.maxY - info.minY);
                haveInfo = glyphW > 0.5f && glyphH > 0.5f;
            }

            // A hidden text (renderer disabled) may not refresh its bounds, so enable the renderer for the length of this
            // synchronous call: nothing can render in between, and the real text is put back before returning.
            bool wasVisible = _renderer.enabled;
            if (!wasVisible) _renderer.enabled = true;
            string real = _text;
            _mesh.text = ReferenceGlyph;
            Vector3 size = _renderer.localBounds.size;
            _mesh.text = real;
            if (!wasVisible) _renderer.enabled = false;

            if (!IsUsable(size.x) || !IsUsable(size.y)) return false;

            if (haveInfo)
            {
                // Staleness check: a bounds box left over from some other text would not have the proportions of an "H".
                float measuredRatio = size.x / size.y;
                float expectedRatio = glyphW / glyphH;
                if (Mathf.Abs(measuredRatio / expectedRatio - 1f) > 0.3f) return false;
                capLocal = size.x / glyphW * glyphH;   // world units per glyph pixel, from the width; immune to line padding in the height
            }
            else
            {
                capLocal = size.y;
            }
            return IsUsable(capLocal);
        }

        void ApplyScale()
        {
            float parentScale = 1f;
            Transform parent = transform.parent;
            if (parent != null)
            {
                float ps = parent.lossyScale.y;
                if (ps > 1e-6f) parentScale = ps;
            }
            float s = _targetCapM / (_capLocalUnit * parentScale);
            if (IsUsable(s)) transform.localScale = new Vector3(s, s, s);
        }

        static bool IsUsable(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && v > 1e-6f;
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>
        /// World height in metres of something that spans <paramref name="degrees"/> of view at <paramref name="distanceM"/>
        /// metres: 2 * d * tan(degrees / 2). Use it for the cap height of text. SizeForAngularHeight(1.2, 1.2) is about 2.5 cm,
        /// the project's minimum text. Returns 0 for a non-positive or non-finite input.
        /// </summary>
        public static float SizeForAngularHeight(float degrees, float distanceM)
        {
            if (!IsUsable(degrees) || !IsUsable(distanceM)) return 0f;
            float halfRadians = Mathf.Min(degrees, MaxAngularDegrees) * 0.5f * Mathf.Deg2Rad;
            return 2f * distanceM * Mathf.Tan(halfRadians);
        }

        /// <summary>Inverse of <see cref="SizeForAngularHeight"/>: the degrees of view a height of <paramref name="sizeM"/> spans at <paramref name="distanceM"/>.</summary>
        public static float AngularHeightForSize(float sizeM, float distanceM)
        {
            if (!IsUsable(sizeM) || !IsUsable(distanceM)) return 0f;
            return 2f * Mathf.Atan(sizeM / (2f * distanceM)) * Mathf.Rad2Deg;
        }

        /// <summary>Raises <paramref name="degrees"/> to the larger of <paramref name="minDegrees"/> and <see cref="MinTextDegrees"/> (NaN becomes that floor).</summary>
        public static float ClampCapDegrees(float degrees, float minDegrees)
        {
            float floor = Mathf.Max(minDegrees, MinTextDegrees);
            if (float.IsNaN(degrees)) return floor;
            return Mathf.Max(degrees, floor);
        }

        /// <summary>1 normally, <see cref="LargeTextScale"/> when the largeText flag is on. Text may only ever get bigger, never below the minimums.</summary>
        public static float TextScale(bool largeText)
        {
            return largeText ? LargeTextScale : 1f;
        }

        /// <summary>The raster size of the font (pixels) that makes glyphs about 1:1 with the glasses' pixels for a given cap height.</summary>
        public static int FontSizeForCapDegrees(float capDegrees, float pixelsPerDegree)
        {
            if (!IsUsable(capDegrees) || !IsUsable(pixelsPerDegree)) return MinFontSize;
            float px = capDegrees * pixelsPerDegree / CapHeightEm;
            return Mathf.Clamp(Mathf.RoundToInt(px), MinFontSize, MaxFontSize);
        }

        /// <summary>
        /// The change-detection rule shared by every HUD label: rebuild only when the words differ (ordinal compare; null and
        /// empty are the same thing). Pure, allocation-free.
        /// </summary>
        public static bool NeedsRebuild(string current, string next)
        {
            return !string.Equals(current ?? string.Empty, next ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
