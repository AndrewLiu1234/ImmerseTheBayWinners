// PosedGhostHand: the Blender-modelled ghost hand, posed from the pose files. Loads the FBX from Resources, gives every
// renderer the ghost look (one shared UntitledGuitarApp/GhostHand material, fade and emphasis per renderer through a
// MaterialPropertyBlock) and drives its 16 bones with a GhostPoseDriver.
//
// USE (the fretting hand on the neck; FloatingNeck.Socket is the neck frame: origin at the nut on the fretboard, +x toward
// the bridge, +y out of the fretboard, +z toward string 1, metres; pose positions are neck-frame millimetres):
//     PosedGhostHand fretting = PosedGhostHand.Create(neck.Socket, "fretting", library);
//     if (fretting.IsAvailable) { capsule.gameObject.SetActive(false); fretting.SetPose("G", true); }
//     else { Destroy(fretting.gameObject); }                // keep the CapsuleHand: still a hand, never dots only
//     fretting.SetPose("C", false);                          // one beat before the change: eased over morphSeconds (0.28 s)
//     fretting.SetFade(flags.ghost.fadeAfterSuccess);         // after audio confirms the chord; SetFade(1f) to bring it back
// When the FBX, a bone, the pose files or the shader are missing, or the rig does not match the poses' bind data,
// Create logs ONE UGALog warning ("ghost_hand_posed_unavailable"), IsAvailable is false and nothing is drawn.
//
// HIERARCHY:  PosedGhostHand_<hand> (this; stays where the caller puts it: identity under the socket)
//               <the FBX instance>  the driver owns its local position, rotation and scale and its 16 bones' rotations
//
// COST: one shared material for every posed hand, one property block, the FBX's own renderers (one skinned mesh of about
// 5,400 triangles per hand). Nothing is allocated after Create; Update only eases (Tick) while something moves.
//
// APIS (Unity 2021.3 Scripting Reference, local copy Unity.app/Contents/Documentation/en/ScriptReference/<page>.html):
//   Resources.Load            "public static T Load(string path)": the path is relative to a Resources folder, no extension
//   Object.Instantiate        "public static Object Instantiate(Object original, Transform parent, bool instantiateInWorldSpace)":
//                             "Clones the object original and returns the clone ... all child objects and components are also cloned"
//   Component.GetComponentsInChildren  "public T[] GetComponentsInChildren(bool includeInactive)"
//   Renderer-sharedMaterials  "All the shared materials of this object"
//   Renderer.SetPropertyBlock "public void SetPropertyBlock(MaterialPropertyBlock properties)": "Lets you set or clear
//                             per-renderer or per-material parameter overrides"
//   Behaviour-enabled         "Enabled Behaviours are Updated, disabled Behaviours are not" (an imported Animator is switched off
//                             so it can never write the bones)
//   Material-ctor, Shader-isSupported, Shader.PropertyToID   as in CapsuleHand.cs
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    [DisallowMultipleComponent]
    public sealed class PosedGhostHand : MonoBehaviour
    {
        /// <summary>Resources folder of the two FBX files (Art/Resources/GhostHand/ghost_hand_fretting.fbx, ..._strumming.fbx).</summary>
        public const string ResourceFolder = "GhostHand";
        public const string ShaderResourcePath = "Shaders/GhostHand";
        /// <summary>The plan: morph to the next chord one beat early, over 250-300 ms.</summary>
        public const float DefaultMorphSeconds = 0.28f;
        /// <summary>The pose shown right after Create (falls back to any pose of the hand).</summary>
        public const string RestPose = "rest";

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int RimColorId = Shader.PropertyToID("_RimColor");
        static readonly int TipColorId = Shader.PropertyToID("_TipColor");
        static readonly int FillId = Shader.PropertyToID("_Fill");
        static readonly int EmphasisId = Shader.PropertyToID("_Emphasis");
        static readonly int FadeId = Shader.PropertyToID("_Fade");
        static readonly Color DefaultBlue = new Color(0.310f, 0.765f, 0.969f, 1f);      // #4FC3F7, as CapsuleHand
        static readonly Color DefaultTip = new Color(0.86f, 0.96f, 1f, 1f);
        static readonly Renderer[] s_noRenderers = new Renderer[0];

        static Material s_material;     // shared by every posed hand

        /// <summary>How long SetPose(name, false), SetFade and SetEmphasis take to ease, in seconds.</summary>
        public float morphSeconds = DefaultMorphSeconds;

        string _hand;
        GhostPoseLibrary _library;
        GhostPoseDriver _driver;
        GameObject _model;
        Renderer[] _renderers = s_noRenderers;
        MaterialPropertyBlock _block;
        bool _available;
        string _reason;
        string _currentPose;
        readonly System.Collections.Generic.HashSet<string> _missingPoses = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        float _fadeFrom = 1f, _fadeTo = 1f, _fadeNow = 1f;
        float _emphasisFrom = 1f, _emphasisTo = 1f, _emphasisNow = 1f;
        float _lookT = 1f;

        // ------------------------------------------------------------------------------------------------ creation

        /// <summary>The Resources path of a hand's FBX: "GhostHand/ghost_hand_fretting".</summary>
        public static string ResourcePathFor(string hand)
        {
            return ResourceFolder + "/ghost_hand_" + hand;
        }

        /// <summary>
        /// Creates the posed ghost hand under <paramref name="parent"/> (the neck frame) for <paramref name="hand"/>
        /// ("fretting" or "strumming"), loading Resources/GhostHand/ghost_hand_&lt;hand&gt;. <paramref name="library"/> supplies
        /// the poses (null = a new GhostPoseLibrary on StreamingAssets/poses). Never returns null: check IsAvailable.
        /// </summary>
        public static PosedGhostHand Create(Transform parent, string hand, GhostPoseLibrary library)
        {
            GameObject model = null;
            if (GhostPose.IsKnownHand(hand))
            {
                try
                {
                    model = Resources.Load<GameObject>(ResourcePathFor(hand));
                }
                catch (Exception)
                {
                    model = null;   // reported below as "not found"
                }
            }
            return Create(parent, hand, library, model);
        }

        /// <summary>As Create, with the model given directly instead of loaded from Resources (tests, previews).</summary>
        public static PosedGhostHand Create(Transform parent, string hand, GhostPoseLibrary library, GameObject model)
        {
            var go = new GameObject("PosedGhostHand_" + (GhostPose.IsKnownHand(hand) ? hand : "unknown"));
            go.transform.SetParent(parent, false);
            if (parent != null) go.layer = parent.gameObject.layer;
            PosedGhostHand posed = go.AddComponent<PosedGhostHand>();
            posed.Build(hand, library, model);
            return posed;
        }

        void Build(string hand, GhostPoseLibrary library, GameObject model)
        {
            _hand = hand;
            _block = new MaterialPropertyBlock();
            string reason;
            try
            {
                reason = TryBuild(hand, library, model);
            }
            catch (Exception e)
            {
                reason = "unexpected " + e.GetType().Name + ": " + e.Message;
            }
            if (reason != null)
            {
                Fail(reason);
                return;
            }
            _available = true;
            UGALog.Event("ghost_hand_posed_ready", "hand", hand, "fitErrorMm", _driver.CalibrationErrorMm, "renderers", _renderers.Length);
        }

        // Null on success, otherwise what is wrong (one sentence for the one warning).
        string TryBuild(string hand, GhostPoseLibrary library, GameObject model)
        {
            if (!GhostPose.IsKnownHand(hand)) return "hand must be '" + GhostPose.HandFretting + "' or '" + GhostPose.HandStrumming + "', not '" + hand + "'";
            _library = library ?? new GhostPoseLibrary();
            GhostPose reference = ReferencePose(_library, hand);
            if (reference == null) return "no " + hand + " pose with a bind block is loaded from " + _library.Folder;
            if (model == null) return "Resources/" + ResourcePathFor(hand) + " was not found (the FBX is not imported yet)";

            _model = Instantiate(model, transform, false) as GameObject;
            if (_model == null) return "the model could not be instantiated";
            _model.name = model.name;
            SwitchOffAnimation(_model);
            SetLayerRecursively(_model.transform, gameObject.layer);

            // the rig first (bones, fit to the poses' bind data), then the look, so the reason names the real problem
            _driver = new GhostPoseDriver();
            if (!_driver.Bind(_model.transform)) return _driver.Problem;
            if (!_driver.Calibrate(reference)) return _driver.Problem;

            _renderers = _model.GetComponentsInChildren<Renderer>(true);
            if (_renderers.Length == 0) return "the model has no renderer";
            Material material = SharedMaterial();
            if (material == null) return "Resources/" + ShaderResourcePath + ".shader is missing or not supported on this GPU";
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                int slots = Mathf.Max(1, r.sharedMaterials.Length);
                var materials = new Material[slots];
                for (int k = 0; k < slots; k++) materials[k] = material;
                r.sharedMaterials = materials;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            if (!_driver.Apply(reference)) return "the pose " + reference.Name + " could not be applied";
            _currentPose = reference.Name;
            PushBlock();
            return null;
        }

        void Fail(string reason)
        {
            _available = false;
            _reason = reason;
            if (_model != null)
            {
                _model.SetActive(false);   // Destroy is deferred in Play Mode: hide it now
                if (Application.isPlaying) Destroy(_model); else DestroyImmediate(_model);
                _model = null;
            }
            _renderers = s_noRenderers;
            UGALog.Warn("ghost_hand_posed_unavailable", (_hand ?? "(null)") + " hand: " + reason + ". The capsule hand stays in use.");
        }

        static GhostPose ReferencePose(GhostPoseLibrary library, string hand)
        {
            GhostPose pose;
            if (library.TryGet(hand, RestPose, out pose) && pose.HasBind) return pose;
            var poses = library.Poses;
            for (int i = 0; i < poses.Count; i++)
                if (string.Equals(poses[i].Hand, hand, StringComparison.Ordinal) && poses[i].HasBind) return poses[i];
            return null;
        }

        static void SwitchOffAnimation(GameObject model)
        {
            Animator[] animators = model.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++) animators[i].enabled = false;
            Animation[] animations = model.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < animations.Length; i++) animations[i].enabled = false;
        }

        static void SetLayerRecursively(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursively(t.GetChild(i), layer);
        }

        // ------------------------------------------------------------------------------------------------ material

        static Material SharedMaterial()
        {
            if (s_material != null) return s_material;
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader == null || !shader.isSupported) return null;
            var m = new Material(shader);
            m.name = "GhostHand posed (shared)";
            m.hideFlags = HideFlags.HideAndDontSave;
            ApplyFlags(m, FlagStore.Current);
            s_material = m;
            return m;
        }

        static void ApplyFlags(Material m, FlagSet flags)
        {
            if (m == null) return;
            Color baseColor = DefaultBlue;
            float fill = 0.2f;
            if (flags != null && flags.ghost != null)
            {
                Color parsed;
                if (!string.IsNullOrEmpty(flags.ghost.color) && ColorUtility.TryParseHtmlString(flags.ghost.color, out parsed)) baseColor = parsed;
                if (!float.IsNaN(flags.ghost.fill) && !float.IsInfinity(flags.ghost.fill)) fill = Mathf.Clamp01(flags.ghost.fill);
            }
            m.SetColor(ColorId, baseColor);
            m.SetColor(RimColorId, Color.Lerp(baseColor, Color.white, 0.57f));    // #4FC3F7 -> #B3E5FC, as CapsuleHand
            m.SetFloat(FillId, fill);
        }

        void OnFlagsChanged(FlagSet flags)
        {
            if (s_material != null) ApplyFlags(s_material, flags);
        }

        void PushBlock()
        {
            if (_block == null) return;
            _block.SetFloat(FadeId, _fadeNow);
            _block.SetFloat(EmphasisId, _emphasisNow);
            _block.SetColor(TipColorId, DefaultTip);
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].SetPropertyBlock(_block);
        }

        // ------------------------------------------------------------------------------------------------ public controls

        /// <summary>True when the FBX, its 16 bones, the poses and the shader were all found and the rig matches the poses.</summary>
        public bool IsAvailable { get { return _available; } }
        /// <summary>Why IsAvailable is false (the text of the one warning), or null.</summary>
        public string UnavailableReason { get { return _reason; } }
        /// <summary>"fretting" or "strumming".</summary>
        public string Hand { get { return _hand; } }
        /// <summary>The name of the pose shown, or being morphed to.</summary>
        public string CurrentPose { get { return _currentPose; } }
        /// <summary>The bone driver (Blend for a strum, fingertip positions, calibration results). Null when unavailable early.</summary>
        public GhostPoseDriver Driver { get { return _driver; } }
        /// <summary>The FBX instance, or null when unavailable.</summary>
        public GameObject Model { get { return _model; } }
        /// <summary>The fade currently drawn (1 = normal).</summary>
        public float Fade { get { return _fadeNow; } }

        /// <summary>
        /// Shows a pose of this hand by name ("G", "rest", "down-start" ...). immediate = true jumps (first placement, tests);
        /// false eases over morphSeconds, starting from whatever is on screen. Asking again for the pose being shown or
        /// morphed to changes nothing. False when unavailable or when the pose is not loaded (logged once per name).
        /// </summary>
        public bool SetPose(string poseName, bool immediate)
        {
            if (!_available || _driver == null || poseName == null) return false;
            GhostPose pose;
            if (!TryGetLogged(poseName, out pose)) return false;
            bool ok = immediate ? _driver.Apply(pose) : _driver.MorphTo(pose, morphSeconds);
            if (ok) _currentPose = poseName;
            return ok;
        }

        /// <summary>The pose library this hand reads (never null once created).</summary>
        public GhostPoseLibrary Library { get { return _library; } }

        /// <summary>A loaded pose of this hand by name. False (pose null) when unavailable or not loaded; never logs.</summary>
        public bool TryGetPose(string poseName, out GhostPose pose)
        {
            pose = null;
            return _available && _library != null && poseName != null && _library.TryGet(_hand, poseName, out pose);
        }

        /// <summary>
        /// Shows the blend of two poses of this hand at u (0 = fromPose, 1 = toPose, used as it is: ease it yourself), moving
        /// the fraction <paramref name="follow"/> of the way there (1 = exactly; see GhostPoseDriver.BlendLinearToward).
        /// Safe and allocation-free every frame: the beat-locked strokes and chord morphs of GhostHandsDirector use it.
        /// A pose that is not loaded is logged once per name and the other one is shown alone; false when neither is.
        /// </summary>
        public bool ShowBlend(string fromPose, string toPose, float u, float follow)
        {
            if (!_available || _driver == null) return false;
            GhostPose a, b;
            bool hasA = TryGetLogged(fromPose, out a), hasB = TryGetLogged(toPose, out b);
            if (!hasA && !hasB) return false;
            if (!hasA) a = b;
            if (!hasB) b = a;
            if (!_driver.BlendLinearToward(a, b, u, follow)) return false;
            _currentPose = u >= 0.5f ? b.Name : a.Name;
            return true;
        }

        bool TryGetLogged(string poseName, out GhostPose pose)
        {
            if (TryGetPose(poseName, out pose)) return true;
            if (poseName != null && _missingPoses.Add(poseName))   // once per name: a song may alternate two missing chords
            {
                UGALog.Warn("ghost_hand_pose_missing", GhostPose.FileNameFor(_hand, poseName) + " is not loaded; the " + _hand + " hand keeps its pose");
            }
            return false;
        }

        /// <summary>1 = normal, 0.4 = "fade to 40% after success" (flag ghost.fadeAfterSuccess). Eases over morphSeconds.</summary>
        public void SetFade(float fade)
        {
            SetFade(fade, false);
        }

        /// <summary>As SetFade; immediate = true jumps.</summary>
        public void SetFade(float fade, bool immediate)
        {
            float v = IsFinite(fade) ? Mathf.Clamp01(fade) : 1f;
            if (immediate)
            {
                _fadeFrom = _fadeTo = _fadeNow = v;
                PushBlock();
                return;
            }
            if (v == _fadeTo) return;
            StartLookChange();
            _fadeTo = v;
        }

        /// <summary>Brightness of the whole hand (1 = full, 0.3 = dimmed). Eases over morphSeconds. The FBX is one mesh, so
        /// finger-by-finger emphasis stays with the neck's dots.</summary>
        public void SetEmphasis(float emphasis)
        {
            float v = IsFinite(emphasis) ? Mathf.Clamp(emphasis, 0f, 2f) : 1f;
            if (v == _emphasisTo) return;
            StartLookChange();
            _emphasisTo = v;
        }

        /// <summary>Where a fingertip is now, world space (digit 0 thumb .. 4 pinky). False when unavailable.</summary>
        public bool TryGetFingertip(int digit, out Vector3 worldPosition)
        {
            worldPosition = Vector3.zero;
            return _available && _driver != null && _driver.TryGetFingertip(digit, out worldPosition);
        }

        /// <summary>Put the hand and all its parts on a layer (for per-camera culling masks).</summary>
        public void SetLayer(int layer)
        {
            SetLayerRecursively(transform, layer);
        }

        /// <summary>Advance the eases by dt seconds. Update() calls this; call it yourself where Update does not run
        /// (edit mode, tests). Allocation-free.</summary>
        public void Tick(float dt)
        {
            if (!_available) return;
            _driver.Tick(dt);
            if (_lookT < 1f)
            {
                float step = dt > 0f ? dt / Mathf.Max(morphSeconds, 1e-3f) : 0f;
                _lookT = Mathf.Min(1f, _lookT + step);
                float e = GhostPose.Ease(_lookT);
                _fadeNow = _fadeFrom + (_fadeTo - _fadeFrom) * e;
                _emphasisNow = _emphasisFrom + (_emphasisTo - _emphasisFrom) * e;
                PushBlock();
            }
        }

        void StartLookChange()
        {
            _fadeFrom = _fadeNow;
            _emphasisFrom = _emphasisNow;
            _lookT = 0f;
        }

        static bool IsFinite(float v)
        {
            return !(float.IsNaN(v) || float.IsInfinity(v));
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
    }
}
