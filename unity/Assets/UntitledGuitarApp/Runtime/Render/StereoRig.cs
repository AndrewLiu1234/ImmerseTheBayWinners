using System.Globalization;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Render
{
    /// <summary>
    /// The camera rig for the glasses: two eye cameras an IPD apart that draw the left and right halves of one side-by-side
    /// frame (3840x1080 = two 1920x1080 eyes) onto the display the glasses are plugged into. Built entirely from code:
    /// <c>StereoRig.Create(parent)</c>, no Inspector wiring.
    ///
    ///   StereoRig (this component, parent chosen by the caller)
    ///     Head          other code rotates this to turn the wearer's head (ImuHeadPose, the editor mouse-look). Put the HUD,
    ///                   the reticle and anything else that must follow the head under it. Not read or written here.
    ///       LeftEye     Camera at x = -IPD/2, draws the left half of the display  (the only camera in the 2D fallback)
    ///       RightEye    Camera at x = +IPD/2, draws the right half of the display (disabled in the 2D fallback)
    ///
    /// Everything comes from <c>FlagStore.Current.render</c> and is re-applied by <see cref="Apply"/> on every
    /// <c>FlagStore.Changed</c>:
    ///   stereo         true = side-by-side stereo; false = 2D fallback, one camera, full viewport (glasses in 2D mode, 1920x1080)
    ///   glassesDisplay Unity display index of the glasses (1 = "Display 2"; 0 = the Mac screen). Every eye camera gets
    ///                  Camera.targetDisplay = this value. In a standalone build the display is activated (Display.Activate);
    ///                  in the Editor the Game view's Display dropdown decides (see Editor/GlassesGameView.cs).
    ///   ipdMm, fovDiagonalDeg, eyeWidth, eyeHeight, nearM, farM, convergenceM   see StereoMath (projection maths and derivation)
    ///   swapEyes       the left camera draws the right half and vice versa (glasses that show the halves to the opposite eyes)
    ///
    /// Look: clear to solid black, no skybox (black is transparent on these additive glasses), no HDR, no post-processing,
    /// no lights and no shadows are created here. Screen-space overlay canvases render once per display, not once per eye:
    /// give the HUD world-space canvases under <see cref="Head"/> instead.
    ///
    /// Cost: nothing allocates per frame. Update only smooths an fps number and polls for a display that is not plugged in
    /// yet. The optional fps log (see <see cref="FpsLogIntervalSeconds"/>) is off by default because logging boxes values.
    ///
    /// API facts used (quoted in docs/apis/unity/multi-display.md): Camera.targetDisplay, Camera.rect, Camera.projectionMatrix,
    /// Display.displays, Display.Activate, Display.active, Display.systemWidth/systemHeight, Display.renderingWidth/renderingHeight.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StereoRig : MonoBehaviour
    {
        public const string RigObjectName = "StereoRig";
        public const string HeadName = "Head";
        public const string LeftEyeName = "LeftEye";
        public const string RightEyeName = "RightEye";

        // Flag values are clamped to these so a typo in flags.json cannot produce a degenerate matrix (a warning is logged).
        const float FallbackDiagonalDeg = 52f, MinDiagonalDeg = 10f, MaxDiagonalDeg = 120f;
        const float FallbackIpdMm = 63f, MaxIpdMm = 120f;
        const float FallbackNearM = 0.1f, MinNearM = 0.01f, MinClipRangeM = 0.5f, FallbackFarM = 20f;
        const float MinConvergenceM = 0.25f;
        const int MaxDisplayIndex = 7;            // Camera.targetDisplay: "Maximum number of displays (eg. monitors) supported is 8."
        const int FallbackEyeWidth = 1920, FallbackEyeHeight = 1080, MinEyePixels = 16;

        const float FpsWindowSeconds = 0.5f;
        const float DisplayPollSeconds = 1f;      // while the glasses display is not there yet
        const float SizeCheckDelaySeconds = 1f;   // after activating, give the window a moment before comparing sizes

        static readonly Color DebugLeftColor = new Color(0.6f, 0f, 0f, 1f);
        static readonly Color DebugRightColor = new Color(0f, 0f, 0.6f, 1f);

        [Tooltip("Spike aid: the left eye camera clears to red and the right eye camera to blue, so you can see which eye gets which half. Off in the real app.")]
        [SerializeField] bool debugEyeColors;

        [Tooltip("Keep rendering when another app has focus. Without it a standalone player pauses (the glasses freeze) as soon as you click another window on the Mac.")]
        [SerializeField] bool keepRunningInBackground = true;

        [Tooltip("0 = off. Otherwise log an fps line every this many seconds (allocates a few bytes per line; keep it off in the real app).")]
        [SerializeField] float fpsLogIntervalSeconds;

        Transform _head;
        Camera _left;
        Camera _right;
        bool _built;
        bool _applied;

        // sanitised values of the last Apply
        float _diagonalDeg = FallbackDiagonalDeg;
        float _ipdMm = FallbackIpdMm;
        float _convergenceM;
        int _eyeWidth = FallbackEyeWidth;
        int _eyeHeight = FallbackEyeHeight;

        // standalone display handling
        bool _displayWanted;
        bool _warnedMissingDisplay;
        float _nextDisplayPollTime;
        bool _sizeCheckPending;
        float _sizeCheckTime;

        // fps
        int _fpsFrames;
        float _fpsAccum;
        float _fpsWorstDt;
        float _fpsLogAccum;

        /// <summary>Parent of both eyes. Rotate it to turn the wearer's head.</summary>
        public Transform Head { get { return _head; } }

        /// <summary>Left eye camera (the only camera in the 2D fallback).</summary>
        public Camera LeftEye { get { return _left; } }

        /// <summary>Right eye camera (disabled in the 2D fallback).</summary>
        public Camera RightEye { get { return _right; } }

        /// <summary>True for side-by-side stereo, false in the 2D fallback.</summary>
        public bool IsStereo { get; private set; }

        /// <summary>The Unity display index the eye cameras render to (the last applied render.glassesDisplay, clamped to 0..7).</summary>
        public int TargetDisplay { get; private set; }

        /// <summary>Frames per second over the last half second (unscaled time).</summary>
        public float SmoothedFps { get; private set; }

        /// <summary>Seconds between "stereo.fps" log lines; 0 turns the log off (the default).</summary>
        public float FpsLogIntervalSeconds
        {
            get { return fpsLogIntervalSeconds; }
            set { fpsLogIntervalSeconds = value < 0f ? 0f : value; }
        }

        /// <summary>Left eye clears to red and right eye to blue instead of black. For checking which eye gets which half.</summary>
        public bool DebugEyeColors
        {
            get { return debugEyeColors; }
            set { debugEyeColors = value; ApplyEyeColors(); }
        }

        /// <summary>
        /// Creates the rig under <paramref name="parent"/> (null = scene root), builds Head / LeftEye / RightEye and applies
        /// <c>FlagStore.Current.render</c>. From then on it follows <c>FlagStore.Changed</c> by itself.
        /// </summary>
        public static StereoRig Create(Transform parent)
        {
            GameObject go = new GameObject(RigObjectName);
            if (parent != null)
                go.transform.SetParent(parent, false);
            StereoRig rig = go.AddComponent<StereoRig>();
            rig.EnsureBuilt();                          // Awake does not run in Edit Mode; EnsureBuilt is idempotent
            if (!rig._applied)
                rig.Apply(FlagStore.Current.render);
            return rig;
        }

        /// <summary>
        /// Re-applies everything from the given render flags: eye positions, viewports, target display, projection matrices,
        /// 2D fallback, clear colour. Safe to call at any time (it is called on every FlagStore.Changed). Out-of-range values are
        /// clamped with a logged warning instead of throwing.
        /// </summary>
        public void Apply(RenderFlags flags)
        {
            EnsureBuilt();
            if (flags == null)
                flags = FlagStore.Current.render;

            // ---- sanitise ---------------------------------------------------------------------------------------------
            bool clamped = false;
            float diagonal = Sane(flags.fovDiagonalDeg, MinDiagonalDeg, MaxDiagonalDeg, FallbackDiagonalDeg, ref clamped);
            float ipd = Sane(flags.ipdMm, 0f, MaxIpdMm, FallbackIpdMm, ref clamped);
            float near = Sane(flags.nearM, MinNearM, 1000f, FallbackNearM, ref clamped);
            float far = Sane(flags.farM, near + MinClipRangeM, 100000f, FallbackFarM, ref clamped);
            float convergence = 0f;                     // 0 = parallel axes
            float convergenceFlag = flags.convergenceM;
            if (float.IsNaN(convergenceFlag) || float.IsInfinity(convergenceFlag) || convergenceFlag < 0f)
                clamped = true;                         // nonsense: fall back to parallel axes
            else if (convergenceFlag > 0f)
                convergence = Sane(convergenceFlag, MinConvergenceM, 1000f, MinConvergenceM, ref clamped);
            int display = flags.glassesDisplay;
            if (display < 0 || display > MaxDisplayIndex)
            {
                display = display < 0 ? 0 : MaxDisplayIndex;
                clamped = true;
            }
            int eyeWidth = flags.eyeWidth;
            int eyeHeight = flags.eyeHeight;
            if (eyeWidth < MinEyePixels || eyeHeight < MinEyePixels)
            {
                eyeWidth = eyeWidth < MinEyePixels ? FallbackEyeWidth : eyeWidth;
                eyeHeight = eyeHeight < MinEyePixels ? FallbackEyeHeight : eyeHeight;
                clamped = true;
            }
            if (clamped)
                UGALog.Warn("stereo.flags_clamped", "render flags were out of range and have been clamped: "
                    + "fovDiagonalDeg " + flags.fovDiagonalDeg + ", ipdMm " + flags.ipdMm + ", nearM " + flags.nearM + ", farM " + flags.farM
                    + ", convergenceM " + flags.convergenceM + ", glassesDisplay " + flags.glassesDisplay
                    + ", eye " + flags.eyeWidth + "x" + flags.eyeHeight);

            bool stereo = flags.stereo;
            IsStereo = stereo;
            TargetDisplay = display;
            _diagonalDeg = diagonal;
            _ipdMm = ipd;
            _convergenceM = stereo ? convergence : 0f;
            _eyeWidth = eyeWidth;
            _eyeHeight = eyeHeight;
            float aspect = StereoMath.AspectOf(eyeWidth, eyeHeight);

            // ---- cameras ----------------------------------------------------------------------------------------------
            ConfigureEye(_left, StereoEye.Left, stereo, flags.swapEyes, diagonal, aspect, near, far, ipd, convergence, display);
            ConfigureEye(_right, StereoEye.Right, stereo, flags.swapEyes, diagonal, aspect, near, far, ipd, convergence, display);
            ApplyEyeColors();

            // Players only: in the Editor this setter writes the project's Player Settings, which we must not touch from code.
            if (keepRunningInBackground && Application.isPlaying && !Application.isEditor)
                Application.runInBackground = true;

            // ---- display ----------------------------------------------------------------------------------------------
            ActivateDisplayIfNeeded();
            if (Application.isPlaying)
            {
                _sizeCheckPending = true;                         // compare sizes a moment from now (also logs the Editor's eye size)
                _sizeCheckTime = Time.unscaledTime + SizeCheckDelaySeconds;
            }

            float h, v;
            StereoMath.DiagonalToHorizontalVertical(diagonal, aspect, out h, out v);
            UGALog.Event("stereo.apply", "mode", stereo ? "sbs" : "2d", "displayIndex", display, "swapEyes", flags.swapEyes);
            UGALog.Event("stereo.optics", "fovHxV", Num(h, "0.0") + "x" + Num(v, "0.0"), "ipdMm", ipd, "convergenceM", _convergenceM);
            _applied = true;
        }

        /// <summary>One line describing the current setup, for a debug overlay. Allocates a string: call it at a low rate.</summary>
        public string Describe()
        {
            if (!_built)
                return "StereoRig: not built";
            float h, v;
            StereoMath.DiagonalToHorizontalVertical(_diagonalDeg, StereoMath.AspectOf(_eyeWidth, _eyeHeight), out h, out v);
            return (IsStereo ? "SBS" : "2D") + " on display index " + TargetDisplay
                + " | eye " + _eyeWidth + "x" + _eyeHeight
                + " | IPD " + Num(_ipdMm, "0.#") + " mm"
                + " | FOV " + Num(h, "0.0") + "x" + Num(v, "0.0")
                + " | convergence " + (_convergenceM > 0f ? Num(_convergenceM, "0.##") + " m" : "parallel")
                + " | " + Num(SmoothedFps, "0") + " fps";
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Unity messages
        // ---------------------------------------------------------------------------------------------------------------

        void Awake()
        {
            EnsureBuilt();
        }

        void OnEnable()
        {
            EnsureBuilt();
            FlagStore.Changed += OnFlagsChanged;
            Apply(FlagStore.Current.render);
        }

        void OnDisable()
        {
            FlagStore.Changed -= OnFlagsChanged;
        }

        void OnValidate()
        {
            // Inspector edits during Play Mode (debugEyeColors, fpsLogIntervalSeconds).
            if (fpsLogIntervalSeconds < 0f)
                fpsLogIntervalSeconds = 0f;
            if (_built)
                ApplyEyeColors();
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _fpsAccum += dt;
            _fpsFrames++;
            if (dt > _fpsWorstDt)
                _fpsWorstDt = dt;
            if (_fpsAccum >= FpsWindowSeconds)
            {
                SmoothedFps = _fpsFrames / _fpsAccum;
                if (fpsLogIntervalSeconds > 0f)
                {
                    _fpsLogAccum += _fpsAccum;
                    if (_fpsLogAccum >= fpsLogIntervalSeconds)
                    {
                        _fpsLogAccum = 0f;
                        UGALog.Event("stereo.fps", "fps", SmoothedFps, "worstFrameMs", _fpsWorstDt * 1000f);   // boxes: opt-in only
                    }
                }
                _fpsAccum = 0f;
                _fpsFrames = 0;
                _fpsWorstDt = 0f;
            }

            if (_displayWanted && Time.unscaledTime >= _nextDisplayPollTime)
            {
                _nextDisplayPollTime = Time.unscaledTime + DisplayPollSeconds;
                ActivateDisplayIfNeeded();
            }
            if (_sizeCheckPending && Time.unscaledTime >= _sizeCheckTime)
                CheckDisplaySize();
        }

        void OnFlagsChanged(FlagSet flags)
        {
            Apply(flags != null ? flags.render : FlagStore.Current.render);
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Building
        // ---------------------------------------------------------------------------------------------------------------

        void EnsureBuilt()
        {
            if (_built)
                return;
            _head = FindOrCreateChild(transform, HeadName);
            _left = FindOrCreateEye(_head, LeftEyeName);
            _right = FindOrCreateEye(_head, RightEyeName);
            _built = true;
        }

        static Transform FindOrCreateChild(Transform parent, string childName)
        {
            Transform t = parent.Find(childName);
            if (t == null)
            {
                GameObject go = new GameObject(childName);
                t = go.transform;
                t.SetParent(parent, false);
            }
            return t;
        }

        static Camera FindOrCreateEye(Transform head, string eyeName)
        {
            Transform t = FindOrCreateChild(head, eyeName);
            Camera cam = t.GetComponent<Camera>();
            if (cam == null)
                cam = t.gameObject.AddComponent<Camera>();

            // Settings that never change with the flags.
            cam.orthographic = false;
            cam.clearFlags = CameraClearFlags.SolidColor;     // no skybox: black is transparent on additive glasses
            cam.backgroundColor = Color.black;
            cam.cullingMask = ~0;
            cam.depth = 0f;
            cam.allowHDR = false;                             // HDR would force an intermediate render texture and a blit
            cam.allowDynamicResolution = false;
            cam.useOcclusionCulling = false;                  // no baked occlusion data in this project
            cam.targetTexture = null;                         // render straight into the display
            return cam;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Applying
        // ---------------------------------------------------------------------------------------------------------------

        static void ConfigureEye(Camera cam, StereoEye eye, bool stereo, bool swapEyes, float diagonalDeg, float aspect,
                                 float near, float far, float ipdMm, float convergenceM, int display)
        {
            Transform t = cam.transform;
            t.localPosition = new Vector3(stereo ? StereoMath.EyeOffsetM(ipdMm, eye) : 0f, 0f, 0f);
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            cam.targetDisplay = display;
            cam.rect = stereo ? StereoMath.EyeViewport(eye, swapEyes) : StereoMath.FullViewport;
            cam.nearClipPlane = near;                         // before the matrix, so nothing can overwrite it afterwards
            cam.farClipPlane = far;
            cam.projectionMatrix = StereoMath.EyeProjection(diagonalDeg, aspect, near, far,
                                                            stereo ? ipdMm : 0f, eye, stereo ? convergenceM : 0f);
            cam.enabled = stereo || eye == StereoEye.Left;    // 2D fallback: the left camera alone, full viewport, centred
        }

        void ApplyEyeColors()
        {
            if (!_built)
                return;
            _left.backgroundColor = debugEyeColors ? DebugLeftColor : Color.black;
            _right.backgroundColor = debugEyeColors ? DebugRightColor : Color.black;
        }

        static string Num(float value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        // A value in range, or the fallback when it is NaN / infinite; sets clamped when it had to change anything.
        static float Sane(float value, float min, float max, float fallback, ref bool clamped)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                clamped = true;
                return fallback < min ? min : (fallback > max ? max : fallback);
            }
            if (value < min)
            {
                clamped = true;
                return min;
            }
            if (value > max)
            {
                clamped = true;
                return max;
            }
            return value;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Display (standalone player only; the Editor's Game view "Display N" popup plays this role there)
        // ---------------------------------------------------------------------------------------------------------------

        void ActivateDisplayIfNeeded()
        {
            _displayWanted = false;
            if (Application.isEditor || !Application.isPlaying)
                return;                                       // Display.displays.Length is always 1 in the Editor
            int index = TargetDisplay;
            if (index <= 0)
                return;                                       // display 0 is the primary display and is always on

            Display[] displays = Display.displays;
            if (index >= displays.Length)
            {
                // Glasses not plugged in (yet). Keep polling once a second: the list updates by itself in a player.
                _displayWanted = true;
                _nextDisplayPollTime = Time.unscaledTime + DisplayPollSeconds;
                if (!_warnedMissingDisplay)
                {
                    _warnedMissingDisplay = true;
                    UGALog.Warn("stereo.display_missing", "render.glassesDisplay is " + index + " but only " + displays.Length
                        + " display(s) are connected: plug the glasses in and make them an extended (not mirrored) display in System Settings > Displays."
                        + " The eye cameras render to a display that is not there yet.");
                }
                return;
            }

            _warnedMissingDisplay = false;
            Display d = displays[index];
            if (!d.active)
            {
                d.Activate();                                 // once: "Once you activate a display, you can't deactivate it."
                UGALog.Event("stereo.display_activated", "displayIndex", index, "systemW", d.systemWidth, "systemH", d.systemHeight);
            }
            _sizeCheckPending = true;
            _sizeCheckTime = Time.unscaledTime + SizeCheckDelaySeconds;
        }

        void CheckDisplaySize()
        {
            _sizeCheckPending = false;
            int expectedW = IsStereo ? 2 * _eyeWidth : _eyeWidth;
            int expectedH = _eyeHeight;

            if (Application.isEditor)
            {
                // The Game view that shows this display decides the size. Informational only: it is also the size of the
                // left eye image times two when GlassesGameView is open, and something else when it is not.
                UGALog.Event("stereo.eye_pixels", "left", _left.pixelWidth + "x" + _left.pixelHeight,
                    "expectedEye", _eyeWidth + "x" + _eyeHeight, "displayIndex", TargetDisplay);
                return;
            }

            Display[] displays = Display.displays;
            if (TargetDisplay <= 0 || TargetDisplay >= displays.Length)
                return;
            Display d = displays[TargetDisplay];
            if (d.renderingWidth == expectedW && d.renderingHeight == expectedH)
            {
                UGALog.Event("stereo.display_ok", "displayIndex", TargetDisplay, "rendering", d.renderingWidth + "x" + d.renderingHeight);
                return;
            }
            UGALog.Warn("stereo.display_size", "display " + TargetDisplay + " renders " + d.renderingWidth + "x" + d.renderingHeight
                + " (native " + d.systemWidth + "x" + d.systemHeight + ") but " + (IsStereo ? "side-by-side stereo" : "the 2D fallback")
                + " needs " + expectedW + "x" + expectedH + ". " + (IsStereo
                    ? "Put the glasses in 3D mode (hold brightness-up about 3 s until it beeps) and choose 3840x1080 in System Settings > Displays, or set render.stereo to false."
                    : "Put the glasses in 2D mode and choose 1920x1080 in System Settings > Displays, or set render.stereo to true."));
        }
    }
}
