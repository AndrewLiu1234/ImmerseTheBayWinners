// CapsuleHand: the always-correct ghost hand. A stylized hand built from code (a palm plus five fingers, each finger one
// continuous skin over three phalanges) and posed by inverse kinematics from fingertip targets. "Still a hand, never dots only."
//
// USE (a fretting hand on a neck; `neck` is a transform in the pinned neck frame: origin at the nut on the fretboard,
// +x toward the bridge, +y out of the fretboard, +z toward string 1; positions in metres):
//     CapsuleHand hand = CapsuleHand.Create(neck, leftHand: true);
//     hand.SetFingertipTargets(dots);        // Vector3[4]: fingers 1-4; x = float.NaN means "finger not used"
//     hand.SetActiveFinger(2);               // finger 2 at full brightness, the rest at 30%;  CapsuleHand.AllFingers = all
//     hand.SetFade(0.4f);                    // after success;  SetFade(1f) to bring it back
// A free hand (the strumming hand) is placed with SetWristPose(position, rotation) instead of fingertip targets.
//
// HIERARCHY (built once, never rebuilt):
//     <hand root>            stays at the parent's origin (identity), so "wrist" below is expressed in the parent's frame
//       wrist                the palm's pose and the hand's scale
//         thumb_proximal > thumb_intermediate > thumb_distal        (and the same for index, middle, ring, pinky)
//         mesh_palm, mesh_index, mesh_middle, mesh_ring, mesh_pinky, mesh_thumb     (MeshRenderers, one per part)
//   Each bone's local +z runs along the bone toward the next joint and +y is the back of the finger. The meshes are
//   redrawn from the bone transforms every LateUpdate (only when a bone moved), so anything that rotates the named bones
//   (an authored pose, for example) moves the visible hand too. Set driveBones = false to stop the built-in IK writing them.
//
// COST: one shared material, one MaterialPropertyBlock per part, six draw calls, about 4,700 triangles. Nothing is
// allocated after Create: a pose change rewrites vertex arrays in place and uploads them to meshes that already exist.
//
// APIS (Unity 2021.3 Scripting Reference, local copy Unity.app/Contents/Documentation/en/ScriptReference/<page>.html,
// online https://docs.unity3d.com/2021.3/Documentation/ScriptReference/<page>.html):
//   Resources.Load            "public static T Load(string path)": "the path ... must not contain a file extension"
//   Material-ctor             "public Material(Shader shader)"
//   Shader-isSupported        "Can this shader run on the end-users graphics card?"
//   MaterialPropertyBlock     "used by Graphics.DrawMesh and Renderer.SetPropertyBlock ... The block passed ... is copied"
//   Renderer.SetPropertyBlock "public void SetPropertyBlock(MaterialPropertyBlock properties)"
//   Mesh.SetVertices / Mesh.SetNormals   "public void SetVertices(Vector3[] inVertices)", "public void SetNormals(Vector3[] inNormals)"
//   Mesh.MarkDynamic          "Optimize mesh for frequent updates."
//   Mesh-bounds, Mesh.RecalculateBounds  "After modifying vertices you should call this function ..." (here the bounds are
//                             set to a fixed box instead, so the hand is never culled by mistake)
//   GL                        the documented built-in "Hidden/Internal-Colored" shader with _SrcBlend/_DstBlend/_Cull/_ZWrite,
//                             used ONLY as an emergency look if the ghost shader is missing or fails to compile.
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    [DisallowMultipleComponent]
    public sealed class CapsuleHand : MonoBehaviour
    {
        /// <summary>Pass to SetActiveFinger to light the whole hand.</summary>
        public const int AllFingers = -1;
        public const string ShaderResourcePath = "Shaders/GhostHand";

        const int FingerCount = CapsuleHandDimensions.FingerCount;      // index, middle, ring, little, thumb
        const int ThumbIndex = CapsuleHandDimensions.ThumbIndex;
        const int PartCount = 6;            // 0 = palm, 1-4 = fingers 1-4, 5 = thumb  (part = finger index + 1)
        const int FingerAround = 20;        // vertices around a finger
        const int PalmAround = 28;
        const int ElbowSteps = 4;
        const int CapRows = 4;
        const int FadeRows = 3;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int RimColorId = Shader.PropertyToID("_RimColor");
        static readonly int TipColorId = Shader.PropertyToID("_TipColor");
        static readonly int FillId = Shader.PropertyToID("_Fill");
        static readonly int EmphasisId = Shader.PropertyToID("_Emphasis");
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        static readonly Color DefaultBlue = new Color(0.310f, 0.765f, 0.969f, 1f);      // #4FC3F7
        static readonly Color DefaultTip = new Color(0.86f, 0.96f, 1f, 1f);
        static readonly Color[] FingerColors =                                          // fingers 1-4 (flag ghost.fingerColors)
        {
            new Color(1.00f, 0.92f, 0.23f, 1f),     // 1 yellow
            new Color(1.00f, 0.25f, 0.85f, 1f),     // 2 magenta
            new Color(0.70f, 1.00f, 0.20f, 1f),     // 3 lime
            new Color(1.00f, 0.60f, 0.10f, 1f),     // 4 orange
        };
        static readonly string[] PartNames = { "mesh_palm", "mesh_index", "mesh_middle", "mesh_ring", "mesh_pinky", "mesh_thumb" };
        static readonly Bounds FixedBounds = new Bounds(new Vector3(0f, 0f, 0.06f), new Vector3(0.5f, 0.5f, 0.5f));    // wrist space

        static Material s_material;         // shared by every capsule hand
        static bool s_fallbackLook;         // true when the ghost shader could not be used

        /// <summary>How long a move between two target sets (or two brightness settings) takes, in seconds.</summary>
        public float morphSeconds = 0.28f;
        /// <summary>Brightness of the parts that are not the active finger.</summary>
        public float dimmedEmphasis = 0.3f;
        /// <summary>False = the built-in IK stops writing the bone transforms (another script poses the named bones).</summary>
        public bool driveBones = true;

        [SerializeField] bool m_leftHand = true;
        [SerializeField] float m_scale = 1f;

        bool m_built;
        CapsuleHandSolver m_solver;
        Transform m_wrist;
        readonly Transform[] m_bones = new Transform[FingerCount * 3];          // finger index * 3 + joint
        readonly MeshRenderer[] m_renderers = new MeshRenderer[PartCount];
        readonly Mesh[] m_meshes = new Mesh[PartCount];
        readonly GhostTubeGeometry[] m_tubes = new GhostTubeGeometry[FingerCount];
        MaterialPropertyBlock[] m_blocks;

        // pose animation
        HandPose m_from, m_to, m_now;
        float m_poseT = 1f;
        readonly FingerIkResult[] m_results = new FingerIkResult[FingerCount];
        readonly Vector3[] m_targets = new Vector3[4];
        bool m_fretting = true;
        Vector3 m_freePosition;
        Quaternion m_freeRotation = Quaternion.identity;

        // skin (mesh) state
        readonly Vector3[] m_points = new Vector3[5];
        readonly float[] m_radii = new float[5];
        readonly Vector3[] m_drawn = new Vector3[FingerCount * 4];              // the joint positions each skin was last built from
        bool m_skinValid;

        // look animation
        readonly float[] m_emphasisFrom = new float[PartCount];
        readonly float[] m_emphasisTo = new float[PartCount];
        readonly float[] m_emphasisNow = new float[PartCount];
        readonly Color[] m_tipColors = new Color[PartCount];
        float m_fadeFrom = 1f, m_fadeTo = 1f, m_fadeNow = 1f;
        float m_lookT = 1f;
        int m_activeFinger = AllFingers;

        // ------------------------------------------------------------------------------------------------ creation

        /// <summary>Create a capsule hand under <paramref name="parent"/> (null = scene root). leftHand = true builds a left
        /// hand, whose palm sits on the +z side of a neck in the pinned frame; false builds its mirror image.</summary>
        public static CapsuleHand Create(Transform parent, bool leftHand)
        {
            GameObject go = new GameObject(leftHand ? "GhostHand_Left" : "GhostHand_Right");
            go.transform.SetParent(parent, false);
            if (parent != null) go.layer = parent.gameObject.layer;
            CapsuleHand hand = go.AddComponent<CapsuleHand>();
            hand.Build(leftHand);
            return hand;
        }

        public bool IsLeftHand { get { return m_leftHand; } }

        /// <summary>The placement solver: neck depth, hover height, palm tilt and so on. Change a value, then set the targets again.</summary>
        public CapsuleHandSolver Solver { get { EnsureBuilt(); return m_solver; } }

        /// <summary>The "wrist" bone: the palm's pose in the parent's frame, and the hand's scale.</summary>
        public Transform Wrist { get { EnsureBuilt(); return m_wrist; } }

        /// <summary>True when the ghost shader was missing or broken and the plain emergency look is in use.</summary>
        public static bool UsingFallbackLook { get { return s_fallbackLook; } }

        /// <summary>Uniform size of the hand; 1 = adult. The fingertips still land on their targets.</summary>
        public float scale
        {
            get { return m_scale; }
            set
            {
                float v = CapsuleHandMath.IsFinite(value) ? Mathf.Clamp(value, 0.05f, 20f) : 1f;
                if (v == m_scale && m_built) return;
                m_scale = v;
                if (!m_built) return;
                m_solver.scale = v;
                Resolve(true);
            }
        }

        /// <summary>A finger bone. fingerNumber: 1 = index .. 4 = pinky, 5 = thumb. joint: 0 = proximal, 1 = intermediate, 2 = distal.</summary>
        public Transform GetBone(int fingerNumber, int joint)
        {
            EnsureBuilt();
            if (fingerNumber < 1 || fingerNumber > 5 || joint < 0 || joint > 2) return null;
            return m_bones[(fingerNumber - 1) * 3 + joint];
        }

        void EnsureBuilt()
        {
            if (!m_built) Build(m_leftHand);
        }

        void Build(bool leftHand)
        {
            if (m_built) return;
            m_built = true;
            m_leftHand = leftHand;
            m_solver = new CapsuleHandSolver(leftHand);
            m_solver.scale = m_scale;
            CapsuleHandDimensions dims = m_solver.dims;
            Material material = SharedMaterial();
            int layer = gameObject.layer;

            // ---- bones (the pinned names) ----
            m_wrist = NewChild(CapsuleHandDimensions.BoneNames[0], transform, layer);
            for (int f = 0; f < FingerCount; f++)
            {
                Transform proximal = NewChild(CapsuleHandDimensions.BoneName(f, 0), m_wrist, layer);
                Transform intermediate = NewChild(CapsuleHandDimensions.BoneName(f, 1), proximal, layer);
                Transform distal = NewChild(CapsuleHandDimensions.BoneName(f, 2), intermediate, layer);
                proximal.localPosition = dims.chains[f].knuckle;
                intermediate.localPosition = new Vector3(0f, 0f, dims.chains[f].length1);
                distal.localPosition = new Vector3(0f, 0f, dims.chains[f].length2);
                m_bones[f * 3] = proximal; m_bones[f * 3 + 1] = intermediate; m_bones[f * 3 + 2] = distal;
            }

            // ---- look state ----
            m_blocks = new MaterialPropertyBlock[PartCount];
            for (int p = 0; p < PartCount; p++)
            {
                m_blocks[p] = new MaterialPropertyBlock();
                m_emphasisFrom[p] = m_emphasisTo[p] = m_emphasisNow[p] = 1f;
                m_tipColors[p] = DefaultTip;
            }

            // ---- the palm: built once ----
            {
                Vector3[] v; Vector3[] n; Color32[] c; int[] t;
                GhostPalmGeometry.Build(dims, PalmAround, out v, out n, out c, out t);
                Mesh mesh = new Mesh();
                mesh.name = "GhostHand palm";
                mesh.SetVertices(v);
                mesh.SetNormals(n);
                mesh.colors32 = s_fallbackLook ? BakeFallbackColors(c) : c;
                mesh.triangles = t;
                mesh.bounds = FixedBounds;
                m_meshes[0] = mesh;
                m_renderers[0] = NewRenderer(PartNames[0], mesh, material, layer);
            }

            // ---- the fingers: one skin each; the topology is fixed, the vertices follow the bones ----
            for (int f = 0; f < FingerCount; f++)
            {
                bool thumb = f == ThumbIndex;
                GhostTubeGeometry tube = new GhostTubeGeometry(thumb ? 4 : 5, FingerAround, ElbowSteps, CapRows, FadeRows, thumb ? 0.50f : 0.55f, 0.5f);
                m_tubes[f] = tube;
                Mesh mesh = new Mesh();
                mesh.name = "GhostHand " + PartNames[f + 1];
                mesh.MarkDynamic();
                mesh.SetVertices(tube.vertices);
                mesh.SetNormals(tube.normals);
                mesh.colors32 = s_fallbackLook ? BakeFallbackColors(tube.colors) : tube.colors;
                mesh.triangles = tube.triangles;
                mesh.bounds = FixedBounds;
                m_meshes[f + 1] = mesh;
                m_renderers[f + 1] = NewRenderer(PartNames[f + 1], mesh, material, layer);
            }

            ApplyFlags(FlagStore.Current);

            // ---- start as a fretting hand at rest: fingers hovering, thumb behind the neck ----
            for (int i = 0; i < 4; i++) m_targets[i] = new Vector3(float.NaN, 0f, 0f);
            m_fretting = true;
            Resolve(true);
            PushBlocks();
        }

        static Transform NewChild(string childName, Transform parent, int layer)
        {
            GameObject go = new GameObject(childName);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        MeshRenderer NewRenderer(string partName, Mesh mesh, Material material, int layer)
        {
            GameObject go = new GameObject(partName);
            go.layer = layer;
            go.transform.SetParent(m_wrist, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        /// <summary>Put the hand and all its parts on a layer (for per-camera culling masks).</summary>
        public void SetLayer(int layer)
        {
            EnsureBuilt();
            gameObject.layer = layer;
            m_wrist.gameObject.layer = layer;
            for (int i = 0; i < m_bones.Length; i++) m_bones[i].gameObject.layer = layer;
            for (int p = 0; p < PartCount; p++) m_renderers[p].gameObject.layer = layer;
        }

        // ------------------------------------------------------------------------------------------------ material

        static Material SharedMaterial()
        {
            if (s_material != null) return s_material;
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            s_fallbackLook = shader == null || !shader.isSupported;
            if (s_fallbackLook)
            {
                UGALog.Error("ghost_shader_unusable", shader == null
                    ? "Resources/Shaders/GhostHand.shader not found; the capsule hand uses a plain additive look"
                    : "Resources/Shaders/GhostHand.shader is not supported (compile error?); the capsule hand uses a plain additive look");
                shader = Shader.Find("Hidden/Internal-Colored");
            }
            Material m = new Material(shader);
            m.name = "GhostHand (shared)";
            m.hideFlags = HideFlags.HideAndDontSave;
            if (s_fallbackLook)
            {   // additive, no depth writes, back faces culled: the property names are the ones the GL page documents
                m.SetInt("_SrcBlend", (int)BlendMode.One);
                m.SetInt("_DstBlend", (int)BlendMode.One);
                m.SetInt("_Cull", (int)CullMode.Back);
                m.SetInt("_ZWrite", 0);
            }
            s_material = m;
            return m;
        }

        // The emergency shader only shows vertex colours, so turn the masks into real colours: faint blue, bright tips.
        static Color32[] BakeFallbackColors(Color32[] masks)
        {
            Color32[] baked = new Color32[masks.Length];
            for (int i = 0; i < masks.Length; i++)
            {
                float tip = Mathf.Clamp01((masks[i].r - masks[i].g) / 255f);
                float cover = 1f - Mathf.Clamp01((masks[i].b - masks[i].g) / 255f);
                Color c = Color.Lerp(DefaultBlue * 0.38f, DefaultTip, tip) * cover;
                c.a = 1f;
                baked[i] = c;
            }
            return baked;
        }

        void ApplyFlags(FlagSet flags)
        {
            Color baseColor = DefaultBlue;
            float fill = 0.2f;
            bool fingerColors = false;
            if (flags != null && flags.ghost != null)
            {
                Color parsed;
                if (!string.IsNullOrEmpty(flags.ghost.color) && ColorUtility.TryParseHtmlString(flags.ghost.color, out parsed)) baseColor = parsed;
                if (CapsuleHandMath.IsFinite(flags.ghost.fill)) fill = Mathf.Clamp01(flags.ghost.fill);
                fingerColors = flags.ghost.fingerColors;
            }
            Material m = SharedMaterial();
            if (!s_fallbackLook)
            {
                m.SetColor(ColorId, baseColor);
                m.SetColor(RimColorId, Color.Lerp(baseColor, Color.white, 0.57f));      // #4FC3F7 -> #B3E5FC
                m.SetFloat(FillId, fill);
            }
            for (int p = 0; p < PartCount; p++)
                m_tipColors[p] = fingerColors && p >= 1 && p <= 4 ? FingerColors[p - 1] : DefaultTip;
            if (m_built && m_blocks != null) PushBlocks();
        }

        void OnFlagsChanged(FlagSet flags)
        {
            if (m_built) ApplyFlags(flags);
        }

        void PushBlocks()
        {
            for (int p = 0; p < PartCount; p++)
            {
                MaterialPropertyBlock block = m_blocks[p];
                if (s_fallbackLook)
                {
                    float k = m_emphasisNow[p] * m_fadeNow;
                    block.SetColor(ColorId, new Color(k, k, k, 1f));
                }
                else
                {
                    block.SetFloat(EmphasisId, m_emphasisNow[p]);
                    block.SetFloat(FadeId, m_fadeNow);
                    block.SetColor(TipColorId, m_tipColors[p]);
                }
                m_renderers[p].SetPropertyBlock(block);
            }
        }

        // ------------------------------------------------------------------------------------------------ public controls

        /// <summary>
        /// Where the fingertips go. targets[0..3] = fingers 1-4 (index, middle, ring, pinky), in the parent's local space, in
        /// metres: the point each fingertip pad should touch. A target whose x is NaN means "finger not used" (it hovers).
        /// The thumb rests behind the neck. The hand eases to the new set over morphSeconds.
        /// </summary>
        public void SetFingertipTargets(Vector3[] targets)
        {
            SetFingertipTargets(targets, false);
        }

        /// <summary>As above; immediate = true jumps to the new pose at once (first placement, edit-mode previews, tests).</summary>
        public void SetFingertipTargets(Vector3[] targets, bool immediate)
        {
            EnsureBuilt();
            for (int i = 0; i < 4; i++)
                m_targets[i] = targets != null && i < targets.Length ? targets[i] : new Vector3(float.NaN, 0f, 0f);
            m_fretting = true;
            Resolve(immediate);
        }

        /// <summary>Place a free hand (the strumming hand): the wrist's position and rotation in the parent's local space,
        /// fingers relaxed. Call it every frame to animate the hand; immediate = false eases to the pose instead.</summary>
        public void SetWristPose(Vector3 position, Quaternion rotation, bool immediate = true)
        {
            EnsureBuilt();
            m_fretting = false;
            m_freePosition = position;
            m_freeRotation = rotation;
            Resolve(immediate);
        }

        /// <summary>1 = normal, 0.4 = "fade to 40% after success". Eases over morphSeconds.</summary>
        public void SetFade(float fade)
        {
            EnsureBuilt();
            float v = CapsuleHandMath.IsFinite(fade) ? Mathf.Clamp01(fade) : 1f;
            if (v == m_fadeTo) return;
            StartLookChange();
            m_fadeTo = v;
        }

        /// <summary>fingerNumber 1-4 (1 = index .. 4 = pinky): that finger at full brightness, everything else at
        /// dimmedEmphasis (30%). AllFingers (-1), or any other number, lights the whole hand. Eases over morphSeconds.</summary>
        public void SetActiveFinger(int fingerNumber)
        {
            EnsureBuilt();
            int active = fingerNumber >= 1 && fingerNumber <= 4 ? fingerNumber : AllFingers;
            if (active == m_activeFinger) return;
            m_activeFinger = active;
            StartLookChange();
            for (int p = 0; p < PartCount; p++)
                m_emphasisTo[p] = active == AllFingers || p == active ? 1f : Mathf.Clamp01(dimmedEmphasis);
        }

        public int ActiveFinger { get { return m_activeFinger; } }

        /// <summary>Where the pad of a fingertip is right now, in the parent's local space (fingerNumber 1-4, 5 = thumb).
        /// With the default hover it floats Solver.fingertipHover above its target.</summary>
        public Vector3 GetFingertipPad(int fingerNumber)
        {
            EnsureBuilt();
            int f = Mathf.Clamp(fingerNumber, 1, 5) - 1;
            Vector3 centre = m_solver.PalmToParent(m_now, m_results[f].tip);
            return centre - m_solver.Normal * (m_solver.dims.TipRadius(f) * m_scale);
        }

        /// <summary>How far (metres) a finger's final pose misses its target; 0 when the target is reached (fingerNumber 1-4, 5 = thumb).</summary>
        public float GetReachError(int fingerNumber)
        {
            EnsureBuilt();
            return m_solver.reachError[Mathf.Clamp(fingerNumber, 1, 5) - 1];
        }

        /// <summary>Advance the eases by dt seconds. Update() calls this; call Tick(1f) yourself to finish every ease at
        /// once where Update does not run (edit mode, tests).</summary>
        public void Tick(float dt)
        {
            EnsureBuilt();
            if (!(dt > 0f)) dt = 0f;
            float step = dt / Mathf.Max(morphSeconds, 1e-3f);
            if (m_poseT < 1f)
            {
                m_poseT = Mathf.Min(1f, m_poseT + step);
                HandPose.Lerp(m_from, m_to, Smooth(m_poseT), out m_now);
                ApplyPose();
            }
            if (m_lookT < 1f)
            {
                m_lookT = Mathf.Min(1f, m_lookT + step);
                float e = Smooth(m_lookT);
                for (int p = 0; p < PartCount; p++) m_emphasisNow[p] = Mathf.Lerp(m_emphasisFrom[p], m_emphasisTo[p], e);
                m_fadeNow = Mathf.Lerp(m_fadeFrom, m_fadeTo, e);
                PushBlocks();
            }
            UpdateSkins();
        }

        // ------------------------------------------------------------------------------------------------ internals

        static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        void StartLookChange()
        {
            for (int p = 0; p < PartCount; p++) m_emphasisFrom[p] = m_emphasisNow[p];
            m_fadeFrom = m_fadeNow;
            m_lookT = 0f;
        }

        // Solve the pose for the current targets (or the free wrist pose) and go there.
        void Resolve(bool immediate)
        {
            HandPose to;
            if (m_fretting) m_solver.SolveFretting(m_targets, out to);
            else m_solver.SolveFree(m_freePosition, m_freeRotation, out to);

            if (immediate || !(morphSeconds > 0f))
            {
                m_from = m_to = m_now = to;
                m_poseT = 1f;
                ApplyPose();
                UpdateSkins();
            }
            else
            {
                m_from = m_now;
                m_to = to;
                m_poseT = 0f;
            }
        }

        // Inverse kinematics for the current pose, written to the wrist and the fifteen finger bones.
        void ApplyPose()
        {
            m_solver.SolveFingers(m_now, m_results);
            if (!driveBones) return;

            m_wrist.localPosition = m_now.palmPosition;
            m_wrist.localRotation = m_now.palmRotation;
            m_wrist.localScale = new Vector3(m_scale, m_scale, m_scale);

            CapsuleHandDimensions dims = m_solver.dims;
            for (int f = 0; f < FingerCount; f++)
            {
                FingerIkResult r = m_results[f];
                Vector3 back = -dims.chains[f].curl;                    // the back of the finger: opposite the way it curls
                Vector3 d1 = r.pip - r.knuckle, d2 = r.dip - r.pip, d3 = r.tip - r.dip;
                Quaternion q1 = CapsuleHandMath.LookRotationSafe(d1, back);
                Quaternion q2 = CapsuleHandMath.LookRotationSafe(d2, back);
                Quaternion q3 = CapsuleHandMath.LookRotationSafe(d3, back);
                Transform proximal = m_bones[f * 3], intermediate = m_bones[f * 3 + 1], distal = m_bones[f * 3 + 2];
                proximal.localPosition = r.knuckle;
                proximal.localRotation = q1;
                intermediate.localRotation = CapsuleHandMath.Conjugate(q1) * q2;
                distal.localRotation = CapsuleHandMath.Conjugate(q2) * q3;
            }
        }

        // Redraw every finger whose bones moved since its skin was last built. Reads the bone transforms, so it follows
        // whatever poses them. No allocation.
        void UpdateSkins()
        {
            CapsuleHandDimensions dims = m_solver.dims;
            for (int f = 0; f < FingerCount; f++)
            {
                Transform proximal = m_bones[f * 3], intermediate = m_bones[f * 3 + 1], distal = m_bones[f * 3 + 2];
                Vector3 knuckle = proximal.localPosition;
                Quaternion q1 = proximal.localRotation;
                Vector3 pip = knuckle + q1 * intermediate.localPosition;
                Quaternion q2 = q1 * intermediate.localRotation;
                Vector3 dip = pip + q2 * distal.localPosition;
                Quaternion q3 = q2 * distal.localRotation;
                Vector3 tip = dip + q3 * new Vector3(0f, 0f, dims.chains[f].length3);

                int c = f * 4;
                if (m_skinValid && knuckle == m_drawn[c] && pip == m_drawn[c + 1] && dip == m_drawn[c + 2] && tip == m_drawn[c + 3]) continue;
                m_drawn[c] = knuckle; m_drawn[c + 1] = pip; m_drawn[c + 2] = dip; m_drawn[c + 3] = tip;

                Vector3 upHint;
                if (f == ThumbIndex)
                {
                    m_points[0] = knuckle; m_points[1] = pip; m_points[2] = dip; m_points[3] = tip;
                    m_radii[0] = dims.Radius(f, 0); m_radii[1] = dims.Radius(f, 1); m_radii[2] = dims.Radius(f, 2); m_radii[3] = dims.Radius(f, 3);
                    upHint = -dims.chains[f].curl;
                }
                else
                {
                    m_points[0] = dims.skinStart[f]; m_points[1] = knuckle; m_points[2] = pip; m_points[3] = dip; m_points[4] = tip;
                    m_radii[0] = dims.skinStartRadius[f]; m_radii[1] = dims.Radius(f, 0); m_radii[2] = dims.Radius(f, 1);
                    m_radii[3] = dims.Radius(f, 2); m_radii[4] = dims.Radius(f, 3);
                    upHint = Vector3.up;
                }

                GhostTubeGeometry tube = m_tubes[f];
                tube.Update(m_points, m_radii, upHint);
                Mesh mesh = m_meshes[f + 1];
                mesh.SetVertices(tube.vertices);
                mesh.SetNormals(tube.normals);
                mesh.bounds = FixedBounds;
            }
            m_skinValid = true;
        }

        // ------------------------------------------------------------------------------------------------ Unity messages

        void OnEnable()
        {
            FlagStore.Changed += OnFlagsChanged;
        }

        void OnDisable()
        {
            FlagStore.Changed -= OnFlagsChanged;
        }

        void Update()
        {
            Tick(Time.deltaTime);
        }

        void LateUpdate()
        {
            if (m_built) UpdateSkins();         // picks up bones moved by other scripts this frame
        }

        void OnDestroy()
        {
            for (int p = 0; p < PartCount; p++)
            {
                if (m_meshes[p] == null) continue;
                if (Application.isPlaying) Destroy(m_meshes[p]); else DestroyImmediate(m_meshes[p]);
                m_meshes[p] = null;
            }
        }
    }
}
