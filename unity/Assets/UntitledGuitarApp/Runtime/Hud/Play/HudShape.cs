// HudShape: the non-text shapes of the play-along HUD (dots, arrows, carets, bars, rings), built only from code.
//
// Technique: small procedural meshes whose edges fade out through vertex alpha (a "feather" a few pixels wide), drawn with
// one unlit additive material. The feather is the anti-aliasing: the eye cameras need no MSAA, and a soft bright edge is
// what reads best on an additive display. Colour is set per shape through a MaterialPropertyBlock, so all shapes share one
// material and nothing is allocated when a colour changes. Nothing here can draw anything dark.
//
// SOURCES (offline documentation of Unity 2021.3.3f1: Unity.app/Contents/Documentation/en/ScriptReference):
//  * Resources.Load.html: "Loads the asset of the requested type stored at path in a Resources folder" (path relative to
//    the Resources folder, no extension): Resources.Load<Shader>("Shaders/HudPlay").
//  * Shader-isSupported.html: "Can this shader run on the end-users graphics card?"
//  * Shader.Find.html: only used as the FALLBACK when Shaders/HudPlay is missing or unsupported. It warns that a shader is
//    only in a build if something references it or it is in "Always Included Shaders"; "Sprites/Default" is in that list
//    in a default project (UNVERIFIED for this project's GraphicsSettings).
//  * MaterialPropertyBlock.SetColor.html, Renderer.SetPropertyBlock.html: per-renderer colour without a material instance.
//  * Mesh.SetVertices.html, Mesh.SetColors.html, Mesh.SetTriangles.html, Mesh.MarkDynamic.html, Mesh-bounds.html.
//  * Unity.app/Contents/CGIncludes/UnitySprites.cginc: the Sprites/Default vertex function is
//    "OUT.color = IN.color * _Color * _RendererColor", i.e. the same vertex colour * _Color maths as HudPlay.shader.
//
// UNVERIFIED (nobody has run this in Unity yet): that HudPlay.shader compiles (it never has been compiled), and that a
// shader with compile errors reports isSupported == false so that the fallback is taken.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// Pure mesh-data builders (arrays only, no engine objects), so they can be unit-tested. All shapes lie in the XY
    /// plane, are centred on the origin and are white; alpha 255 inside, fading to 0 across the feather.
    /// </summary>
    public static class HudShapeGeometry
    {
        /// <summary>Loops of a ring mesh: feather-in, core inner edge, core outer edge, feather-out.</summary>
        public const int RingLoops = 4;

        /// <summary>
        /// A filled polygon with a feathered edge. <paramref name="outline"/> runs counter-clockwise and must be visible
        /// from <paramref name="kernel"/> (a point inside from which every corner can be seen; the centre for a convex
        /// shape). Vertices: the kernel, then the outline (opaque), then the outline pushed out by
        /// <paramref name="feather"/> (transparent).
        /// </summary>
        public static void FeatheredPolygon(Vector2[] outline, Vector2 kernel, float feather,
            out Vector3[] vertices, out Color32[] colors, out int[] triangles)
        {
            int n = outline != null ? outline.Length : 0;
            if (n < 3)
            {
                vertices = new Vector3[0];
                colors = new Color32[0];
                triangles = new int[0];
                return;
            }

            vertices = new Vector3[1 + 2 * n];
            colors = new Color32[1 + 2 * n];
            triangles = new int[n * 9];
            var solid = new Color32(255, 255, 255, 255);
            var clear = new Color32(255, 255, 255, 0);

            vertices[0] = kernel;
            colors[0] = solid;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = outline[i];
                Vector2 prev = outline[(i + n - 1) % n];
                Vector2 next = outline[(i + 1) % n];
                Vector2 n1 = OutwardNormal(prev, p);
                Vector2 n2 = OutwardNormal(p, next);
                Vector2 m = n1 + n2;
                float mLen = m.magnitude;
                m = mLen > 1e-6f ? m / mLen : n1;
                // Mitre: keep the feather the same width along both edges; limit it at sharp corners.
                float cos = Mathf.Max(0.35f, Vector2.Dot(m, n1));
                Vector2 outer = p + m * (feather / cos);

                vertices[1 + i] = p;
                colors[1 + i] = solid;
                vertices[1 + n + i] = outer;
                colors[1 + n + i] = clear;
            }

            int t = 0;
            for (int i = 0; i < n; i++)
            {
                int a = 1 + i;
                int b = 1 + (i + 1) % n;
                int c = 1 + n + i;
                int d = 1 + n + (i + 1) % n;
                triangles[t++] = 0; triangles[t++] = a; triangles[t++] = b;     // fill
                triangles[t++] = a; triangles[t++] = c; triangles[t++] = d;     // feather
                triangles[t++] = a; triangles[t++] = d; triangles[t++] = b;
            }
        }

        // For a counter-clockwise outline the inside is on the left of the direction of travel, so outward is to the right.
        static Vector2 OutwardNormal(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            float len = d.magnitude;
            if (len < 1e-9f) return Vector2.zero;
            return new Vector2(d.y, -d.x) / len;
        }

        /// <summary>Outline of a regular polygon approximating a circle of <paramref name="radius"/>, counter-clockwise.</summary>
        public static Vector2[] CircleOutline(float radius, int segments)
        {
            segments = Mathf.Max(3, segments);
            var outline = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = i * (2f * Mathf.PI / segments);
                outline[i] = new Vector2(radius * Mathf.Cos(a), radius * Mathf.Sin(a));
            }
            return outline;
        }

        /// <summary>
        /// Outline of an arrow pointing up, 1 tall (y from -0.5 to +0.5) including nothing of the feather: a triangular
        /// head on a shaft. Counter-clockwise from the tip. Visible from <see cref="ArrowKernel"/>.
        /// </summary>
        public static Vector2[] ArrowOutline(float headWidth, float headHeight, float shaftWidth)
        {
            float hw = headWidth * 0.5f;
            float sw = shaftWidth * 0.5f;
            float neck = 0.5f - headHeight;
            return new[]
            {
                new Vector2(0f, 0.5f),
                new Vector2(-hw, neck),
                new Vector2(-sw, neck),
                new Vector2(-sw, -0.5f),
                new Vector2(sw, -0.5f),
                new Vector2(sw, neck),
                new Vector2(hw, neck),
            };
        }

        /// <summary>A point just inside the arrow's head from which all seven corners can be seen.</summary>
        public static Vector2 ArrowKernel(float headHeight)
        {
            return new Vector2(0f, 0.5f - headHeight + 0.04f);
        }

        /// <summary>Outline of a triangle pointing up, 1 tall and <paramref name="width"/> wide, counter-clockwise.</summary>
        public static Vector2[] TriangleOutline(float width)
        {
            return new[]
            {
                new Vector2(0f, 0.5f),
                new Vector2(-width * 0.5f, -0.5f),
                new Vector2(width * 0.5f, -0.5f),
            };
        }

        /// <summary>Outline of a bar with round ends (a stadium), <paramref name="width"/> by <paramref name="height"/>, counter-clockwise.</summary>
        public static Vector2[] CapsuleOutline(float width, float height, int segmentsPerEnd)
        {
            segmentsPerEnd = Mathf.Max(2, segmentsPerEnd);
            bool wide = width >= height;
            float r = (wide ? height : width) * 0.5f;
            float half = (wide ? width : height) * 0.5f - r;
            var outline = new Vector2[2 * (segmentsPerEnd + 1)];
            int k = 0;
            for (int end = 0; end < 2; end++)
            {
                // end 0: the right (or top) cap, sweeping -90..+90 degrees; end 1: the left (or bottom) cap, +90..+270.
                float start = end == 0 ? -0.5f * Mathf.PI : 0.5f * Mathf.PI;
                float centre = end == 0 ? half : -half;
                for (int i = 0; i <= segmentsPerEnd; i++)
                {
                    float a = start + i * (Mathf.PI / segmentsPerEnd);
                    float cx = r * Mathf.Cos(a);
                    float cy = r * Mathf.Sin(a);
                    // For a tall bar the same shape is turned a quarter turn (x, y) -> (-y, x), which keeps it counter-clockwise.
                    outline[k++] = wide ? new Vector2(centre + cx, cy) : new Vector2(-cy, centre + cx);
                }
            }
            return outline;
        }

        /// <summary>
        /// A soft round glow, 1 across: opaque-ish in the middle, fading to nothing at the edge. Vertices: the centre,
        /// then three rings.
        /// </summary>
        public static void Halo(int segments, out Vector3[] vertices, out Color32[] colors, out int[] triangles)
        {
            segments = Mathf.Max(6, segments);
            float[] radii = { 0.18f, 0.34f, 0.5f };
            byte[] alphas = { 150, 56, 0 };
            int rings = radii.Length;
            vertices = new Vector3[1 + rings * segments];
            colors = new Color32[1 + rings * segments];
            triangles = new int[segments * 3 + (rings - 1) * segments * 6];
            vertices[0] = Vector3.zero;
            colors[0] = new Color32(255, 255, 255, 255);
            for (int r = 0; r < rings; r++)
            {
                for (int i = 0; i < segments; i++)
                {
                    float a = i * (2f * Mathf.PI / segments);
                    vertices[1 + r * segments + i] = new Vector3(radii[r] * Mathf.Cos(a), radii[r] * Mathf.Sin(a), 0f);
                    colors[1 + r * segments + i] = new Color32(255, 255, 255, alphas[r]);
                }
            }
            int t = 0;
            for (int i = 0; i < segments; i++)
            {
                triangles[t++] = 0; triangles[t++] = 1 + i; triangles[t++] = 1 + (i + 1) % segments;
            }
            for (int r = 0; r < rings - 1; r++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = 1 + r * segments + i;
                    int b = 1 + r * segments + (i + 1) % segments;
                    int c = 1 + (r + 1) * segments + i;
                    int d = 1 + (r + 1) * segments + (i + 1) % segments;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = d;
                    triangles[t++] = a; triangles[t++] = d; triangles[t++] = b;
                }
            }
        }

        /// <summary>Number of vertices of a ring mesh with <paramref name="segments"/> segments.</summary>
        public static int RingVertexCount(int segments)
        {
            return RingLoops * Mathf.Max(3, segments);
        }

        /// <summary>
        /// Writes the vertices of a ring (annulus) of centre-line <paramref name="radius"/> whose stroke is
        /// 2 * <paramref name="halfStroke"/> wide plus a <paramref name="feather"/> on each side, into a preallocated
        /// array of <see cref="RingVertexCount"/> entries. No allocation: call it every frame to change the radius while
        /// the stroke keeps its weight. Loop l, segment i is vertex l * segments + i.
        /// </summary>
        public static void FillRingVertices(Vector3[] vertices, int segments, float radius, float halfStroke, float feather)
        {
            segments = Mathf.Max(3, segments);
            if (vertices == null || vertices.Length < RingLoops * segments) return;
            float r1 = Mathf.Max(0f, radius - halfStroke);
            float r0 = Mathf.Max(0f, r1 - feather);
            float r2 = Mathf.Max(r1, radius + halfStroke);
            float r3 = r2 + Mathf.Max(0f, feather);
            float step = 2f * Mathf.PI / segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i * step;
                float c = Mathf.Cos(a);
                float s = Mathf.Sin(a);
                vertices[i] = new Vector3(r0 * c, r0 * s, 0f);
                vertices[segments + i] = new Vector3(r1 * c, r1 * s, 0f);
                vertices[2 * segments + i] = new Vector3(r2 * c, r2 * s, 0f);
                vertices[3 * segments + i] = new Vector3(r3 * c, r3 * s, 0f);
            }
        }

        /// <summary>The fixed colours of a ring mesh: transparent, opaque, opaque, transparent.</summary>
        public static Color32[] RingColors(int segments)
        {
            segments = Mathf.Max(3, segments);
            var colors = new Color32[RingLoops * segments];
            for (int i = 0; i < segments; i++)
            {
                colors[i] = new Color32(255, 255, 255, 0);
                colors[segments + i] = new Color32(255, 255, 255, 255);
                colors[2 * segments + i] = new Color32(255, 255, 255, 255);
                colors[3 * segments + i] = new Color32(255, 255, 255, 0);
            }
            return colors;
        }

        /// <summary>The fixed triangles of a ring mesh (three bands of quads).</summary>
        public static int[] RingTriangles(int segments)
        {
            segments = Mathf.Max(3, segments);
            var triangles = new int[(RingLoops - 1) * segments * 6];
            int t = 0;
            for (int l = 0; l < RingLoops - 1; l++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = l * segments + i;
                    int b = l * segments + (i + 1) % segments;
                    int c = (l + 1) * segments + i;
                    int d = (l + 1) * segments + (i + 1) % segments;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = d;
                    triangles[t++] = a; triangles[t++] = d; triangles[t++] = b;
                }
            }
            return triangles;
        }
    }

    /// <summary>
    /// One HUD shape: a GameObject with a MeshFilter and a MeshRenderer using the shared HUD material. Create it once,
    /// then move, size, colour and show or hide it; none of that allocates. Main thread only. A plain class, not a
    /// component: the owner keeps the reference.
    /// </summary>
    public sealed class HudShape
    {
        /// <summary>Resources path of the shader (Runtime/Resources/Shaders/HudPlay.shader).</summary>
        public const string ShaderResourcePath = "Shaders/HudPlay";

        public const int CircleSegments = 40;
        public const int RingSegments = 56;

        // Arrow proportions (of its height): a chunky head and a thick shaft read at small sizes.
        public const float ArrowHeadWidth = 0.66f;
        public const float ArrowHeadHeight = 0.46f;
        public const float ArrowShaftWidth = 0.24f;
        public const float ArrowWidth = ArrowHeadWidth;
        public const float UnitFeather = 0.045f;

        static Material s_Material;
        static bool s_MaterialFailed;
        static string s_ShaderSource = "none";
        static MaterialPropertyBlock s_Block;
        static int s_ColorId;
        static bool s_ColorIdSet;

        static Mesh s_Disc;
        static Mesh s_Halo;
        static Mesh s_Arrow;
        static Mesh s_Caret;
        static Mesh s_Ring;

        readonly GameObject _go;
        readonly Transform _transform;
        readonly MeshFilter _filter;
        readonly MeshRenderer _renderer;
        Color _color = Color.white;
        bool _colorApplied;

        HudShape(GameObject go, MeshFilter filter, MeshRenderer renderer)
        {
            _go = go;
            _transform = go.transform;
            _filter = filter;
            _renderer = renderer;
        }

        // ------------------------------------------------------------------ creation

        /// <summary>Creates a shape under <paramref name="parent"/> (may be null) showing <paramref name="mesh"/>.</summary>
        public static HudShape Create(Transform parent, string objectName, Mesh mesh)
        {
            var go = new GameObject(string.IsNullOrEmpty(objectName) ? "HudShape" : objectName);
            if (parent != null) go.transform.SetParent(parent, false);
            MeshFilter filter = go.AddComponent<MeshFilter>();
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.sharedMaterial = SharedMaterial;
            filter.sharedMesh = mesh;
            var shape = new HudShape(go, filter, renderer);
            shape.ApplyColor();
            return shape;
        }

        /// <summary>A bar with round ends, built for exactly this size in metres so its soft edge is even all round. Not shared: one mesh per call.</summary>
        public static HudShape CreateBar(Transform parent, string objectName, float widthM, float heightM)
        {
            float feather = Mathf.Min(widthM, heightM) * 0.22f;
            float w = Mathf.Max(1e-5f, widthM - 2f * feather);
            float h = Mathf.Max(1e-5f, heightM - 2f * feather);
            Vector2[] outline = HudShapeGeometry.CapsuleOutline(w, h, 6);
            return Create(parent, objectName, BuildPolygon("HudBar", outline, Vector2.zero, feather));
        }

        // ------------------------------------------------------------------ the shared material

        /// <summary>
        /// The one material every HUD shape uses. Created on first use from Resources "Shaders/HudPlay"; if that shader is
        /// missing or unsupported, from the built-in "Sprites/Default" (then "UI/Default"). Null, with one error logged, if
        /// none of them can be found: shapes then do not render, text still does.
        /// </summary>
        public static Material SharedMaterial
        {
            get
            {
                if (s_Material == null && !s_MaterialFailed)
                {
                    Shader shader = Resources.Load<Shader>(ShaderResourcePath);
                    string source = "resources";
                    if (shader == null || !shader.isSupported)
                    {
                        string why = shader == null ? "not found in Resources" : "not supported (compile error?)";
                        UGALog.Warn("hudplay_material", ShaderResourcePath + " " + why + "; falling back to a built-in shader");
                        shader = Shader.Find("Sprites/Default");
                        source = "Sprites/Default";
                        if (shader == null || !shader.isSupported)
                        {
                            shader = Shader.Find("UI/Default");
                            source = "UI/Default";
                        }
                    }

                    if (shader == null)
                    {
                        s_MaterialFailed = true;
                        UGALog.Error("hudplay_material", "no shader for HUD shapes; dots, arrows and rings will not render");
                        return null;
                    }

                    s_Material = new Material(shader);
                    s_Material.name = "HudPlay (runtime)";
                    s_Material.hideFlags = HideFlags.HideAndDontSave;
                    s_ShaderSource = source;
                    UGALog.Event("hudplay_material", "shader", shader.name, "source", source);
                }
                return s_Material;
            }
        }

        /// <summary>Where the shared material's shader came from: "resources", "Sprites/Default", "UI/Default" or "none". For the preview report.</summary>
        public static string ShaderSource { get { return s_ShaderSource; } }

        // ------------------------------------------------------------------ shared meshes (unit size, built once)

        /// <summary>A filled circle, 1 across.</summary>
        public static Mesh Disc
        {
            get
            {
                if (s_Disc == null)
                    s_Disc = BuildPolygon("HudDisc", HudShapeGeometry.CircleOutline(0.5f - UnitFeather, CircleSegments), Vector2.zero, UnitFeather);
                return s_Disc;
            }
        }

        /// <summary>A soft glow, 1 across.</summary>
        public static Mesh Halo
        {
            get
            {
                if (s_Halo == null)
                {
                    HudShapeGeometry.Halo(CircleSegments, out Vector3[] v, out Color32[] c, out int[] t);
                    s_Halo = BuildMesh("HudHalo", v, c, t);
                }
                return s_Halo;
            }
        }

        /// <summary>An arrow pointing up, 1 tall and <see cref="ArrowWidth"/> wide. Turn it half a turn for a down stroke.</summary>
        public static Mesh Arrow
        {
            get
            {
                if (s_Arrow == null)
                {
                    // The outline is drawn slightly inside the unit box so the feather ends on it.
                    Vector2[] outline = HudShapeGeometry.ArrowOutline(ArrowHeadWidth - 2f * UnitFeather, ArrowHeadHeight, ArrowShaftWidth - 2f * UnitFeather);
                    for (int i = 0; i < outline.Length; i++) outline[i].y *= 1f - 2f * UnitFeather;
                    Vector2 kernel = HudShapeGeometry.ArrowKernel(ArrowHeadHeight);
                    kernel.y *= 1f - 2f * UnitFeather;
                    s_Arrow = BuildPolygon("HudArrow", outline, kernel, UnitFeather);
                }
                return s_Arrow;
            }
        }

        /// <summary>A triangle pointing up, 1 tall and 1.1 wide: the cursor.</summary>
        public static Mesh Caret
        {
            get
            {
                if (s_Caret == null)
                {
                    Vector2[] outline = HudShapeGeometry.TriangleOutline(1.1f - 4f * UnitFeather);
                    for (int i = 0; i < outline.Length; i++) outline[i].y *= 1f - 3f * UnitFeather;
                    s_Caret = BuildPolygon("HudCaret", outline, new Vector2(0f, -0.15f), UnitFeather);
                }
                return s_Caret;
            }
        }

        /// <summary>A thin ring of radius 0.5 (1 across), for outlines that do not change size.</summary>
        public static Mesh Ring
        {
            get
            {
                if (s_Ring == null)
                {
                    var vertices = new Vector3[HudShapeGeometry.RingVertexCount(RingSegments)];
                    HudShapeGeometry.FillRingVertices(vertices, RingSegments, 0.5f - 0.06f, 0.035f, 0.025f);
                    s_Ring = BuildMesh("HudRing", vertices, HudShapeGeometry.RingColors(RingSegments), HudShapeGeometry.RingTriangles(RingSegments));
                }
                return s_Ring;
            }
        }

        /// <summary>
        /// A ring mesh whose radius will change every frame: fixed colours and triangles, vertices to be written with
        /// <see cref="HudShapeGeometry.FillRingVertices"/> and <see cref="UploadRing"/>. <paramref name="maxRadiusM"/> fixes
        /// the bounds once, so they are never recalculated.
        /// </summary>
        public static Mesh CreateDynamicRing(string meshName, Vector3[] vertices, float maxRadiusM)
        {
            var mesh = new Mesh();
            mesh.name = meshName;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.MarkDynamic();
            mesh.SetVertices(vertices);
            mesh.SetColors(HudShapeGeometry.RingColors(RingSegments));
            mesh.SetTriangles(HudShapeGeometry.RingTriangles(RingSegments), 0, false);
            float d = Mathf.Max(1e-4f, 2f * maxRadiusM);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(d, d, d * 0.05f));
            return mesh;
        }

        /// <summary>Sends changed ring vertices to the mesh. No allocation, bounds untouched.</summary>
        public static void UploadRing(Mesh mesh, Vector3[] vertices)
        {
            if (mesh != null) mesh.SetVertices(vertices);
        }

        static Mesh BuildPolygon(string meshName, Vector2[] outline, Vector2 kernel, float feather)
        {
            HudShapeGeometry.FeatheredPolygon(outline, kernel, feather, out Vector3[] v, out Color32[] c, out int[] t);
            return BuildMesh(meshName, v, c, t);
        }

        static Mesh BuildMesh(string meshName, Vector3[] vertices, Color32[] colors, int[] triangles)
        {
            var mesh = new Mesh();
            mesh.name = meshName;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, true);
            return mesh;
        }

        // ------------------------------------------------------------------ per-shape state

        public Transform Transform { get { return _transform; } }

        public GameObject GameObject { get { return _go; } }

        public Mesh Mesh
        {
            get { return _filter.sharedMesh; }
            set { if (_filter.sharedMesh != value) _filter.sharedMesh = value; }
        }

        /// <summary>Shows or hides the shape.</summary>
        public bool Visible
        {
            get { return _renderer.enabled; }
            set { if (_renderer.enabled != value) _renderer.enabled = value; }
        }

        /// <summary>Tint (alpha scales the brightness). Applied only when it changed.</summary>
        public Color Color
        {
            get { return _color; }
            set
            {
                if (_colorApplied && value == _color) return;
                _color = value;
                ApplyColor();
            }
        }

        /// <summary>Sets the colour with a separate brightness in 0..1 (the alpha).</summary>
        public void SetColor(Color rgb, float level)
        {
            rgb.a = Mathf.Clamp01(level);
            Color = rgb;
        }

        void ApplyColor()
        {
            if (s_Block == null) s_Block = new MaterialPropertyBlock();
            if (!s_ColorIdSet)
            {
                s_ColorId = Shader.PropertyToID("_Color");
                s_ColorIdSet = true;
            }
            s_Block.Clear();
            s_Block.SetColor(s_ColorId, _color);
            _renderer.SetPropertyBlock(s_Block);
            _colorApplied = true;
        }

        /// <summary>Local position (metres).</summary>
        public void SetPosition(float x, float y)
        {
            _transform.localPosition = new Vector3(x, y, 0f);
        }

        /// <summary>Local size (metres) of a unit mesh: its width and height.</summary>
        public void SetSize(float width, float height)
        {
            _transform.localScale = new Vector3(width, height, 1f);
        }

        /// <summary>Local size (metres) of a unit mesh, the same in both directions.</summary>
        public void SetSize(float size)
        {
            _transform.localScale = new Vector3(size, size, 1f);
        }

        /// <summary>Turns the shape in its plane (degrees, counter-clockwise seen by the viewer).</summary>
        public void SetRoll(float degrees)
        {
            _transform.localRotation = Quaternion.Euler(0f, 0f, degrees);
        }
    }
}
