// Reticle: a small bright ring with a tiny centre dot, drawn at a fixed distance straight ahead of the head, with a radial
// progress arc that fills (clockwise from 12 o'clock) while a dwell is in progress.
//
// It is built as a mesh in code (ReticleGeometry) and drawn with the additive, unlit shader UntitledGuitarApp/Reticle
// (Resources/Shaders/Reticle.shader, loaded with Resources.Load). It is thin, the middle stays open so the target stays
// visible, it never flashes (brightness eases, and a confirmation holds steady then fades), and it ignores depth so
// nothing can hide it.
//
//   var reticle = new GameObject("Reticle").AddComponent<Reticle>();
//   reticle.Gaze = gazeDwell;     // follows gazeDwell.Head and shows the focused target's dwell progress
//
// For a dwell that is not a DwellTarget (e.g. "hold still" during neck alignment), set reticle.Progress yourself each
// frame; the arc shows the larger of Progress and the GazeDwell's focus progress. Put the GameObject at the scene root
// (or under an unscaled parent): it positions itself in world space every LateUpdate.
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Gaze
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]   // after GazeDwell (500) and after the head pose has been applied this frame
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class Reticle : MonoBehaviour
    {
        /// <summary>Path of the shader under a Resources folder: Runtime/Resources/Shaders/Reticle.shader.</summary>
        public const string ShaderResourcePath = "Shaders/Reticle";

        const string FallbackShaderName = "Sprites/Default";
        const float MaxFrameSeconds = 0.1f;
        const float RingEaseLevelsPerSecond = 6f;   // idle <-> focused brightness takes about a sixth of a second

        // ----- tuning. Public fields, so they can also be changed live in the Inspector while the glasses are on. -----

        /// <summary>Metres from the head to the reticle along the head's forward direction. Set it to about where the wearer is looking.</summary>
        public float DistanceM = 1.0f;

        /// <summary>Angular radius (degrees, seen from the head) of the ring's centre line.</summary>
        public float RingRadiusDeg = 0.75f;

        /// <summary>Angular thickness of the idle ring. About 5 pixels on the 1920 x 1080 eye image.</summary>
        public float RingWidthDeg = 0.12f;

        /// <summary>Angular thickness of the progress arc (a bolder band over the ring).</summary>
        public float ArcWidthDeg = 0.30f;

        /// <summary>Angular radius of the centre dot; 0 hides it.</summary>
        public float DotRadiusDeg = 0.08f;

        public Color RingColor = new Color(0.80f, 0.95f, 1.00f, 1f);   // cool white
        public Color ArcColor = new Color(0.45f, 1.00f, 0.60f, 1f);    // green, like a tick

        /// <summary>Ring brightness (0..1) when nothing is focused.</summary>
        public float IdleIntensity = 0.55f;

        /// <summary>Ring brightness (0..1) while a target is focused.</summary>
        public float FocusIntensity = 1.0f;

        /// <summary>After a fire or Confirm the arc stays full and steady for this long, then fades over ConfirmFadeSeconds.</summary>
        public float ConfirmHoldSeconds = 0.30f;
        public float ConfirmFadeSeconds = 0.30f;

        /// <summary>Arc fill (0..1) set by code, for a dwell that is not a DwellTarget. Combined with the Gaze's focus progress by max.</summary>
        public float Progress;

        /// <summary>False hides the reticle (e.g. in headLocked mode, where gaze selection is off).</summary>
        public bool Visible = true;

        // ----- wiring -----

        GazeDwell gaze;

        /// <summary>The GazeDwell to follow: supplies Head (unless Head is set here), the focus progress and the fire/confirm feedback.</summary>
        public GazeDwell Gaze
        {
            get { return gaze; }
            set
            {
                if (gaze == value) return;
                Unsubscribe();
                gaze = value;
                if (isActiveAndEnabled) Subscribe();
            }
        }

        /// <summary>Explicit head transform. If null, Gaze.Head is used. With neither, the reticle is hidden.</summary>
        public Transform Head { get; set; }

        // ----- state -----

        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        Mesh mesh;
        Material material;
        ReticleGeometry geometry;
        bool ready;

        float builtDistance, builtRingRadius, builtRingWidth, builtArcWidth, builtDotRadius;
        Color32 appliedRing, appliedArc;
        float ringLevel;        // current ring brightness, eased
        float confirmLeft;      // seconds of confirmation display left

        void Awake()
        {
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();

            material = CreateMaterial();
            if (material == null)
            {
                UGALog.Error("reticle_no_shader", "neither Resources/" + ShaderResourcePath + " nor " + FallbackShaderName + " could be loaded; the reticle will not draw");
                meshRenderer.enabled = false;
                enabled = false;
                return;
            }

            geometry = new ReticleGeometry();
            geometry.SetShape(DistanceM, RingRadiusDeg, RingWidthDeg, ArcWidthDeg, DotRadiusDeg);
            RememberShape();

            mesh = new Mesh { name = "UGA Reticle" };
            mesh.MarkDynamic();                      // the arc's vertices change while a dwell fills
            mesh.vertices = geometry.Vertices;       // vertices before triangles
            mesh.colors32 = geometry.Colors;
            mesh.triangles = geometry.Triangles;
            UpdateBounds();
            meshFilter.sharedMesh = mesh;

            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            meshRenderer.enabled = false;            // LateUpdate shows it once there is a head
            ringLevel = IdleIntensity;
            ready = true;
        }

        void OnEnable()
        {
            Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
        }

        void OnDestroy()
        {
            ReleaseObject(mesh);
            ReleaseObject(material);
        }

        void Subscribe()
        {
            if (gaze == null) return;
            gaze.TargetFired += OnTargetFired;
            gaze.Confirmed += OnConfirmed;
        }

        void Unsubscribe()
        {
            if (gaze == null) return;
            gaze.TargetFired -= OnTargetFired;
            gaze.Confirmed -= OnConfirmed;
        }

        void OnTargetFired(DwellTarget target) { StartConfirmation(); }

        void OnConfirmed() { StartConfirmation(); }

        void StartConfirmation()
        {
            confirmLeft = ConfirmHoldSeconds + ConfirmFadeSeconds;
        }

        void LateUpdate()
        {
            if (!ready) return;

            Transform head = Head != null ? Head : (gaze != null ? gaze.Head : null);
            bool show = Visible && head != null;
            if (meshRenderer.enabled != show) meshRenderer.enabled = show;
            if (!show) return;

            float dt = Time.unscaledDeltaTime;
            if (dt > MaxFrameSeconds) dt = MaxFrameSeconds;

            // Straight ahead of the head, facing it (local +Z = the head's forward, so the wearer sees the front).
            transform.SetPositionAndRotation(head.position + head.forward * DistanceM, head.rotation);

            // What to show: the larger of the code-set progress and the focused target's progress.
            float live = Progress > 0f ? Progress : 0f;
            bool focused = live > 0f;
            if (gaze != null)
            {
                float gazeProgress = gaze.FocusProgress;
                if (gazeProgress > live) live = gazeProgress;
                if (gaze.Focused != null) focused = true;
            }

            float arcProgress = live;
            float arcLevel = 1f;
            if (confirmLeft > 0f)
            {
                confirmLeft -= dt;
                if (confirmLeft > 0f)
                {
                    arcProgress = 1f;   // steady full circle, then a smooth fade; never a flash
                    float fade = ConfirmFadeSeconds > 0.01f ? ConfirmFadeSeconds : 0.01f;
                    arcLevel = Mathf.Clamp01(confirmLeft / fade);
                }
            }
            ringLevel = Mathf.MoveTowards(ringLevel, focused ? FocusIntensity : IdleIntensity, dt * RingEaseLevelsPerSecond);

            // Rebuild only what changed, and upload only if something did.
            bool verticesChanged = false;
            bool colorsChanged = false;

            bool shapeChanged = ShapeChanged();
            if (shapeChanged)
            {
                geometry.SetShape(DistanceM, RingRadiusDeg, RingWidthDeg, ArcWidthDeg, DotRadiusDeg);
                RememberShape();
                verticesChanged = true;
            }
            if (geometry.SetProgress(arcProgress)) verticesChanged = true;

            Color32 ring = ToColor32(RingColor, ringLevel);
            Color32 arc = ToColor32(ArcColor, arcLevel);
            if (!SameColor(ring, appliedRing)) { geometry.SetRingColor(ring); appliedRing = ring; colorsChanged = true; }
            if (!SameColor(arc, appliedArc)) { geometry.SetArcColor(arc); appliedArc = arc; colorsChanged = true; }

            if (verticesChanged) mesh.vertices = geometry.Vertices;
            if (colorsChanged) mesh.colors32 = geometry.Colors;
            // Unity recalculates a mesh's bounds only when its first vertices are assigned (Mesh.vertices docs), so they
            // are set by hand. The arc always moves inside them, so only a new shape needs new bounds.
            if (shapeChanged) UpdateBounds();
        }

        bool ShapeChanged()
        {
            return DistanceM != builtDistance || RingRadiusDeg != builtRingRadius || RingWidthDeg != builtRingWidth
                || ArcWidthDeg != builtArcWidth || DotRadiusDeg != builtDotRadius;
        }

        void RememberShape()
        {
            builtDistance = DistanceM;
            builtRingRadius = RingRadiusDeg;
            builtRingWidth = RingWidthDeg;
            builtArcWidth = ArcWidthDeg;
            builtDotRadius = DotRadiusDeg;
        }

        void UpdateBounds()
        {
            // A little larger than the drawing, so the mesh is never culled while it is on screen.
            float r = geometry.OuterRadius * 1.5f + 0.001f;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f * r, 2f * r, 0.02f));
        }

        static Color32 ToColor32(Color color, float level)
        {
            // Alpha carries brightness: the additive shader outputs rgb * a, and the fallback shader blends by it.
            return new Color32(ToByte(color.r), ToByte(color.g), ToByte(color.b), ToByte(level * color.a));
        }

        static byte ToByte(float v)
        {
            return (byte)(Mathf.Clamp01(v) * 255f + 0.5f);
        }

        static bool SameColor(Color32 a, Color32 b)
        {
            return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        }

        /// <summary>
        /// The reticle's own additive shader if it loads and is supported; otherwise Unity's built-in Sprites/Default so
        /// the reticle still shows (alpha-blended, depth-tested) while the shader is being fixed. The warning says which.
        /// </summary>
        static Material CreateMaterial()
        {
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader == null || !shader.isSupported)
            {
                UGALog.Warn("reticle_shader", "Resources/" + ShaderResourcePath + " is missing or unsupported (shader compile error? see the Console); falling back to " + FallbackShaderName);
                shader = Shader.Find(FallbackShaderName);
            }
            if (shader == null) return null;

            var m = new Material(shader);
            m.name = "UGA Reticle";
            m.hideFlags = HideFlags.DontSave;
            m.renderQueue = (int)RenderQueue.Overlay;   // draw last
            return m;
        }

        static void ReleaseObject(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
