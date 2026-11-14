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
//    text)". There is NO documented formula from characterSize/fontSize to world units. So the size comes from a constant
//    MEASURED in Unity's own renders (CapUnitsPerFontSize below; Editor/HudTextCalibration.cs measures it) and the transform
//    is scaled to fit. The same size every time, from the first frame: nothing is measured at run time.
//  * Font.CreateDynamicFontFromOSFont(string[] fontnames, int size): Documentation/en/ScriptReference/Font.CreateDynamicFontFromOSFont.html.
//
// WHY NOT A RUN-TIME MEASUREMENT (the code before 14 Nov 12:00): it read Renderer.localBounds of an "H" against
// Font.GetCharacterInfo, with an unverified fallback (0.1 unit per font pixel). In the full-app frame of 14 Nov 11:38 the
// chord name "G", asked for 4.0 degrees, measured 3.28 (0.82 of the request; 0.79 for the cap height once the G's overshoot,
// 1516 vs 1466 font units in Arial Bold, is taken out). NeckLabel (Runtime/Neck) had the same fault: its bounds gave 0.64 of
// the request after a few renders and its fallback 0.769 before any, so one request gave two sizes depending on the frame.
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

        /// <summary>
        /// MEASURED, not documented: the cap height of a bold "H" (Arial Bold, the built-in font) in the TextMesh's own units,
        /// per unit of fontSize, at characterSize 1. World cap height = fontSize * CapUnitsPerFontSize * localScale * parent scale.
        /// PROVISIONAL VALUE (round 1, 14 Nov ~12:15): NeckLabel.DigitUnitsPerFontSize (0.0551, measured from 50% edges of a bold
        /// "1" in three Unity renders of 14 Nov, at its real distance from the eye) times Arial Bold's H / 1 height ratio,
        /// 1466 / 1472 font units (the 'glyf' table of /System/Library/Fonts/Supplemental/Arial Bold.ttf, read with fontTools)
        /// = 0.0549. Editor/HudTextCalibration.cs renders "H" at 1.2 to 4 degrees and 1.0 / 1.2 m with a glasses-eye camera and
        /// measures it; this value is replaced by that measurement (see the calibration JSON named in the commit).
        /// </summary>
        public const float CapUnitsPerFontSize = 0.0549f;

        // Arial cap height is 1466/2048 em. Only used to pick a font raster size (sharpness), not the world size.
        const float CapHeightEm = 0.716f;

        const float MaxAngularDegrees = 170f;

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

        /// <summary>
        /// True once a size has been applied. The size always comes from <see cref="CapUnitsPerFontSize"/>, a constant measured
        /// in Unity's renders, so it is right from the first frame and never changes afterwards; there is no fallback any more.
        /// (Kept for the preview scripts that report it.)
        /// </summary>
        public bool SizeMeasured { get { return _targetCapM > 0f; } }

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
            ApplyScale();
        }

        void ApplyScale()
        {
            float parentScale = 1f;
            Transform parent = transform.parent;
            if (parent != null)
            {
                float ps = Mathf.Abs(parent.lossyScale.y);
                if (ps > 1e-6f) parentScale = ps;
            }
            float s = ScaleForCap(_targetCapM, _fontSize, parentScale);
            if (s > 0f) transform.localScale = new Vector3(s, s, s);
        }

        static bool IsUsable(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && v > 1e-6f;
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>The cap height of the text in the TextMesh's own units at characterSize 1 and this font size (the measured constant).</summary>
        public static float CapHeightUnits(int fontSize)
        {
            return fontSize * CapUnitsPerFontSize;
        }

        /// <summary>
        /// The uniform localScale that gives capital letters a world height of <paramref name="capMeters"/> at this font size,
        /// under a parent whose world (lossy) scale is <paramref name="parentScale"/>: capMeters / (fontSize * CapUnitsPerFontSize
        /// * parentScale). A pure function of its arguments, so a request always gives the same size, from the first frame.
        /// Returns 0 for a non-positive or non-finite input (the caller then leaves the scale alone).
        /// </summary>
        public static float ScaleForCap(float capMeters, int fontSize, float parentScale)
        {
            if (!IsUsable(capMeters) || fontSize <= 0 || !IsUsable(parentScale)) return 0f;
            float s = capMeters / (CapHeightUnits(fontSize) * parentScale);
            return IsUsable(s) ? s : 0f;
        }

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
