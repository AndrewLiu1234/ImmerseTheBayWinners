// SpectatorCam: what the judges watch on the Mac screen while one person wears the glasses.
//
// A mono camera on Unity display index 0 (the Mac screen) that renders the same virtual world the glasses show, from the
// wearer's head pose, framed exactly like one glasses eye (same vertical field of view, same 16:9 shape; letterboxed if the
// screen is a different shape, so the judges never see more than the wearer). Under it: the caption "glasses view (virtual
// layer)" and a large two-line status panel for live verdicts, readable from 1.5 m. A dark background is fine on this screen.
//
// SOURCES (read offline: /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Documentation/en):
//  * ScriptReference/Camera-targetDisplay.html: "Set the target display for this Camera. This setting makes a Camera render into
//    the specified display. Maximum number of displays (eg. monitors) supported is 8. This property has no effect on Android
//    and iOS platforms". Display index 0 is what the Game view calls "Display 1".
//  * Manual/MultiDisplay.html: "Display.displays[0] is the primary, default display and is always ON, so start at index 1."
//    So the spectator needs no Display.Activate(); only the glasses' display (index 1, the stereo rig's job) does.
//  * ScriptReference/MonoBehaviour.OnGUI.html: "Your OnGUI implementation might be called several times per frame (one call per
//    event)". So the panel is drawn only on the Repaint event.
//  * ScriptReference/MonoBehaviour-useGUILayout.html: "Disabling this lets you skip the GUI layout phase" (we use no GUILayout).
//  * ScriptReference/Display-activeEditorGameViewTarget.html: "Returns the last selected GameView's display target". That is the
//    last SELECTED Game view, not the one being drawn, so it cannot tell OnGUI which view it is painting; see IsSbsGlassesSurface.
//
// WHY IMGUI (OnGUI) AND NOT WORLD-SPACE TEXT: world-space text would be part of the scene, so the glasses' eye cameras would
// render it too unless they exclude a layer, and bright text in the glasses is exactly the failure to avoid. IMGUI belongs to
// the Game view, not the scene. It allocates nothing while the text is unchanged (styles and GUIContent are cached, the
// layout pass is skipped, only the Repaint event draws, fonts are re-fitted only when the text or the screen size changes).
//
// UNVERIFIED (nobody has run this in Unity yet, and the offline docs do not say): that IMGUI is drawn only into the Game
// view / window of display 0. If an Editor Game view showing the glasses' display receives the panel too, the defensive
// guard SpectatorLayout.IsSbsGlassesSurface suppresses it on a 3840x1080 surface; check it at L3.
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Spectator
{
    /// <summary>
    /// The spectator view. Create it once with <see cref="Create"/>, give it the wearer's head with <see cref="Head"/>, and
    /// push the live verdict text with <see cref="SetStatus"/>. Main thread only. Honours the spectator flag ("off" disables it)
    /// and switches itself off if the glasses' display index is also 0 (the two would fight over one screen).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class SpectatorCam : MonoBehaviour
    {
        /// <summary>The caption drawn on the view, as the plan words it (docs/MASTER_PLAN.md section 11).</summary>
        public const string CaptionText = "glasses view (virtual layer)";

        /// <summary>Unity display index of the Mac screen.</summary>
        public const int SpectatorDisplayIndex = 0;

        const string SimulatedTagText = "SIMULATED INPUT (WAV replay)";

        // The backdrop camera clears the whole screen to the dark colour (letterbox bars, panel area); the spectator camera
        // draws the glasses' frame on top. High depths so any stray default camera on display 0 is simply covered.
        const float BackdropDepth = 1000f;
        const float CameraDepth = 1001f;

        static readonly Color BackdropColor = new Color(0.02f, 0.025f, 0.05f, 1f);
        static readonly Color PlateColor = new Color(0.03f, 0.04f, 0.07f, 1f);
        static readonly Color AccentColor = new Color(0.310f, 0.765f, 0.969f, 1f);     // #4FC3F7
        static readonly Color InfoColor = new Color(0.702f, 0.898f, 0.988f, 1f);       // #B3E5FC
        static readonly Color AmberColor = new Color(1f, 0.757f, 0.027f, 1f);          // #FFC107
        static readonly Color TagPlateColor = new Color(0f, 0f, 0f, 0.6f);

        static readonly RenderFlags s_DefaultRender = new RenderFlags();

        Camera _cam;
        Camera _backdrop;
        bool _panelVisible = true;

        // status text
        string _line1 = string.Empty;
        string _line2 = string.Empty;
        readonly GUIContent _c1 = new GUIContent(string.Empty);
        readonly GUIContent _c2 = new GUIContent(string.Empty);
        readonly GUIContent _cCaption = new GUIContent(CaptionText);
        readonly GUIContent _cSim = new GUIContent(SimulatedTagText);

        // IMGUI state (created on the first OnGUI)
        GUIStyle _sLine1;
        GUIStyle _sLine2;
        GUIStyle _sCaption;
        GUIStyle _sSim;
        int _styleW = -1;
        int _styleH = -1;
        int _base1;
        int _base2;
        int _eff1;
        int _eff2;
        bool _fitDirty = true;
        Vector2 _capSize;
        Vector2 _simSize;
        string _simSource;
        bool _simShow;

        // mirrored flags
        string _spectatorFlag;
        bool _flagOn = true;
        int _glassesDisplay = 1;
        float _diagonalDeg = 52f;
        int _eyeW = 1920;
        int _eyeH = 1080;
        float _nearM = 0.1f;
        float _farM = 20f;
        bool _stereo = true;

        // derived state
        bool _active;
        bool _stateLogged;
        bool _lastLoggedActive;
        float _glassesAspect = SpectatorLayout.DefaultAspect;
        bool _viewportDirty = true;
        int _screenW;
        int _screenH;
        Rect _framePx;

        /// <summary>The wearer's head. The camera copies its world position and rotation every frame. Set by other code; may be null (the camera then stays where it is).</summary>
        public Transform Head { get; set; }

        /// <summary>The spectator camera (display index 0).</summary>
        public Camera Camera { get { return _cam; } }

        /// <summary>First status line, large (for example the verdict).</summary>
        public string Line1 { get { return _line1; } }

        /// <summary>Second status line, smaller (for example the tip).</summary>
        public string Line2 { get { return _line2; } }

        /// <summary>True while the spectator is actually rendering (flag on and no display conflict).</summary>
        public bool Active { get { return _active; } }

        /// <summary>
        /// Which layers the spectator renders. Default: everything. Set it to the same mask as the glasses' eye cameras so
        /// both show exactly the same virtual layer.
        /// </summary>
        public int CullingMask
        {
            get { return _cam.cullingMask; }
            set { _cam.cullingMask = value; }
        }

        /// <summary>Shows or hides the status panel (the camera then uses the whole screen). Default true.</summary>
        public bool PanelVisible
        {
            get { return _panelVisible; }
            set
            {
                if (_panelVisible == value) return;
                _panelVisible = value;
                _viewportDirty = true;
            }
        }

        /// <summary>Creates the spectator under <paramref name="parent"/> (may be null). No Inspector wiring needed.</summary>
        public static SpectatorCam Create(Transform parent)
        {
            var go = new GameObject("SpectatorCam");
            if (parent != null) go.transform.SetParent(parent, false);
            SpectatorCam spectator = go.AddComponent<SpectatorCam>();   // RequireComponent adds the Camera first
            spectator.Init();
            return spectator;
        }

        void Init()
        {
            useGUILayout = false;   // we draw with explicit rects only; skips the layout pass

            _cam = GetComponent<Camera>();
            ConfigureCamera(_cam, CameraDepth, true);

            var backdropObject = new GameObject("SpectatorBackdrop");
            backdropObject.transform.SetParent(transform, false);
            _backdrop = backdropObject.AddComponent<Camera>();
            ConfigureCamera(_backdrop, BackdropDepth, false);
            _backdrop.cullingMask = 0;   // renders nothing, only clears

            PollFlags(true);
            ApplyViewport(true);
            UGALog.Event("spectator_created", "display", SpectatorDisplayIndex, "displays", Display.displays.Length);
        }

        static void ConfigureCamera(Camera cam, float depth, bool drawsScene)
        {
            cam.targetDisplay = SpectatorDisplayIndex;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackdropColor;
            cam.depth = depth;
            cam.orthographic = false;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            if (drawsScene) cam.cullingMask = -1;
        }

        /// <summary>
        /// Sets the two status lines. Call it whenever the verdict changes (or every frame: unchanged text costs nothing).
        /// Null is treated as empty. Use plain words: the font has no check-mark or cross glyphs. Lines that are too long
        /// for the screen are shrunk to fit.
        /// </summary>
        public void SetStatus(string line1, string line2)
        {
            string a = line1 ?? string.Empty;
            string b = line2 ?? string.Empty;
            if (HudText.NeedsRebuild(_line1, a))
            {
                _line1 = a;
                _c1.text = a;
                _fitDirty = true;
            }
            if (HudText.NeedsRebuild(_line2, b))
            {
                _line2 = b;
                _c2.text = b;
                _fitDirty = true;
            }
        }

        void LateUpdate()
        {
            Transform head = Head;
            if (head != null) transform.SetPositionAndRotation(head.position, head.rotation);
            PollFlags(false);
            ApplyViewport(false);
        }

        // ------------------------------------------------------------------ flags and state

        void PollFlags(bool force)
        {
            FlagSet flags = FlagStore.Current;
            RenderFlags render = flags.render ?? s_DefaultRender;
            bool changed = force;

            string spectator = flags.spectator;
            if (!ReferenceEquals(spectator, _spectatorFlag))
            {
                _spectatorFlag = spectator;
                _flagOn = !SpectatorLayout.IsOffValue(spectator);
                changed = true;
            }
            if (render.glassesDisplay != _glassesDisplay) { _glassesDisplay = render.glassesDisplay; changed = true; }
            if (render.fovDiagonalDeg != _diagonalDeg) { _diagonalDeg = render.fovDiagonalDeg; changed = true; }
            if (render.eyeWidth != _eyeW || render.eyeHeight != _eyeH) { _eyeW = render.eyeWidth; _eyeH = render.eyeHeight; changed = true; }
            if (render.nearM != _nearM || render.farM != _farM) { _nearM = render.nearM; _farM = render.farM; changed = true; }
            _stereo = render.stereo;

            if (changed) ApplyState();
        }

        void ApplyState()
        {
            bool conflict = _glassesDisplay == SpectatorDisplayIndex;
            bool active = _flagOn && !conflict;
            _active = active;
            _cam.enabled = active;
            _backdrop.enabled = active;

            float near = Mathf.Max(0.01f, _nearM);
            _cam.nearClipPlane = near;
            _cam.farClipPlane = Mathf.Max(near + 0.1f, _farM);

            _glassesAspect = _eyeH > 0 && _eyeW > 0 ? _eyeW / (float)_eyeH : SpectatorLayout.DefaultAspect;
            float fov = SpectatorLayout.VerticalFovFromDiagonal(_diagonalDeg, _glassesAspect);
            if (fov > 1f && fov < 170f) _cam.fieldOfView = fov;
            _viewportDirty = true;

            if (!_stateLogged || active != _lastLoggedActive)
            {
                _stateLogged = true;
                _lastLoggedActive = active;
                UGALog.Event("spectator_state", "active", active, "flagOn", _flagOn, "glassesDisplay", _glassesDisplay);
            }
        }

        void ApplyViewport(bool force)
        {
            int w = Screen.width;
            int h = Screen.height;
            if (!force && !_viewportDirty && w == _screenW && h == _screenH) return;
            _screenW = w;
            _screenH = h;
            _viewportDirty = false;
            if (w <= 0 || h <= 0) return;

            float panelFraction = _panelVisible ? SpectatorLayout.PanelFraction : 0f;
            _cam.rect = SpectatorLayout.FrameViewportRect(w, h, _glassesAspect, panelFraction);
            _framePx = SpectatorLayout.FramePixelRect(w, h, _glassesAspect, panelFraction);
        }

        // ------------------------------------------------------------------ IMGUI: caption, status panel, simulated-input tag

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || !_active) return;

            int w = Screen.width;
            int h = Screen.height;
            if (w <= 0 || h <= 0) return;
            if (_stereo && SpectatorLayout.IsSbsGlassesSurface(w, h, _eyeW, _eyeH)) return;

            EnsureStyles(w, h);

            if (_panelVisible) DrawPanel(w, h);
            DrawCaption();
            DrawSimulatedTag();
        }

        void EnsureStyles(int w, int h)
        {
            if (_sLine1 == null)
            {
                _sLine1 = MakeStyle(Color.white);
                _sLine2 = MakeStyle(InfoColor);
                _sCaption = MakeStyle(InfoColor);
                _sSim = MakeStyle(AmberColor);
            }

            if (w != _styleW || h != _styleH)
            {
                _styleW = w;
                _styleH = h;
                int panelH = SpectatorLayout.PanelHeightPx(h);
                _base1 = SpectatorLayout.Line1FontPx(panelH);
                _base2 = SpectatorLayout.Line2FontPx(panelH);
                int captionPx = SpectatorLayout.CaptionFontPx(h);
                _sCaption.fontSize = captionPx;
                _sSim.fontSize = captionPx;
                _capSize = _sCaption.CalcSize(_cCaption);
                _simSize = _sSim.CalcSize(_cSim);
                _fitDirty = true;
            }

            if (_fitDirty)
            {
                _fitDirty = false;
                float margin = SpectatorLayout.PanelHeightPx(h) * 0.10f;
                float available = w - 2f * margin;

                _sLine1.fontSize = _base1;
                _eff1 = SpectatorLayout.FitFontSize(_base1, _sLine1.CalcSize(_c1).x, available, SpectatorLayout.MinLinePx);
                _sLine1.fontSize = _eff1;

                _sLine2.fontSize = _base2;
                _eff2 = SpectatorLayout.FitFontSize(_base2, _sLine2.CalcSize(_c2).x, available, SpectatorLayout.MinLinePx);
                _sLine2.fontSize = _eff2;
            }
        }

        static GUIStyle MakeStyle(Color color)
        {
            var style = new GUIStyle(GUI.skin.label);
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.MiddleLeft;
            style.wordWrap = false;
            style.richText = false;
            style.clipping = TextClipping.Clip;
            style.padding = new RectOffset(0, 0, 0, 0);
            style.margin = new RectOffset(0, 0, 0, 0);
            style.normal.textColor = color;
            return style;
        }

        void DrawPanel(int w, int h)
        {
            int panelH = SpectatorLayout.PanelHeightPx(h);
            float top = h - panelH;
            float margin = panelH * 0.10f;
            float available = w - 2f * margin;

            Fill(new Rect(0f, top, w, panelH), PlateColor);
            Fill(new Rect(0f, top, w, Mathf.Max(2f, h * 0.004f)), AccentColor);

            GUI.Label(new Rect(margin, top + panelH * 0.06f, available, panelH * 0.50f), _c1, _sLine1);
            GUI.Label(new Rect(margin, top + panelH * 0.57f, available, panelH * 0.38f), _c2, _sLine2);
        }

        void DrawCaption()
        {
            float pad = Mathf.Max(4f, _capSize.y * 0.35f);
            var plate = new Rect(_framePx.x + pad, _framePx.y + pad, _capSize.x + 2f * pad, _capSize.y + pad);
            Fill(plate, TagPlateColor);
            GUI.Label(new Rect(plate.x + pad, plate.y + pad * 0.5f, _capSize.x, _capSize.y), _cCaption, _sCaption);
        }

        void DrawSimulatedTag()
        {
            string source = FlagStore.Current.audioSource;
            if (!ReferenceEquals(source, _simSource))
            {
                _simSource = source;
                _simShow = SimulatedBadge.ShouldShow(source);
            }
            if (!_simShow) return;

            float pad = Mathf.Max(4f, _simSize.y * 0.35f);
            var plate = new Rect(_framePx.xMax - _simSize.x - 3f * pad, _framePx.y + pad, _simSize.x + 2f * pad, _simSize.y + pad);
            Fill(plate, TagPlateColor);
            GUI.Label(new Rect(plate.x + pad, plate.y + pad * 0.5f, _simSize.x, _simSize.y), _cSim, _sSim);
        }

        static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
