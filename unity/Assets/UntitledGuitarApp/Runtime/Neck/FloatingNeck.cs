// FloatingNeck: the virtual guitar neck a beginner sees through the glasses. Built entirely from code:
//
//     FloatingNeck neck = FloatingNeck.Create(worldRoot);        // worldRoot: a transform that does NOT move with the head
//     neck.Build(layout);                                        // NeckLayout from Core's FretboardGeometry
//     neck.SetDots(dots, count);                                 // FingertipDot[] from Core's ChordLibrary + FretboardGeometry
//     neck.SetStringMarks(marks);                                // StringMark[6], index = string number - 1
//     neck.PlaceFloating(headTransform);                         // floating mode: hangs in front of the player
//     ghostHand.transform.SetParent(neck.Socket, false);         // the fretting ghost hand: neck frame, metres
//     strumHand.transform.SetParent(neck.StrumZone, false);      // the strumming ghost hand
//
// It shows strings, frets, the nut, the fretboard edges, fingertip dots with finger numbers and X / O marks at the nut.
// Positions come in as data, in the pinned neck frame (NeckLayout.cs); nothing here computes music theory.
//
// Two modes. Floating: PlaceFloating puts it 0.8-1.0 m ahead, lower-left of view, like the player's own neck seen from
// above, and keeps it there as the head turns. Aligned: another component (NeckAligner, later) calls SetWorldPose to put it
// on the real neck. The transform of this GameObject IS the neck frame: its position is the nut, its scale is the neck's
// size, and for a left-handed player its z scale is negative (the neck, the dots and anything parented to Socket or
// StrumZone are mirrored together).
//
// Cost: two meshes and one material. Meshes are rebuilt only when the layout, the dots, the marks, the mode or a flag
// changes. Brightness changes (active finger, muted strings) are material values eased in Update; nothing allocates per frame.
//
// Sources (Unity 2021.3.3f1 offline docs, /Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/):
//   Mesh.SetVertices.html, Mesh.SetColors.html, Mesh.SetUVs.html, Mesh.SetTriangles.html  the (array, start, length) overloads,
//                                         which copy a slice of a preallocated array ("using a part of the input array")
//   Mesh.MarkDynamic.html                 "Optimize mesh for frequent updates"
//   Resources.Load.html                   Resources.Load<Shader>("Shaders/NeckOverlay"): path relative to a Resources folder, no extension
//   Shader-isSupported.html               false when the shader cannot run (then a plain additive fallback is used)
//   GL.html                               the example there uses Shader.Find("Hidden/Internal-Colored") with _SrcBlend, _DstBlend,
//                                         _Cull, _ZWrite: "a built-in shader that is useful for drawing simple colored things"
//   Material.SetVector.html, Shader.PropertyToID.html
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>The four flags the neck reads, as a value. Normally taken from FlagStore; tests and the preview set them directly.</summary>
    public struct NeckOptions
    {
        /// <summary>Flag leftHanded: mirror the neck (and everything parented to its anchors).</summary>
        public bool leftHanded;

        /// <summary>Flag ghost.fingerColors: finger 1 yellow, 2 magenta, 3 lime, 4 orange.</summary>
        public bool fingerColors;

        /// <summary>Flag largeText: finger numbers 1.35 times taller.</summary>
        public bool largeText;

        /// <summary>Flag debugOverlay: draw the neck frame's axes at the socket and a cross at the strum zone.</summary>
        public bool debugOverlay;

        public static NeckOptions FromFlags(FlagSet flags)
        {
            NeckOptions o;
            o.leftHanded = flags != null && flags.leftHanded;
            o.fingerColors = flags != null && flags.ghost != null && flags.ghost.fingerColors;
            o.largeText = flags != null && flags.largeText;
            o.debugOverlay = flags != null && flags.debugOverlay;
            return o;
        }

        public bool SameAs(NeckOptions other)
        {
            return leftHanded == other.leftHanded && fingerColors == other.fingerColors && largeText == other.largeText &&
                   debugOverlay == other.debugOverlay;
        }
    }

    [DisallowMultipleComponent]
    public sealed class FloatingNeck : MonoBehaviour
    {
        /// <summary>The most fingertip dots shown at once.</summary>
        public const int MaxDots = 8;

        /// <summary>Resources path of the overlay shader (Runtime/Resources/Shaders/NeckOverlay.shader).</summary>
        public const string ShaderResourcePath = "Shaders/NeckOverlay";

        const int LineQuadCapacity = 192;
        const int DotQuadCapacity = 192;

        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        static readonly int GainAId = Shader.PropertyToID("_GainA");
        static readonly int GainBId = Shader.PropertyToID("_GainB");
        static readonly int GainCId = Shader.PropertyToID("_GainC");
        static readonly string[] Digits = { string.Empty, "1", "2", "3", "4" };

        [Tooltip("Where the floating neck hangs relative to the head (MASTER_PLAN section 17).")]
        [SerializeField] FloatingPlacement _floating = FloatingPlacement.Default;

        [Tooltip("Aligned mode: the eye-to-neck distance that line widths and text sizes are chosen for, in metres.")]
        [SerializeField] float _alignedViewDistanceM = 0.55f;

        [Tooltip("Floating mode: the strum zone sits this far beyond the last fret, in mm (0 = at its real distance).")]
        [SerializeField] float _floatingStrumGapMm = 110f;

        [Tooltip("Overall brightness of the neck's lines and dots.")]
        [SerializeField] float _brightness = 1f;

        bool _initialized;
        bool _placed;
        bool _attachedToHead;
        bool _followHead = true;
        Transform _home;
        Transform _viewer;
        NeckMode _mode = NeckMode.Floating;
        NeckPose _floatingPose;

        NeckLayout _layout;
        NeckSizes _sizes;
        NeckOptions _options;
        bool _optionsOverridden;
        float _viewDistanceM;
        float _builtDistanceM = -1f;
        float _builtScale = -1f;
        NeckMode _builtMode;

        readonly FingertipDot[] _dots = new FingertipDot[MaxDots];
        int _dotCount;
        readonly StringMark[] _marks = new StringMark[NeckLayout.StringCount];
        readonly LabelSlot[] _slots = new LabelSlot[MaxDots];
        int _slotCount;
        int _activeFinger = -1;

        readonly float[] _gain = new float[NeckGeometry.GroupCount];
        readonly float[] _gainTarget = new float[NeckGeometry.GroupCount];
        bool _animating;
        bool _placementDirty;

        NeckGeometry _lineGeometry;
        NeckGeometry _dotGeometry;
        Mesh _lineMesh;
        Mesh _dotMesh;
        Material _material;
        Transform _socket;
        Transform _strumZone;
        readonly Transform[] _labelHolders = new Transform[MaxDots];
        readonly NeckLabel[] _labels = new NeckLabel[MaxDots];

        // ------------------------------------------------------------------ creation

        /// <summary>
        /// Creates the neck as a child of <paramref name="parent"/> (may be null). The parent must not move with the head and
        /// must have unit scale: it is where the neck lives in aligned mode. Nothing is drawn until <see cref="Build"/>.
        /// </summary>
        public static FloatingNeck Create(Transform parent)
        {
            var go = new GameObject("FloatingNeck");
            if (parent != null) go.transform.SetParent(parent, false);
            FloatingNeck neck = go.AddComponent<FloatingNeck>();
            neck.EnsureInit();
            return neck;
        }

        void Awake()
        {
            EnsureInit();
        }

        // Awake does not run in Edit Mode (previews, EditMode tests), so every public member starts with EnsureInit.
        void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            _home = transform.parent;
            for (int i = 0; i < _gain.Length; i++)
            {
                _gain[i] = 1f;
                _gainTarget[i] = 1f;
            }
            _options = NeckOptions.FromFlags(FlagStore.Current);
            _material = CreateMaterial();
            _lineGeometry = new NeckGeometry(LineQuadCapacity);
            _dotGeometry = new NeckGeometry(DotQuadCapacity);
            _lineMesh = NewMesh("NeckLines", false);
            _dotMesh = NewMesh("NeckDots", true);
            NewMeshObject("Lines", _lineMesh);
            NewMeshObject("Dots", _dotMesh);
            _socket = NewChild("Socket", transform);
            _strumZone = NewChild("StrumZone", transform);
            for (int i = 0; i < MaxDots; i++)
            {
                _labelHolders[i] = NewChild("FingerNumber" + i, transform);
                _labels[i] = NeckLabel.Create(_labelHolders[i], "Text");
                _labels[i].Visible = false;
            }
            PushGains();
        }

        static Material CreateMaterial()
        {
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            bool fallback = shader == null || !shader.isSupported;
            if (fallback)
            {
                UGALog.Error("neck_shader", "Resources/" + ShaderResourcePath + (shader == null ? " not found" : " is not supported") +
                                            "; using Hidden/Internal-Colored (dots will be square)");
                shader = Shader.Find("Hidden/Internal-Colored");
            }
            if (shader == null) return null;
            var material = new Material(shader);
            material.name = "NeckOverlay (runtime)";
            material.hideFlags = HideFlags.DontSave;
            if (fallback)
            {
                material.SetInt("_SrcBlend", (int)BlendMode.One);
                material.SetInt("_DstBlend", (int)BlendMode.One);
                material.SetInt("_Cull", (int)CullMode.Off);
                material.SetInt("_ZWrite", 0);
            }
            return material;
        }

        static Mesh NewMesh(string meshName, bool dynamic)
        {
            var mesh = new Mesh();
            mesh.name = meshName;
            mesh.hideFlags = HideFlags.DontSave;
            if (dynamic) mesh.MarkDynamic();
            return mesh;
        }

        void NewMeshObject(string objectName, Mesh mesh)
        {
            Transform child = NewChild(objectName, transform);
            child.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = child.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        static Transform NewChild(string objectName, Transform parent)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        void OnEnable()
        {
            FlagStore.Changed += OnFlagsChanged;
        }

        void OnDisable()
        {
            FlagStore.Changed -= OnFlagsChanged;
        }

        void OnDestroy()
        {
            DestroyOwned(_lineMesh);
            DestroyOwned(_dotMesh);
            DestroyOwned(_material);
        }

        static void DestroyOwned(Object owned)
        {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
        }

        // ------------------------------------------------------------------ anchors and state

        /// <summary>
        /// The neck socket at the nut: the neck frame itself (origin on the centre line at the nut face, on the playing surface;
        /// +x to the bridge, +y out of the fretboard, +z toward string 1), in metres. Parent the fretting ghost hand here: a
        /// point given in neck-local millimetres is at 0.001 times that in this transform's local space.
        /// </summary>
        public Transform Socket
        {
            get { EnsureInit(); return _socket; }
        }

        /// <summary>
        /// The strum zone: on the centre line at string height, same axes as the socket. In aligned mode it is at the real
        /// nut-to-soundhole distance; on the floating neck it sits just beyond the last fret (see NeckMath.FloatingStrumZoneXMm).
        /// Parent the strumming ghost hand here.
        /// </summary>
        public Transform StrumZone
        {
            get { EnsureInit(); return _strumZone; }
        }

        /// <summary>Floating (set by <see cref="PlaceFloating(Transform)"/>) or Aligned (set by <see cref="SetWorldPose"/>).</summary>
        public NeckMode Mode
        {
            get { return _mode; }
        }

        /// <summary>The layout last accepted by <see cref="Build"/> (a private copy), or null.</summary>
        public NeckLayout Layout
        {
            get { return _layout; }
        }

        /// <summary>The sizes the neck is currently drawn with, in neck-local mm.</summary>
        public NeckSizes Sizes
        {
            get { return _sizes; }
        }

        /// <summary>The flags in effect.</summary>
        public NeckOptions Options
        {
            get { EnsureInit(); return _options; }
        }

        public int DotCount
        {
            get { return _dotCount; }
        }

        /// <summary>How many finger numbers are shown.</summary>
        public int LabelCount
        {
            get { return _slotCount; }
        }

        /// <summary>The active finger, or -1 when all dots are bright.</summary>
        public int ActiveFinger
        {
            get { return _activeFinger; }
        }

        /// <summary>Where the floating neck hangs. Setting it re-places a floating neck.</summary>
        public FloatingPlacement Floating
        {
            get { return _floating; }
            set
            {
                _floating = value.Clamped();
                if (_initialized && _placed && _mode == NeckMode.Floating) PlaceFloating(_viewer, _followHead);
            }
        }

        /// <summary>
        /// The player's head, used to turn the finger numbers toward the eyes. PlaceFloating sets it; in aligned mode set it
        /// once (without it the numbers lie flat on the fretboard).
        /// </summary>
        public Transform Viewer
        {
            get { return _viewer; }
            set { _viewer = value; if (_initialized) UpdateLabelPoses(); }
        }

        /// <summary>Overall brightness of the lines and dots (1 = as designed). Tune it with the glasses' dimming in the demo room.</summary>
        public float Brightness
        {
            get { return _brightness; }
            set
            {
                _brightness = Mathf.Max(0f, value);
                if (_initialized) PushGains();
            }
        }

        // ------------------------------------------------------------------ content

        /// <summary>
        /// Draws the neck for <paramref name="layout"/> (a copy is kept). Returns false, logs every problem and keeps the
        /// previous neck when <see cref="NeckLayout.Validate"/> reports errors.
        /// </summary>
        public bool Build(NeckLayout layout)
        {
            EnsureInit();
            if (layout == null)
            {
                UGALog.Error("neck_layout", "Build was given no layout");
                return false;
            }
            List<string> errors = layout.Validate();
            if (errors.Count > 0)
            {
                UGALog.Error("neck_layout", string.Join(" | ", errors));
                return false;
            }
            _layout = layout.Clone();
            RebuildAll();
            return true;
        }

        /// <summary>
        /// Shows the first <paramref name="count"/> dots of <paramref name="dots"/> (copied; at most <see cref="MaxDots"/>).
        /// Dots whose finger is 1-4 get a finger number. Calling it again with the same dots does nothing.
        /// </summary>
        public void SetDots(FingertipDot[] dots, int count)
        {
            EnsureInit();
            int n = dots == null ? 0 : Mathf.Clamp(count, 0, Mathf.Min(dots.Length, MaxDots));
            if (dots != null && count > MaxDots)
                UGALog.Warn("neck_dots", "asked for " + count + " dots; only the first " + MaxDots + " are shown");

            bool same = n == _dotCount;
            for (int i = 0; same && i < n; i++)
            {
                same = dots[i].stringNumber == _dots[i].stringNumber && dots[i].finger == _dots[i].finger &&
                       dots[i].xMm == _dots[i].xMm && dots[i].zMm == _dots[i].zMm;
            }
            if (same) return;

            for (int i = 0; i < n; i++) _dots[i] = dots[i];
            _dotCount = n;
            if (_layout == null) return;   // drawn by the next Build
            WarnIfDotsMissTheirStrings();
            RebuildDots();
            if (Application.isPlaying)
            {
                // New dots ease in instead of popping (nothing may flash).
                for (int finger = 0; finger <= 4; finger++)
                {
                    int group = NeckGeometry.FingerGroup(finger);
                    _gain[group] = Mathf.Min(_gain[group], NeckStyle.DotFadeInStart);
                }
                _animating = true;
                PushGains();
            }
        }

        /// <summary>
        /// The mark at the nut end of each string: <paramref name="marksByString"/>[i] is for string i + 1 (so index 0 is the
        /// high E string). Null or a short array leaves the remaining strings unmarked. A string marked
        /// <see cref="StringMark.Muted"/> is also dimmed along its whole length.
        /// </summary>
        public void SetStringMarks(StringMark[] marksByString)
        {
            EnsureInit();
            bool same = true;
            for (int i = 0; i < _marks.Length; i++)
            {
                StringMark mark = marksByString != null && i < marksByString.Length ? marksByString[i] : StringMark.None;
                if (mark != _marks[i]) same = false;
                _marks[i] = mark;
            }
            if (same) return;
            UpdateGainTargets();
            if (_layout != null) RebuildDots();
        }

        /// <summary>
        /// Finger-by-finger teaching: the dot (and number) of <paramref name="finger"/> stays bright and the others drop to
        /// 30%. -1 (or anything outside 0-4) makes every dot bright again. The change is eased over 250 ms.
        /// </summary>
        public void SetActiveFinger(int finger)
        {
            EnsureInit();
            int f = finger >= 0 && finger <= 4 ? finger : -1;
            if (f == _activeFinger) return;
            _activeFinger = f;
            UpdateGainTargets();
        }

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Floating mode: hangs the neck in front of <paramref name="head"/> (0.8-1.0 m ahead, lower-left of view, like the
        /// player's own neck seen from above; mirrored when the leftHanded flag is on) and makes it a child of the head, so it
        /// stays put in the view however the head turns. With a null head it is placed the same way in its parent's space.
        /// </summary>
        public void PlaceFloating(Transform head)
        {
            PlaceFloating(head, true);
        }

        /// <summary>
        /// As <see cref="PlaceFloating(Transform)"/>; with <paramref name="followHead"/> false the neck is placed once in front
        /// of where the head is now and then stays there in the world.
        /// </summary>
        public void PlaceFloating(Transform head, bool followHead)
        {
            EnsureInit();
            _viewer = head;
            _followHead = followHead;
            _mode = NeckMode.Floating;
            _placed = true;
            RebuildIfStyleChanged();

            NeckPose pose = NeckPlacement.Floating(_floating, _options.leftHanded);
            _floatingPose = pose;
            if (head != null && followHead)
            {
                if (transform.parent != head) transform.SetParent(head, false);
                _attachedToHead = true;
                transform.localPosition = pose.position;
                transform.localRotation = pose.rotation;
            }
            else if (head != null)
            {
                ReturnHome();
                Vector3 worldPosition;
                Quaternion worldRotation;
                NeckPlacement.ToWorld(head.position, head.rotation, pose, out worldPosition, out worldRotation);
                transform.SetPositionAndRotation(worldPosition, worldRotation);
            }
            else
            {
                ReturnHome();
                transform.localPosition = pose.position;
                transform.localRotation = pose.rotation;
            }
            transform.localScale = pose.scale;
            UpdateLabelPoses();
        }

        /// <summary>
        /// Aligned mode: puts the nut at <paramref name="nutPosition"/> (world) with <paramref name="rotation"/> carrying the
        /// neck's +x (nut toward bridge) and +y (out of the fretboard), at life size. For a right-handed player +z (Unity's
        /// cross(x, y)) is then toward string 1; when the leftHanded flag is on the neck is mirrored across its length, so the
        /// same call works for a left-handed guitar. Safe to call every frame: it only moves transforms.
        /// </summary>
        public void SetWorldPose(Vector3 nutPosition, Quaternion rotation)
        {
            EnsureInit();
            ReturnHome();
            _mode = NeckMode.Aligned;
            _placed = true;
            RebuildIfStyleChanged();
            transform.SetPositionAndRotation(nutPosition, rotation);
            transform.localScale = NeckPlacement.ScaleFor(1f, _options.leftHanded);
            UpdateLabelPoses();
        }

        // ------------------------------------------------------------------ flags

        /// <summary>Uses these options instead of the flags (tests, previews), until <see cref="UseFlags"/>.</summary>
        public void UseOptions(NeckOptions options)
        {
            EnsureInit();
            _optionsOverridden = true;
            ApplyOptions(options);
        }

        /// <summary>Goes back to reading leftHanded, ghost.fingerColors, largeText and debugOverlay from FlagStore.</summary>
        public void UseFlags()
        {
            EnsureInit();
            _optionsOverridden = false;
            ApplyOptions(NeckOptions.FromFlags(FlagStore.Current));
        }

        void OnFlagsChanged(FlagSet flags)
        {
            if (!_initialized || _optionsOverridden) return;
            ApplyOptions(NeckOptions.FromFlags(flags));
        }

        void ApplyOptions(NeckOptions options)
        {
            if (_options.SameAs(options)) return;
            _options = options;
            if (_layout != null) RebuildAll();
            Replace();
        }

        // Puts the neck where it already is again, after something that changes the pose (handedness, placement values).
        void Replace()
        {
            if (!_placed) return;
            if (_mode == NeckMode.Floating)
            {
                PlaceFloating(_viewer, _followHead);
            }
            else
            {
                RebuildIfStyleChanged();
                transform.localScale = NeckPlacement.ScaleFor(1f, _options.leftHanded);
                UpdateLabelPoses();
            }
        }

        // ------------------------------------------------------------------ per frame

        void OnValidate()
        {
            // Inspector edits while playing: apply them from Update (OnValidate may not touch the hierarchy).
            if (_initialized) _placementDirty = true;
        }

        void Update()
        {
            if (_placementDirty)
            {
                _placementDirty = false;
                _floating = _floating.Clamped();
                if (_layout != null) RebuildAll();
                Replace();
            }
            if (!_animating) return;

            float dt = Time.unscaledDeltaTime;
            bool moving = false;
            for (int i = 1; i < _gain.Length; i++)
            {
                _gain[i] = NeckMath.Slew(_gain[i], _gainTarget[i], NeckStyle.GainSlewPerSecond, dt);
                if (_gain[i] != _gainTarget[i]) moving = true;
            }
            PushGains();
            _animating = moving;
        }

        void UpdateGainTargets()
        {
            for (int finger = 0; finger <= 4; finger++)
                _gainTarget[NeckGeometry.FingerGroup(finger)] = _activeFinger < 0 || finger == _activeFinger ? 1f : NeckStyle.InactiveGain;
            for (int s = 1; s <= NeckLayout.StringCount; s++)
                _gainTarget[NeckGeometry.StringGroup(s)] = _marks[s - 1] == StringMark.Muted ? NeckStyle.MutedStringGain : 1f;

            if (Application.isPlaying)
            {
                _animating = true;   // Update eases toward the targets
                return;
            }
            for (int i = 0; i < _gain.Length; i++) _gain[i] = _gainTarget[i];   // Edit Mode has no Update: jump
            PushGains();
        }

        // Sends the current gains to the material and the finger numbers.
        void PushGains()
        {
            if (_material != null)
            {
                _material.SetFloat(IntensityId, _brightness);
                _material.SetVector(GainAId, new Vector4(_gain[1], _gain[2], _gain[3], _gain[4]));
                _material.SetVector(GainBId, new Vector4(_gain[5], _gain[6], _gain[7], _gain[8]));
                _material.SetVector(GainCId, new Vector4(_gain[9], _gain[10], _gain[11], _gain[12]));
            }
            for (int k = 0; k < _slotCount; k++)
            {
                int finger = _dots[_slots[k].dotIndex].finger;
                _labels[k].Color = LabelColor(finger, _gain[NeckGeometry.FingerGroup(finger)]);
            }
        }

        Color LabelColor(int finger, float gain)
        {
            Color color = _options.fingerColors ? (Color)NeckStyle.Lighten(NeckStyle.FingerColor(finger), 0.3f) : Color.white;
            color.a = Mathf.Clamp01(NeckStyle.LabelIntensity * gain * Mathf.Min(1f, _brightness));
            return color;
        }

        // ------------------------------------------------------------------ building

        float StyleDistanceM()
        {
            return _mode == NeckMode.Floating ? _floating.Clamped().distanceM : Mathf.Clamp(_alignedViewDistanceM, 0.3f, 1.5f);
        }

        float StyleScale()
        {
            return _mode == NeckMode.Floating ? _floating.Clamped().scale : 1f;
        }

        float StrumZoneXMm()
        {
            return _mode == NeckMode.Floating ? NeckMath.FloatingStrumZoneXMm(_layout, _floatingStrumGapMm) : NeckMath.StrumZoneXMm(_layout);
        }

        // Line widths and text sizes are chosen for the viewing distance and scale, so they follow the mode.
        void RebuildIfStyleChanged()
        {
            if (_layout == null) return;
            if (_builtMode == _mode && _builtDistanceM == StyleDistanceM() && _builtScale == StyleScale()) return;
            RebuildAll();
        }

        void RebuildAll()
        {
            _viewDistanceM = StyleDistanceM();
            _builtDistanceM = _viewDistanceM;
            _builtScale = StyleScale();
            _builtMode = _mode;
            _sizes = NeckStyle.Resolve(_layout, _viewDistanceM, _builtScale, _options.largeText);

            NeckMeshBuilder.BuildLines(_layout, _sizes, BuildOptions(), _lineGeometry);
            Upload(_lineMesh, _lineGeometry, "lines");
            _socket.localPosition = Vector3.zero;
            _strumZone.localPosition = NeckMath.LocalMetres(StrumZoneXMm(), _layout.stringHeightMm, 0f);
            RebuildDots();
        }

        void RebuildDots()
        {
            _slotCount = NeckLabelLayout.Compute(_layout, _dots, _dotCount, _sizes.labelCapMm, _sizes.labelCellMm, _sizes.labelGapMm, _slots);
            NeckMeshBuilder.BuildDots(_layout, _sizes, BuildOptions(), _dots, _dotCount, _marks, _slots, _slotCount, _dotGeometry);
            Upload(_dotMesh, _dotGeometry, "dots");

            float capDegrees = NeckStyle.LabelCapDegrees(_options.largeText);
            for (int k = 0; k < MaxDots; k++)
            {
                if (k >= _slotCount)
                {
                    _labels[k].Visible = false;
                    continue;
                }
                int finger = _dots[_slots[k].dotIndex].finger;
                _labels[k].Visible = true;
                _labels[k].SetText(Digits[finger]);
                _labels[k].SetAngularHeight(capDegrees, _viewDistanceM);
            }
            UpdateLabelPoses();
            PushGains();
        }

        NeckBuildOptions BuildOptions()
        {
            NeckBuildOptions options;
            options.crossRibbons = _mode == NeckMode.Aligned;
            options.fingerColors = _options.fingerColors;
            options.debugOverlay = _options.debugOverlay;
            options.strumZoneXMm = StrumZoneXMm();
            options.breakStrings = _mode == NeckMode.Aligned;
            return options;
        }

        static void Upload(Mesh mesh, NeckGeometry geometry, string what)
        {
            mesh.Clear();   // first, so a shorter vertex list never leaves triangles pointing past its end
            if (geometry.QuadCount > 0)
            {
                mesh.SetVertices(geometry.positions, 0, geometry.VertexCount);
                mesh.SetColors(geometry.colors, 0, geometry.VertexCount);
                mesh.SetUVs(0, geometry.shape, 0, geometry.VertexCount);
                mesh.SetUVs(1, geometry.extra, 0, geometry.VertexCount);
                mesh.SetTriangles(geometry.triangles, 0, geometry.IndexCount, 0, true, 0);
            }
            if (geometry.Dropped > 0)
                UGALog.Warn("neck_mesh", what + ": " + geometry.Dropped + " quad(s) did not fit and were not drawn");
        }

        // Finger numbers: positioned beside the fretboard, hinged about the neck's length to face the viewer, and un-mirrored
        // when the neck is mirrored (so a left-handed player does not read backwards digits).
        void UpdateLabelPoses()
        {
            if (_layout == null || _slotCount == 0) return;
            float h = _layout.stringHeightMm;
            Vector3 about = NeckMath.LocalMetres(_layout.LastFretXMm * 0.5f, h, 0f);
            Quaternion facing = NeckPlacement.LabelFacing(ViewerInNeckSpace(about), about);
            Vector3 holderScale = new Vector3(transform.localScale.z < 0f ? -1f : 1f, 1f, 1f);
            for (int k = 0; k < _slotCount; k++)
            {
                Transform holder = _labelHolders[k];
                holder.localPosition = NeckMath.LocalMetres(_slots[k].xMm, h, _slots[k].zMm);
                holder.localRotation = facing;
                holder.localScale = holderScale;
            }
        }

        Vector3 ViewerInNeckSpace(Vector3 fallbackAbout)
        {
            if (_viewer != null) return transform.InverseTransformPoint(_viewer.position);
            if (_mode == NeckMode.Floating && _placed) return _floatingPose.InverseTransformPoint(Vector3.zero);
            return fallbackAbout + new Vector3(0f, 1f, 0f);   // unknown viewer: lie flat, facing out of the fretboard
        }

        void ReturnHome()
        {
            if (!_attachedToHead) return;
            _attachedToHead = false;
            transform.SetParent(_home != null ? _home : null, true);
        }

        // The data wins, but a dot far from the string it names almost always means the two sides disagree about the frame.
        void WarnIfDotsMissTheirStrings()
        {
            for (int i = 0; i < _dotCount; i++)
            {
                FingertipDot dot = _dots[i];
                if (dot.stringNumber < 1 || dot.stringNumber > NeckLayout.StringCount) continue;
                float expected = NeckMath.StringZMm(_layout, dot.stringNumber, dot.xMm);
                if (Mathf.Abs(expected - dot.zMm) <= 1.5f) continue;
                UGALog.Warn("neck_dots", "dot on string " + dot.stringNumber + " has z = " + dot.zMm.ToString("0.0") + " mm but that string is at z = " +
                                         expected.ToString("0.0") + " mm there. Check the neck frame: +z points toward string 1 (high E).");
                return;
            }
        }
    }
}
