// LearnGraphics: the shared meshes and the one shared material of the Learn HUD's drawn shapes.
//
// Material. Preferred: the shader Runtime/Resources/Shaders/HudLearn.shader, loaded with Resources.Load (Shader.Find fails in
// builds). It is unlit and "lighten" blended, so black adds nothing (the glasses are additive) and the draw order of
// overlapping shapes does not matter. THAT SHADER HAS NEVER BEEN COMPILED (written offline). So there is a fallback that
// needs no new asset: a copy of the built-in font's own material with a plain white texture. HudText already depends on
// that material, so if text renders, the fallback renders too. A warning is logged once when the fallback is used.
//
// SOURCES (read offline; Unity 2021.3.3f1 = /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents):
//  * Documentation/en/ScriptReference/Resources.Load.html: "returns the asset at path if it can be found, otherwise it returns
//    null ... The path is relative to any folder named Resources ... must not contain a file extension".
//  * Documentation/en/ScriptReference/Shader-isSupported.html: "Can this shader run on the end-users graphics card? ... Returns
//    true if the shader itself or any fallbacks setup in the shader are supported."
//  * Documentation/en/ScriptReference/MaterialPropertyBlock.html: "The block passed to Graphics.DrawMesh or
//    Renderer.SetPropertyBlock is copied, so the most efficient way of using it is to create one block and reuse it".
//  * The built-in font shader has a _Color property and multiplies it into the vertex colour: the strings of
//    Resources/unity default resources show the material "Font Material" with _MainTex and _Color, and the shader
//    "GUI/Text Shader" with the properties _MainTex ("Font Texture") and _Color ("Text Color"), the vertex inputs POSITION,
//    COLOR, TEXCOORD and the globals _MainTex_ST and _Color. Its blend and depth states are NOT readable from the binary
//    (from the published built-in shader source they are "Blend SrcAlpha OneMinusSrcAlpha", "ZTest Always"; not verified here).
//
// UNVERIFIED (nobody has run this in Unity yet): that Shader.isSupported is false for a shader with compile errors, which is
// what sends a broken HudLearn.shader to the fallback. If it is true instead, the shapes would draw in the error colour
// (magenta): fix or delete the shader file and the fallback takes over.
using System.Collections.Generic;
using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>Shared meshes, material and tinting for the Learn HUD's shapes. Main thread only.</summary>
    public static class LearnGraphics
    {
        /// <summary>Resources path of the shape shader (no extension): Runtime/Resources/Shaders/HudLearn.shader.</summary>
        public const string ShaderResourcePath = "Shaders/HudLearn";

        const string ColorProperty = "_Color";

        static readonly Mesh[] s_Meshes = new Mesh[LearnGlyphs.Count];
        static Material s_Material;
        static bool s_MaterialFailed;
        static bool s_Fallback;
        static MaterialPropertyBlock s_Block;
        static int s_ColorId;
        static bool s_HaveColorId;

        static StrokeGeometry s_Scratch;
        static List<Color32> s_Colors;
        static List<Vector2> s_Uvs;

        /// <summary>True when the shapes are drawn with the font material fallback instead of HudLearn.shader.</summary>
        public static bool UsingFallbackShader { get { return s_Fallback; } }

        static int ColorId
        {
            get
            {
                if (!s_HaveColorId)
                {
                    s_ColorId = Shader.PropertyToID(ColorProperty);
                    s_HaveColorId = true;
                }
                return s_ColorId;
            }
        }

        /// <summary>The one material every Learn HUD shape shares (colour comes per renderer from <see cref="SetTint"/>). Null if nothing could be loaded.</summary>
        public static Material SharedMaterial
        {
            get
            {
                if (s_Material == null && !s_MaterialFailed)
                {
                    s_Material = CreateMaterial();
                    if (s_Material == null)
                    {
                        s_MaterialFailed = true;
                        UGALog.Error("hud_learn_material", "no shader for the Learn HUD shapes; marks and icons will not render");
                    }
                }
                return s_Material;
            }
        }

        static Material CreateMaterial()
        {
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader != null && shader.isSupported)
            {
                var material = new Material(shader);
                material.name = "HudLearn";
                material.hideFlags = HideFlags.HideAndDontSave;
                s_Fallback = false;
                return material;
            }

            Font font = HudText.BuiltinFont;
            Material fontMaterial = font != null ? font.material : null;
            if (fontMaterial == null) return null;

            var fallback = new Material(fontMaterial);
            fallback.name = "HudLearn (font shader fallback)";
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.mainTexture = Texture2D.whiteTexture;
            s_Fallback = true;
            UGALog.Warn("hud_learn_material", shader == null
                ? "Resources/" + ShaderResourcePath + " not found; using the font shader for shapes"
                : "HudLearn shader is not supported here; using the font shader for shapes");
            return fallback;
        }

        /// <summary>The shared mesh for a glyph, built on first use.</summary>
        public static Mesh GetMesh(LearnGlyph glyph)
        {
            int index = (int)glyph;
            if (index < 0 || index >= s_Meshes.Length) return null;
            Mesh mesh = s_Meshes[index];
            if (mesh == null)
            {
                mesh = BuildMesh(glyph);
                s_Meshes[index] = mesh;
            }
            return mesh;
        }

        static Mesh BuildMesh(LearnGlyph glyph)
        {
            if (s_Scratch == null)
            {
                s_Scratch = new StrokeGeometry();
                s_Colors = new List<Color32>(256);
                s_Uvs = new List<Vector2>(256);
            }
            LearnGlyphs.Build(glyph, s_Scratch);

            int count = s_Scratch.Vertices.Count;
            s_Colors.Clear();
            s_Uvs.Clear();
            var white = new Color32(255, 255, 255, 255);
            var uv = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < count; i++)
            {
                s_Colors.Add(white);
                s_Uvs.Add(uv);
            }

            var mesh = new Mesh();
            mesh.name = "HudLearn " + glyph;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.SetVertices(s_Scratch.Vertices);
            mesh.SetColors(s_Colors);
            mesh.SetUVs(0, s_Uvs);
            mesh.SetTriangles(s_Scratch.Triangles, 0, true);
            return mesh;
        }

        /// <summary>
        /// Gives one renderer its own colour and brightness without a material copy (a shared property block).
        /// Works for the shapes and for HudText's renderer alike: both shaders multiply by _Color. Allocates nothing.
        /// </summary>
        public static void SetTint(Renderer renderer, Color color)
        {
            if (renderer == null) return;
            if (s_Block == null) s_Block = new MaterialPropertyBlock();
            s_Block.SetColor(ColorId, color);
            renderer.SetPropertyBlock(s_Block);
        }

        /// <summary>Applies the renderer settings every HUD element uses: no shadows, no probes, no motion vectors.</summary>
        public static void ConfigureRenderer(MeshRenderer renderer)
        {
            if (renderer == null) return;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>
        /// Destroys the cached meshes and the material (they are rebuilt on next use). For editor tools that render a
        /// preview and want to leave nothing behind; the running app never needs it.
        /// </summary>
        public static void ReleaseShared()
        {
            for (int i = 0; i < s_Meshes.Length; i++)
            {
                if (s_Meshes[i] != null) DestroyObject(s_Meshes[i]);
                s_Meshes[i] = null;
            }
            if (s_Material != null) DestroyObject(s_Material);
            s_Material = null;
            s_MaterialFailed = false;
            s_Fallback = false;
        }

        static void DestroyObject(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
