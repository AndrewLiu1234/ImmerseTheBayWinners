#if UNITY_EDITOR
// HudLearnPreview: renders the Learn HUD the way one eye of the glasses shows it, to PNG files, with no glasses and no Play Mode.
//
// Batch mode (do NOT pass -nographics: rendering needs the graphics device):
//   Unity -batchmode -quit -projectPath <repo>/unity -executeMethod UntitledGuitarApp.Editor.HudLearnPreview.RenderPreviews
//         -hudPreviewOut <folder> -logFile -
// In the editor: menu UGA/Render Learn HUD Previews (no dialogs; the user's open scene is never touched). Output folder: the
// argument after -hudPreviewOut, default <repo>/build/previews/learn (a relative folder is taken from the repository root, the
// folder above unity/, like the neck preview); the folder is logged. Nothing is saved to the project: each state is built in
// a temporary preview scene (EditorSceneManager.NewPreviewScene).
//
// COMPOSED PASS (composed-*.png, composed-contact-sheet.png, composed-report.json): the floating neck with the chord's X / O
// marks and dots, the fretting ghost hand (the Blender-posed hand when its FBX and poses load, else the capsule hand: the JSON
// says which) and the Learn HUD, driven by LearnPresenter and the SIMULATED DevLearnScript through every moment of a run (tune,
// finger by finger, the pluck check with its deliberate mistake, the retry, the strum), seen by the glasses' LEFT eye (head at
// the origin, eye half the IPD to the left). Then two largeText frames of the judge profile (tools/flags/judge.json: Em,
// largeText) and two PLANTED frames that must be flagged: the old behaviour (learn marks in their own row beside the neck's
// X / O) and the message card moved onto the hand. The check is RELATIONAL: every visible learn part (text at its TRUE asked
// size, see TextTrueSizeInflation; shapes) against every neck X / O mark, finger number and dot, against the hand's rendered
// silhouette (a mask from a hand-only render) and against the other learn parts. The run throws (batch exit 1) when a
// planted frame is NOT flagged.
//
// For each state it writes <name>-black.png (exactly the frame the glasses get: black is what they do not show) and
// <name>-grey.png (the same light added to a mid-grey background, in linear light, the way an additive display adds to
// the room), plus preview-report.txt with numbers a reviewer can check without looking: lit pixels, whether anything
// touches the frame edge, every text's size in degrees and whether its size was really measured, and whether any two HUD
// parts overlap in the frame (see CheckOverlaps; added after a run whose report was clean while the tuner's words sat on
// its note name).
//
// The camera is one eye at the head centre: 1920x1080, 52 degree diagonal field of view (26.9 vertical). Neck states use
// a seated right-handed player's typical eye position (LearnHudLayout.TypicalEyeInNeckFrame) looking at fret 2, and draw
// a faint neck (strings, frets, nut) ONLY as a reference for the reviewer; that guide is not part of the HUD.
//
// SOURCES (read offline: /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Documentation/en):
//  * Manual/EditorCommandLineArguments.html, -executeMethod: "To return an error from the command line process, either throw
//    an exception which causes Unity to exit with return code 1, or call EditorApplication.Exit with a non-zero return code.
//    To pass parameters, add them to the command line and retrieve them inside the function using
//    System.Environment.GetCommandLineArgs." -nographics: "Unity doesn't initialize the graphics device".
//  * ScriptReference/SceneManagement.EditorSceneManager.NewPreviewScene.html: "Creates a new preview Scene. Any object added to
//    a preview Scene will only be rendered in that Scene." ClosePreviewScene: "Closes a preview Scene created by NewPreviewScene."
//  * ScriptReference/Camera-scene.html: "If not null, the camera will only render the contents of the specified Scene.
//    Currently, only Scenes created by EditorSceneManager.NewPreviewScene are supported."
//  * ScriptReference/SceneManagement.SceneManager.MoveGameObjectToScene.html: "You can only move root GameObjects".
//  * ScriptReference/Texture2D.ReadPixels.html: "Reads the pixels from the current render target (the screen, or a
//    RenderTexture), and writes them to the texture ... (0,0) is lower left."
//  * ScriptReference/RenderTextureReadWrite.html: "When using Gamma color space, no conversions are done of any kind ... When
//    Linear color space is used, then by default non-HDR render textures are considered to contain sRGB data". So the bytes
//    read back are display (sRGB) values in both colour spaces.
//  * ScriptReference/Camera-worldToCameraMatrix.html: "Matrix that transforms from world to camera space ... Note that camera
//    space matches OpenGL convention: camera's forward is the negative Z axis." Camera-projectionMatrix.html: the camera's
//    projection ("projection matrix passed to shaders can be modified depending on platform", so the GPU variant is NOT used
//    here). Together they put a point in clip space, with w > 0 in front of the camera.
//  * ScriptReference/Renderer-localBounds.html: "the axis-aligned bounding box fully enclosing the object in local space".
//    Mesh-bounds.html: "the axis-aligned bounding box of the mesh in its local space (that is, not affected by the transform)".
//
// RUN: in batch mode on Metal by the coordinator on 2026-11-14 (16 PNGs; that run predates the overlap check). In Edit Mode
// Update / LateUpdate do not run, so this script advances the widgets itself (LearnWidget.Tick) and renders each state
// twice: the first render lets the font fill its texture, then every size is applied again (HudText measures its real
// glyphs), then the frame that is saved is rendered.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.GhostHand;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Practice.Learn;
using UntitledGuitarApp.Runtime.Practice.Learn.Dev;
using UntitledGuitarApp.Runtime.Spectator;
using CoreNeckLayout = UntitledGuitarApp.Runtime.Neck.CoreNeckLayout;
using FingertipDot = UntitledGuitarApp.Runtime.Neck.FingertipDot;
using FloatingNeck = UntitledGuitarApp.Runtime.Neck.FloatingNeck;
using NeckLabel = UntitledGuitarApp.Runtime.Neck.NeckLabel;
using NeckLayout = UntitledGuitarApp.Runtime.Neck.NeckLayout;
using NeckMark = UntitledGuitarApp.Runtime.Neck.StringMark;
using NeckMath = UntitledGuitarApp.Runtime.Neck.NeckMath;

namespace UntitledGuitarApp.Editor
{
    /// <summary>Renders preview images of the Learn HUD. Entry point for -executeMethod: <see cref="RenderPreviews"/>.</summary>
    public static class HudLearnPreview
    {
        public const string OutArgument = "-hudPreviewOut";
        public const string DefaultOutFolder = "build/previews/learn";   // relative to the repository root
        public const string MenuPath = "UGA/Render Learn HUD Previews";
        public const string ReportFileName = "preview-report.txt";

        /// <summary>One eye of the glasses.</summary>
        public const int EyeWidth = 1920;
        public const int EyeHeight = 1080;
        public const float DiagonalFovDegrees = 52f;

        /// <summary>The "room" behind the additive image in the -grey pictures (sRGB value).</summary>
        public const byte MidGrey = 128;

        const float Step = 1f / 60f;
        const int SettleFrames = 40;          // 0.67 s: ease-ins done, tuner dwell done, a clean verdict still showing
        const byte LitThreshold = 24;
        const float GazeAlongNeckM = 0.07f;   // the player looks at about fret 2

        sealed class Rig
        {
            public GameObject Root;
            public Transform Head;
            public Transform Socket;
            public Camera Camera;
            public LearnHud Hud;
            public LearnWidget[] Widgets;
            public bool FeedTuner;
            public float TunerCents;
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>For -executeMethod. Throws on failure, which makes a batch run exit with code 1.</summary>
        public static void RenderPreviews()
        {
            string folder = ResolveOutFolder(Environment.GetCommandLineArgs(), ProjectRoot);
            Render(folder);
        }

        [MenuItem(MenuPath, false, 210)]
        static void RenderFromMenu()
        {
            Render(ResolveOutFolder(null, ProjectRoot));
        }

        static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        /// <summary>
        /// The folder named after -hudPreviewOut, or the default build/previews/learn. A relative folder is taken from the
        /// repository root (the folder above <paramref name="projectRoot"/>, the Unity project), like the neck preview's.
        /// </summary>
        public static string ResolveOutFolder(string[] args, string projectRoot)
        {
            string folder = DefaultOutFolder;
            if (args != null)
            {
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (!string.Equals(args[i], OutArgument, StringComparison.OrdinalIgnoreCase)) continue;
                    string value = args[i + 1];
                    if (!string.IsNullOrEmpty(value) && !value.StartsWith("-", StringComparison.Ordinal)) folder = value;
                    break;
                }
            }
            if (Path.IsPathRooted(folder)) return folder;
            string repoRoot = string.IsNullOrEmpty(projectRoot) ? string.Empty : Path.GetDirectoryName(Path.GetFullPath(projectRoot));
            return Path.GetFullPath(Path.Combine(repoRoot ?? string.Empty, folder));
        }

        // ------------------------------------------------------------------ the run

        static string Render(string folder)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("HudLearnPreview needs the graphics device: run Unity without -nographics.");

            Directory.CreateDirectory(folder);
            Debug.Log("[HudLearnPreview] rendering into " + folder);
            float verticalFov = SpectatorLayout.VerticalFovFromDiagonal(DiagonalFovDegrees, EyeWidth / (float)EyeHeight);

            var report = new StringBuilder(8192);
            FlagSet flags = FlagStore.Current;
            report.AppendLine("HudLearnPreview report");
            report.AppendLine("unity " + Application.unityVersion + " | colour space " + QualitySettings.activeColorSpace
                + " | graphics " + SystemInfo.graphicsDeviceType + " | batch mode " + Application.isBatchMode);
            report.AppendLine("eye " + EyeWidth + "x" + EyeHeight + ", vertical field of view " + F(verticalFov) + " deg (from "
                + F(DiagonalFovDegrees) + " diagonal); grey background sRGB " + MidGrey + ", added in linear light");
            report.AppendLine("flags: largeText=" + flags.largeText + " debugOverlay=" + flags.debugOverlay
                + " ghost.fingerColors=" + (flags.ghost != null && flags.ghost.fingerColors));
            report.AppendLine("folder: " + folder);
            report.AppendLine();

            byte[] overGrey = BuildOverGreyTable(MidGrey);
            var texture = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            RenderTexture target = RenderTexture.GetTemporary(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32);
            int problems = 0;
            ComposedResult composed = null;
            try
            {
                problems += RenderState("learn-01-tuner-flat", false, folder, verticalFov, target, texture, overGrey, report,
                    rig => StartTuner(rig, -22f));
                problems += RenderState("learn-02-tuner-in-tune", false, folder, verticalFov, target, texture, overGrey, report,
                    rig => StartTuner(rig, 1.5f));
                problems += RenderState("learn-03-tuner-sharp", false, folder, verticalFov, target, texture, overGrey, report,
                    rig => StartTuner(rig, 31f));
                problems += RenderState("learn-04-pluck-g-muted-b", true, folder, verticalFov, target, texture, overGrey, report,
                    rig => rig.Hud.ShowPluck(2, StringMark.Muted, "Arch finger 3, B string muted", TipIcon.Arch));
                problems += RenderState("learn-05-strum-clean", true, folder, verticalFov, target, texture, overGrey, report,
                    rig => rig.Hud.ShowStrum(true, null));
                problems += RenderState("learn-06-finger-2-of-3", true, folder, verticalFov, target, texture, overGrey, report,
                    rig => rig.Hud.ShowFingerStep(2, 5, 2, 2, 3));
                problems += RenderState("learn-07-strum-issue", true, folder, verticalFov, target, texture, overGrey, report,
                    rig => rig.Hud.ShowStrum(false, "Heard C, target is G"));
                problems += RenderState("learn-08-all-marks", true, folder, verticalFov, target, texture, overGrey, report,
                    ShowAllMarks);
                composed = RenderComposedPass(folder, verticalFov, target, texture, overGrey, report);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(texture);
                LearnGraphics.ReleaseShared();
            }

            report.AppendLine(problems == 0 ? "RESULT: no problems flagged" : "RESULT: " + problems + " problem(s) flagged (search for !!)");
            if (composed != null)
                report.AppendLine("COMPOSED: " + composed.Frames + " frames, " + composed.RealOverlaps + " overlap(s) in real frames, planted "
                    + composed.PlantedFired + "/" + composed.Planted + " flagged" + (composed.Error != null ? ", FAILED: " + composed.Error : ""));
            report.AppendLine("shape shader: " + (ShaderFallbackSeen ? "FALLBACK (font shader); HudLearn.shader did not load" : "HudLearn.shader"));
            string reportPath = Path.Combine(folder, ReportFileName);
            File.WriteAllText(reportPath, report.ToString());
            Debug.Log("[HudLearnPreview] wrote 16 PNG files, " + (composed != null ? composed.Frames * 2 + 1 : 0) + " composed PNG files, "
                + ReportFileName + " and " + ComposedJsonName + " to " + folder + " (" + problems + " problem(s) flagged; composed: "
                + (composed != null ? composed.RealOverlaps + " overlap(s), planted " + composed.PlantedFired + "/" + composed.Planted : "not run") + ")");
            if (composed == null || composed.Error != null || composed.PlantedFired < composed.Planted)
                throw new InvalidOperationException("HudLearnPreview composed pass: " + (composed == null ? "did not run"
                    : composed.Error ?? "a PLANTED overlap was not flagged, so the overlap check cannot be trusted") + " (see " + reportPath + ")");
            return folder;
        }

        static bool ShaderFallbackSeen;

        static void StartTuner(Rig rig, float cents)
        {
            rig.Hud.Tuner.SetTarget(6, "E");
            rig.FeedTuner = true;
            rig.TunerCents = cents;
        }

        // One of every mark, to review the shapes side by side (not a state the app shows in normal use).
        static void ShowAllMarks(Rig rig)
        {
            StringMarks marks = rig.Hud.Marks;
            marks.Show(6, StringMark.Correct, float.PositiveInfinity);
            marks.Show(5, StringMark.Wrong);
            marks.Show(4, StringMark.Muted);
            marks.Show(3, StringMark.Unclear);
            marks.Show(2, StringMark.LetRing);
            rig.Hud.ShowTip("Don't strum string 6", TipIcon.Skip);
        }

        static int RenderState(string name, bool withNeck, string folder, float verticalFov, RenderTexture target, Texture2D texture,
            byte[] overGrey, StringBuilder report, Action<Rig> setup)
        {
            int problems = 0;
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                root = new GameObject("HudLearnPreview " + name);
                SceneManager.MoveGameObjectToScene(root, scene);
                Rig rig = BuildRig(root, scene, withNeck, verticalFov);

                setup(rig);
                rig.Widgets = root.GetComponentsInChildren<LearnWidget>(true);
                Advance(rig, SettleFrames);
                RenderCamera(rig.Camera, target);                    // first render: fills the font texture, builds the text meshes
                for (int i = 0; i < rig.Widgets.Length; i++) rig.Widgets[i].RefreshSizes();
                Advance(rig, 1);
                RenderCamera(rig.Camera, target);                    // the frame that is saved

                Color32[] pixels = ReadBack(target, texture);
                report.AppendLine("--- " + name);
                problems += DescribePixels(pixels, verticalFov, report);
                problems += DescribeContent(rig, report);
                problems += CheckOverlaps(rig, report);

                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                WritePng(Path.Combine(folder, name + "-black.png"), pixels, texture);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    pixels[i] = new Color32(overGrey[p.r], overGrey[p.g], overGrey[p.b], 255);
                }
                WritePng(Path.Combine(folder, name + "-grey.png"), pixels, texture);
                report.AppendLine();
                if (LearnGraphics.UsingFallbackShader) ShaderFallbackSeen = true;
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
            return problems;
        }

        // ------------------------------------------------------------------ the scene for one state

        static Rig BuildRig(GameObject root, Scene scene, bool withNeck, float verticalFov)
        {
            var rig = new Rig();
            rig.Root = root;

            var head = new GameObject("Head");
            head.transform.SetParent(root.transform, false);
            rig.Head = head.transform;

            var eye = new GameObject("Eye");
            eye.transform.SetParent(head.transform, false);
            Camera camera = eye.AddComponent<Camera>();
            camera.enabled = false;                                  // rendered by hand, never by the editor loop
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            camera.orthographic = false;
            camera.fieldOfView = verticalFov;
            camera.aspect = EyeWidth / (float)EyeHeight;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 20f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.cullingMask = -1;
            camera.scene = scene;
            rig.Camera = camera;

            if (withNeck)
            {
                var socket = new GameObject("NeckSocket");          // identity: the neck frame is the world frame here
                socket.transform.SetParent(root.transform, false);
                rig.Socket = socket.transform;

                Vector3 eyePosition = LearnHudLayout.TypicalEyeInNeckFrame;
                var gaze = new Vector3(GazeAlongNeckM, 0.03f, 0f);
                Vector3 forward, up;
                LearnHudLayout.TryFacing(eyePosition, gaze, LearnHudLayout.TypicalUpInNeckFrame, out forward, out up);
                head.transform.SetPositionAndRotation(eyePosition, Quaternion.LookRotation(forward, up));
                DrawNeckGuide(root.transform, eyePosition);
            }

            rig.Hud = LearnHud.Create(root.transform, rig.Head, rig.Socket);
            if (withNeck)
            {
                var nutEnds = new Vector3[StringMarks.StringCount];
                for (int s = 1; s <= StringMarks.StringCount; s++) nutEnds[s - 1] = new Vector3(0f, 0f, StringZAtNut(s));
                rig.Hud.SetNeck(rig.Socket, nutEnds, LearnHudLayout.FretDistanceM(LearnHudLayout.DefaultScaleLengthM, LearnHudLayout.CardFret));
            }
            return rig;
        }

        static void Advance(Rig rig, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                if (rig.FeedTuner) rig.Hud.Tuner.SetReading(rig.TunerCents, 0.95f);
                for (int i = 0; i < rig.Widgets.Length; i++) rig.Widgets[i].Tick(Step);
            }
            rig.Hud.Marks.PlaceNow();
            rig.Hud.PlaceCard();
        }

        // ------------------------------------------------------------------ the reference neck (not part of the HUD)

        const float NutSpacingM = 0.007f;            // string to string at the nut (35 mm E to E)
        const float Fret12SpacingM = 0.0089f;        // and at fret 12, on the way to about 54 mm at the saddle

        static float StringZAtNut(int stringNumber)
        {
            return (3.5f - stringNumber) * NutSpacingM;          // +z is toward string 1
        }

        static void DrawNeckGuide(Transform root, Vector3 eye)
        {
            var guide = new GameObject("NeckGuide (reference only, not HUD)");
            guide.transform.SetParent(root, false);
            float fret12 = LearnHudLayout.FretDistanceM(LearnHudLayout.DefaultScaleLengthM, 12);

            for (int s = 1; s <= StringMarks.StringCount; s++)
            {
                var from = new Vector3(0f, 0f, StringZAtNut(s));
                var to = new Vector3(fret12, 0f, (3.5f - s) * Fret12SpacingM);
                GuideLine(guide.transform, "String" + s, from, to, 0.0007f, eye, 0.55f);
            }
            for (int fret = 0; fret <= 12; fret++)
            {
                float x = LearnHudLayout.FretDistanceM(LearnHudLayout.DefaultScaleLengthM, fret);
                float half = Mathf.Lerp(0.0215f, 0.028f, x / fret12);
                bool nut = fret == 0;
                GuideLine(guide.transform, nut ? "Nut" : "Fret" + fret, new Vector3(x, 0f, -half), new Vector3(x, 0f, half),
                    nut ? 0.004f : 0.0012f, eye, nut ? 0.8f : 0.4f);
            }
        }

        // A thin bright bar from a to b, turned about its own axis to face the eye (a flat line on the fretboard would be seen edge-on).
        static void GuideLine(Transform parent, string name, Vector3 a, Vector3 b, float width, Vector3 eye, float opacity)
        {
            Vector3 d = b - a;
            float length = d.magnitude;
            if (!(length > 1e-6f)) return;
            d /= length;
            Vector3 mid = 0.5f * (a + b);
            Vector3 view = mid - eye;
            Vector3 normal = view - Vector3.Dot(view, d) * d;
            if (!(normal.sqrMagnitude > 1e-10f)) return;
            normal.Normalize();

            HudShape line = HudShape.Create(parent, name, LearnGlyph.Line);
            line.Color = LearnPalette.Quiet;
            line.Opacity = opacity;
            line.SortingOrder = -10;
            line.transform.SetPositionAndRotation(mid, Quaternion.LookRotation(normal, Vector3.Cross(normal, d)));
            line.transform.localScale = new Vector3(length, width, 1f);
        }

        // ------------------------------------------------------------------ rendering and files

        static void RenderCamera(Camera camera, RenderTexture target)
        {
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;
        }

        static Color32[] ReadBack(RenderTexture target, Texture2D texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0f, 0f, EyeWidth, EyeHeight), 0, 0, false);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            return texture.GetPixels32();
        }

        static void WritePng(string path, Color32[] pixels, Texture2D texture)
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }

        // What an sRGB value from the display looks like when its light is ADDED to a grey room: decode both to linear
        // light, add, encode again. One table, because the background is a constant.
        static byte[] BuildOverGreyTable(byte grey)
        {
            var table = new byte[256];
            float room = SrgbToLinear(grey / 255f);
            for (int v = 0; v < 256; v++)
            {
                float sum = Mathf.Min(1f, SrgbToLinear(v / 255f) + room);
                table[v] = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToSrgb(sum) * 255f), 0, 255);
            }
            return table;
        }

        static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        // ------------------------------------------------------------------ the report

        static int DescribePixels(Color32[] pixels, float verticalFov, StringBuilder report)
        {
            int lit = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < EyeHeight; y++)
            {
                int row = y * EyeWidth;
                for (int x = 0; x < EyeWidth; x++)
                {
                    Color32 p = pixels[row + x];
                    if (p.r <= LitThreshold && p.g <= LitThreshold && p.b <= LitThreshold) continue;
                    lit++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (lit == 0)
            {
                report.AppendLine("  !! BLANK: no lit pixels at all");
                return 1;
            }

            float focal = 0.5f * EyeHeight / Mathf.Tan(0.5f * verticalFov * Mathf.Deg2Rad);
            float left = Mathf.Atan((minX - 0.5f * EyeWidth) / focal) * Mathf.Rad2Deg;
            float right = Mathf.Atan((maxX - 0.5f * EyeWidth) / focal) * Mathf.Rad2Deg;
            float bottom = Mathf.Atan((minY - 0.5f * EyeHeight) / focal) * Mathf.Rad2Deg;   // pixel rows are read bottom-up
            float top = Mathf.Atan((maxY - 0.5f * EyeHeight) / focal) * Mathf.Rad2Deg;
            report.AppendLine("  lit pixels: " + lit + " (" + F(100f * lit / pixels.Length) + "% of the frame)");
            report.AppendLine("  lit area: " + F(left) + " to " + F(right) + " deg across, " + F(bottom) + " to " + F(top) + " deg up (0 = centre of view)");

            bool touches = minX <= 1 || minY <= 1 || maxX >= EyeWidth - 2 || maxY >= EyeHeight - 2;
            if (touches) report.AppendLine("  note: lit pixels reach the frame edge (the reference neck runs out of view in neck states; check that no HUD text is cut)");
            return 0;
        }

        static int DescribeContent(Rig rig, StringBuilder report)
        {
            int problems = 0;
            Vector3 eye = rig.Camera.transform.position;
            HudText[] texts = rig.Root.GetComponentsInChildren<HudText>(true);
            int hidden = 0;
            for (int i = 0; i < texts.Length; i++)
            {
                HudText text = texts[i];
                if (!text.Visible || text.Text.Length == 0)
                {
                    hidden++;
                    continue;
                }

                float distance = Vector3.Distance(eye, text.transform.position);
                float capDegrees = HudText.AngularHeightForSize(text.CapHeightMeters, distance);
                var renderer = text.GetComponent<MeshRenderer>();
                Vector3 box = renderer != null ? renderer.localBounds.size : Vector3.zero;
                Vector3 scale = text.transform.lossyScale;
                float boxW = HudText.AngularHeightForSize(box.x * scale.x, distance);
                float boxH = HudText.AngularHeightForSize(box.y * scale.y, distance);
                float lines = 1f + CountChar(text.Text, '\n');
                float ratio = text.CapHeightMeters > 0f ? box.y * scale.y / (text.CapHeightMeters * lines) : 0f;

                var flagsText = new StringBuilder();
                if (capDegrees < HudText.MinTextDegrees - 0.02f)
                {
                    flagsText.Append(" !! SMALLER THAN " + F(HudText.MinTextDegrees) + " deg");
                    problems++;
                }
                if (!text.SizeMeasured)
                {
                    flagsText.Append(" !! SIZE NOT MEASURED (HudText fallback scale in use)");
                    problems++;
                }
                if (ratio < 0.9f || ratio > 2.4f)
                {
                    flagsText.Append(" !! BOX/CAP RATIO " + F(ratio) + " (a text line is normally 1.1 to 1.7 cap heights tall)");
                    problems++;
                }

                report.AppendLine("  text \"" + text.Text + "\" cap " + F(capDegrees) + " deg at " + F(distance) + " m, box "
                    + F(boxW) + " x " + F(boxH) + " deg [" + text.name + "]" + flagsText);
            }
            report.AppendLine("  (" + hidden + " hidden or empty texts not listed)");

            HudShape[] shapes = rig.Root.GetComponentsInChildren<HudShape>(true);
            int shown = 0;
            for (int i = 0; i < shapes.Length; i++)
            {
                HudShape shape = shapes[i];
                if (!shape.Visible || shape.Opacity <= 0f || shape.Glyph == LearnGlyph.Line) continue;
                shown++;
                float distance = Vector3.Distance(eye, shape.transform.position);
                report.AppendLine("  shape " + shape.Glyph + " " + F(HudText.AngularHeightForSize(shape.Size, distance)) + " deg, brightness "
                    + F(shape.Opacity) + " [" + shape.name + "]");
            }
            if (shown == 0 && texts.Length - hidden == 0)
            {
                report.AppendLine("  !! NOTHING SHOWN by the HUD in this state");
                problems++;
            }
            return problems;
        }

        // ------------------------------------------------------------------ overlap check
        //
        // Every visible HUD part (texts and shapes, not the reference neck) becomes the quadrilateral that its own bounds box
        // (Renderer.localBounds for a text, the mesh bounds for a shape) covers in the saved frame, projected through the eye
        // camera's own matrices. Two parts whose quadrilaterals overlap by more than OverlapTolerancePx (separating-axis
        // test, so rotated parts are judged by their real outline, not by a screen-aligned box) are flagged with !!, except
        // pairs that overlap by design: tuner dial parts among themselves (the needle crosses the track), the finger number
        // in its ring, and a string mark with that string's own number (they cross-fade in one place).

        const float OverlapTolerancePx = 1.5f;

        sealed class ScreenPart
        {
            public string Kind;      // composed pass: "neck X/O", "neck finger number", "neck dot" (null for HUD parts)
            public string Name;
            public string Label;
            public Transform Parent;
            public bool Dial;
            public readonly Vector2[] Quad = new Vector2[4];
        }

        static int CheckOverlaps(Rig rig, StringBuilder report)
        {
            var parts = new List<ScreenPart>();
            HudText[] texts = rig.Root.GetComponentsInChildren<HudText>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                HudText text = texts[i];
                if (!text.Visible || text.Text.Length == 0) continue;
                var renderer = text.GetComponent<MeshRenderer>();
                if (renderer == null) continue;
                AddScreenPart(parts, rig.Camera, text.transform, renderer.localBounds, false, "text \"" + text.Text + "\" [" + text.name + "]");
            }
            HudShape[] shapes = rig.Root.GetComponentsInChildren<HudShape>(true);
            for (int i = 0; i < shapes.Length; i++)
            {
                HudShape shape = shapes[i];
                if (!shape.Visible || shape.Opacity <= 0f || shape.Glyph == LearnGlyph.Line) continue;
                var filter = shape.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                AddScreenPart(parts, rig.Camera, shape.transform, filter.sharedMesh.bounds, !LearnGlyphs.FitsUnitBox(shape.Glyph),
                    "shape " + shape.Glyph + " [" + shape.name + "]");
            }

            float pixelsPerDegree = EyeHeight / rig.Camera.fieldOfView;
            int problems = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                for (int j = i + 1; j < parts.Count; j++)
                {
                    ScreenPart a = parts[i];
                    ScreenPart b = parts[j];
                    if (OverlapByDesign(a, b)) continue;
                    float depth = OverlapDepthPx(a.Quad, b.Quad);
                    if (depth <= OverlapTolerancePx) continue;
                    report.AppendLine("  !! OVERLAP: " + a.Label + " and " + b.Label + " (" + F(depth / pixelsPerDegree) + " deg deep)");
                    problems++;
                }
            }
            if (problems == 0) report.AppendLine("  overlap check: " + parts.Count + " visible parts, no two overlap");
            return problems;
        }

        static void AddScreenPart(List<ScreenPart> parts, Camera camera, Transform t, Bounds local, bool dial, string label)
        {
            var part = new ScreenPart { Name = t.name, Label = label, Parent = t.parent, Dial = dial };
            Vector3 min = local.min;
            Vector3 max = local.max;
            float z = local.center.z;
            if (!ToEyePixels(camera, t.TransformPoint(new Vector3(min.x, min.y, z)), out part.Quad[0])) return;
            if (!ToEyePixels(camera, t.TransformPoint(new Vector3(max.x, min.y, z)), out part.Quad[1])) return;
            if (!ToEyePixels(camera, t.TransformPoint(new Vector3(max.x, max.y, z)), out part.Quad[2])) return;
            if (!ToEyePixels(camera, t.TransformPoint(new Vector3(min.x, max.y, z)), out part.Quad[3])) return;
            parts.Add(part);
        }

        // A world point in pixels of the saved eye frame (origin bottom left), through the camera's own view and projection
        // matrices, so the answer does not depend on the editor's screen size. False for a point behind the eye.
        static bool ToEyePixels(Camera camera, Vector3 world, out Vector2 pixel)
        {
            Vector4 clip = camera.projectionMatrix * camera.worldToCameraMatrix * new Vector4(world.x, world.y, world.z, 1f);
            pixel = Vector2.zero;
            if (!(clip.w > 1e-6f)) return false;
            pixel = new Vector2((clip.x / clip.w * 0.5f + 0.5f) * EyeWidth, (clip.y / clip.w * 0.5f + 0.5f) * EyeHeight);
            return true;
        }

        static bool OverlapByDesign(ScreenPart a, ScreenPart b)
        {
            if (a.Dial && b.Dial) return true;
            if (IsPair(a.Name, b.Name, "FingerRing", "FingerNumber")) return true;
            return a.Parent == b.Parent && IsPair(a.Name, b.Name, "Mark", "Number");
        }

        static bool IsPair(string a, string b, string first, string second)
        {
            return (a == first && b == second) || (a == second && b == first);
        }

        /// <summary>
        /// How deep two convex quadrilaterals overlap, in pixels: the smallest overlap of their shadows over the edge
        /// normals of both (separating-axis test). 0 or less = they do not overlap. Pure.
        /// </summary>
        public static float OverlapDepthPx(Vector2[] a, Vector2[] b)
        {
            float depth = float.MaxValue;
            for (int pass = 0; pass < 2; pass++)
            {
                Vector2[] polygon = pass == 0 ? a : b;
                for (int i = 0; i < polygon.Length; i++)
                {
                    Vector2 edge = polygon[(i + 1) % polygon.Length] - polygon[i];
                    var axis = new Vector2(-edge.y, edge.x);
                    float length = axis.magnitude;
                    if (!(length > 1e-6f)) continue;
                    axis /= length;
                    float minA, maxA, minB, maxB;
                    Shadow(a, axis, out minA, out maxA);
                    Shadow(b, axis, out minB, out maxB);
                    float overlap = Mathf.Min(maxA, maxB) - Mathf.Max(minA, minB);
                    if (overlap <= 0f) return overlap;
                    if (overlap < depth) depth = overlap;
                }
            }
            return depth == float.MaxValue ? 0f : depth;
        }

        static void Shadow(Vector2[] polygon, Vector2 axis, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                float d = Vector2.Dot(polygon[i], axis);
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }

        // ------------------------------------------------------------------ composed pass (see the file header)

        public const string ComposedJsonName = "composed-report.json";
        public const string ComposedSheetName = "composed-contact-sheet.png";

        /// <summary>
        /// Factor the composed overlap check applies to every drawn text box (about the text's anchor) to judge the layout at
        /// the TRUE asked size. 1: since person1/sl0-hud-text, HudText draws at the asked size (from a measured constant).
        /// </summary>
        public const float TextTrueSizeInflation = 1f;

        /// <summary>Hand-silhouette pixels a learn part may cover before it counts as an overlap (about a 5 x 5 px corner).</summary>
        public const int HandPixelTolerance = 24;

        const int ThumbScale = 4, SheetColumns = 4;

        sealed class ComposedResult
        {
            public int Frames, RealOverlaps, Planted, PlantedFired;
            public string Error;
            public readonly List<string> FrameJson = new List<string>();
            public Color32[] Sheet;
            public int SheetRows, Index;
        }

        sealed class ComposedRig
        {
            public GameObject Root;
            public Transform Head;
            public Camera Eye;
            public FloatingNeck Neck;
            public LearnHud Hud;
            public LearnPresenter Presenter;
            public DevLearnScript Script;
            public PosedGhostHand Posed;
            public CapsuleHand Capsule;
            public LearnWidget[] Widgets;
        }

        delegate void ComposedTweak(ComposedRig rig);

        static ComposedResult RenderComposedPass(string folder, float verticalFov, RenderTexture target, Texture2D texture, byte[] overGrey,
            StringBuilder report)
        {
            var result = new ComposedResult();
            report.AppendLine("=== COMPOSED PASS: floating neck + fretting ghost hand + Learn HUD, LearnPresenter + SIMULATED DevLearnScript, left eye");
            report.AppendLine("text boxes judged at " + F(TextTrueSizeInflation) + "x their drawn size (HudText draws at the asked size)");
            report.AppendLine();
            const int Planned = 16;   // the most frames this pass renders (main run + judge + planted), for the contact sheet
            result.SheetRows = (Planned + SheetColumns - 1) / SheetColumns;
            result.Sheet = new Color32[SheetColumns * (EyeWidth / ThumbScale) * result.SheetRows * (EyeHeight / ThumbScale)];
            for (int i = 0; i < result.Sheet.Length; i++) result.Sheet[i] = new Color32(0, 0, 0, 255);   // opaque: unused cells stay black
            try
            {
                // 1. The run the app would show with the current flags (G unless FlagSet.chords says otherwise), every moment.
                ComposedRun(result, "composed", null, false, null, null, folder, verticalFov, target, texture, overGrey, report);

                // 2. The judge profile (tools/flags/judge.json: Em, largeText) over the real defaults, two moments.
                FlagsForJudge(folder, report, () =>
                    ComposedRun(result, "composed-judge-largetext", new[] { "finger-3", "pluck-" }, false, null, null, folder, verticalFov,
                        target, texture, overGrey, report));

                // 3. PLANTED: the old behaviour (learn marks in their own row while the neck keeps its X / O) must be flagged.
                ComposedRun(result, "composed-planted-old-two-rows", new[] { "pluck-" }, true, rig => rig.Presenter.ShareNeckColumn = false,
                    null, folder, verticalFov, target, texture, overGrey, report);

                // 4. PLANTED: the message card moved onto the fretting hand must be flagged against the hand's silhouette.
                ComposedRun(result, "composed-planted-card-on-hand", new[] { "finger-2" }, true, null, PutCardOnHand,
                    folder, verticalFov, target, texture, overGrey, report);
            }
            catch (Exception e)
            {
                result.Error = e.GetType().Name + ": " + e.Message;
                report.AppendLine("  !! COMPOSED PASS FAILED: " + e);
            }

            WriteContactSheet(Path.Combine(folder, ComposedSheetName), result, texture);
            var json = new StringBuilder(8192);
            json.Append("{\n  \"generatedBy\": \"UntitledGuitarApp.Editor.HudLearnPreview (composed pass)\",\n");
            json.Append("  \"unity\": ").Append(Q(Application.unityVersion)).Append(",\n");
            json.Append("  \"eye\": \"left, ").Append(EyeWidth).Append("x").Append(EyeHeight).Append(", vertical fov ").Append(F(verticalFov)).Append(" deg\",\n");
            json.Append("  \"textTrueSizeInflation\": ").Append(F(TextTrueSizeInflation)).Append(",\n");
            json.Append("  \"overlapTolerancePx\": ").Append(F(OverlapTolerancePx)).Append(",\n");
            json.Append("  \"handPixelTolerance\": ").Append(HandPixelTolerance).Append(",\n");
            json.Append("  \"contactSheet\": ").Append(Q(ComposedSheetName)).Append(", \"contactSheetOrder\": \"row-major, in the order of frames\",\n");
            json.Append("  \"realOverlaps\": ").Append(result.RealOverlaps).Append(",\n");
            json.Append("  \"planted\": ").Append(result.Planted).Append(", \"plantedFlagged\": ").Append(result.PlantedFired).Append(",\n");
            json.Append("  \"error\": ").Append(result.Error == null ? "null" : Q(result.Error)).Append(",\n");
            json.Append("  \"frames\": [\n");
            for (int i = 0; i < result.FrameJson.Count; i++) json.Append(result.FrameJson[i]).Append(i + 1 < result.FrameJson.Count ? ",\n" : "\n");
            json.Append("  ]\n}\n");
            File.WriteAllText(Path.Combine(folder, ComposedJsonName), json.ToString());
            report.AppendLine("composed: " + result.Frames + " frames; " + result.RealOverlaps + " overlap(s) in real frames; planted flagged "
                + result.PlantedFired + "/" + result.Planted);
            report.AppendLine();
            return result;
        }

        // The judge profile, as scripts/use-flags.sh would install it: the real defaults with tools/flags/judge.json laid over
        // them (falling back to the two judge values that matter here). The editor's own flags are put back afterwards.
        static void FlagsForJudge(string folder, StringBuilder report, Action body)
        {
            bool wasInitialized = FlagStore.IsInitialized;
            string oldDefaults = FlagStore.DefaultsPath, oldOverride = FlagStore.OverridePath;
            string defaults = Path.Combine(Application.streamingAssetsPath, FlagStore.DefaultsFileName);
            string judge = Path.GetFullPath(Path.Combine(ProjectRoot, "..", "tools", "flags", "judge.json"));
            if (!File.Exists(judge))
            {
                judge = Path.Combine(folder, "composed-judge-flags.json");
                File.WriteAllText(judge, "{ \"profile\": \"judge\", \"anchorMode\": \"headLocked\", \"chords\": [\"Em\"], \"largeText\": true }\n");
            }
            try
            {
                FlagStore.Initialize(defaults, judge);
                report.AppendLine("judge flags: " + judge + " over " + defaults + " -> chords " + (FlagStore.Current.chords != null
                    ? string.Join(",", FlagStore.Current.chords) : "(none)") + ", largeText " + FlagStore.Current.largeText);
                body();
            }
            finally
            {
                if (wasInitialized) FlagStore.Initialize(oldDefaults, oldOverride);
                else FlagStore.ResetForTests();
            }
        }

        // The message moved onto the middle of the hand (zone card: its left edge 2 deg left of the hand's centre as the head
        // sees it; neck card: the card spot at the hand's centre), so it must land on the hand's silhouette.
        static void PutCardOnHand(ComposedRig rig)
        {
            GameObject hand = rig.Presenter.HandObject;
            if (hand == null) return;
            Renderer[] renderers = hand.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length == 0) return;
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            rig.Hud.CardLocal = rig.Neck.Socket.InverseTransformPoint(b.center);
            Vector3 local = rig.Head.InverseTransformPoint(b.center);
            float az = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float el = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            if (rig.Hud.UsesZoneCard) rig.Hud.UseZoneCard(az - 2f, 90f, -90f, el, LearnHudLayout.ZoneCardDistanceM);
        }

        // One learn run in a fresh preview scene, captured at its moments (all of them, or the first whose name starts with each
        // of momentPrefixes). planted = the frames must be flagged. setup runs before the run starts, atMoment before each capture.
        static void ComposedRun(ComposedResult result, string prefix, string[] momentPrefixes, bool planted, ComposedTweak setup,
            ComposedTweak atMoment, string folder,
            float verticalFov, RenderTexture target, Texture2D texture, byte[] overGrey, StringBuilder report)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                root = new GameObject("HudLearnPreview " + prefix);
                SceneManager.MoveGameObjectToScene(root, scene);
                ComposedRig rig = BuildComposedRig(root, scene, verticalFov);
                if (setup != null) setup(rig);
                if (!rig.Script.Play()) throw new InvalidOperationException("DevLearnScript.Play failed (no chord from the flags?)");
                DevLearnTimeline run = rig.Script.Timeline;
                report.AppendLine("--- " + prefix + ": chord " + run.Chord.Name + ", hand " + rig.Presenter.HandKind + ", largeText "
                    + FlagStore.Current.largeText + ", mistake \"" + run.MistakeTip + "\", run " + F(run.Duration) + " s");

                var moments = new List<DevLearnMoment>();
                for (int i = 0; i < run.Moments.Count; i++)
                {
                    if (momentPrefixes == null) { moments.Add(run.Moments[i]); continue; }
                    for (int k = 0; k < momentPrefixes.Length; k++)
                    {
                        if (!run.Moments[i].name.StartsWith(momentPrefixes[k], StringComparison.Ordinal)) continue;
                        bool taken = false;
                        for (int m = 0; m < moments.Count; m++) taken |= moments[m].name.StartsWith(momentPrefixes[k], StringComparison.Ordinal);
                        if (!taken) moments.Add(run.Moments[i]);
                    }
                }

                float clock = 0f;
                for (int m = 0; m < moments.Count; m++)
                {
                    while (clock + Step <= moments[m].time)
                    {
                        StepComposed(rig, Step);
                        clock += Step;
                    }
                    if (atMoment != null)
                    {
                        atMoment(rig);
                        StepComposed(rig, Step);
                    }
                    string name = prefix + "-" + (result.Index + 1).ToString("00", CultureInfo.InvariantCulture) + "-" + run.Chord.Name + "-" + moments[m].name;
                    CaptureComposed(result, rig, name, moments[m], planted, folder, target, texture, overGrey, report);
                }
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        static ComposedRig BuildComposedRig(GameObject root, Scene scene, float verticalFov)
        {
            var rig = new ComposedRig { Root = root };
            var head = new GameObject("Head");                       // the wearer's head at rest: origin, looking along +z
            head.transform.SetParent(root.transform, false);
            rig.Head = head.transform;

            var eye = new GameObject("LeftEye");
            eye.transform.SetParent(head.transform, false);
            FlagSet flags = FlagStore.Current;
            float ipd = flags.render != null && flags.render.ipdMm > 0f ? flags.render.ipdMm * 0.001f : 0.063f;
            eye.transform.localPosition = new Vector3(-0.5f * ipd, 0f, 0f);
            Camera camera = eye.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            camera.fieldOfView = verticalFov;
            camera.aspect = EyeWidth / (float)EyeHeight;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 20f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.scene = scene;
            rig.Eye = camera;

            // As Bootstrap composes it: the neck from Core's geometry, hanging in front of the head; the hand on its socket.
            rig.Neck = FloatingNeck.Create(root.transform);
            NeckLayout layout = CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry);
            if (!rig.Neck.Build(layout)) throw new InvalidOperationException("the Core neck layout failed NeckLayout.Validate");
            rig.Neck.PlaceFloating(rig.Head, true);

            try
            {
                var library = new GhostPoseLibrary();
                library.Reload();
                PosedGhostHand posed = PosedGhostHand.Create(rig.Neck.Socket, GhostPose.HandFretting, library);
                if (posed != null && posed.IsAvailable) rig.Posed = posed;
                else if (posed != null) UnityEngine.Object.DestroyImmediate(posed.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HudLearnPreview] the posed ghost hand could not be built (" + e.Message + "); using the capsule hand");
            }
            if (rig.Posed == null) rig.Capsule = CapsuleHand.Create(rig.Neck.Socket, true);

            rig.Hud = LearnHud.Create(root.transform, rig.Head, rig.Neck.Socket);
            var nutEnds = new Vector3[StringMarks.StringCount];
            for (int s = 1; s <= StringMarks.StringCount; s++)
                nutEnds[s - 1] = NeckMath.LocalMetres(0f, layout.stringHeightMm, layout.stringZAtNutMm[s - 1]);
            float cardFret = layout.fretXMm.Length > LearnHudLayout.CardFret ? layout.fretXMm[LearnHudLayout.CardFret] * NeckMath.MetresPerMm : 0f;
            rig.Hud.SetNeck(rig.Neck.Socket, nutEnds, cardFret);

            rig.Presenter = LearnPresenter.Create(root.transform, rig.Hud, rig.Neck, rig.Posed, rig.Capsule);
            rig.Script = DevLearnScript.Create(root.transform, rig.Presenter);
            rig.Script.HandleKeys = false;
            rig.Widgets = root.GetComponentsInChildren<LearnWidget>(true);
            return rig;
        }

        // What Update and LateUpdate do in Play Mode, in their order (Edit Mode runs neither).
        static void StepComposed(ComposedRig rig, float dt)
        {
            rig.Script.Advance(dt);
            rig.Presenter.Tick(dt);
            rig.Hud.Tick(dt);
            for (int i = 0; i < rig.Widgets.Length; i++) rig.Widgets[i].Tick(dt);
            if (rig.Posed != null) rig.Posed.Tick(dt);
            if (rig.Capsule != null) rig.Capsule.Tick(dt);
            rig.Presenter.LateTick();
            rig.Hud.Marks.PlaceNow();
            rig.Hud.PlaceCard();
        }

        static void CaptureComposed(ComposedResult result, ComposedRig rig, string name, DevLearnMoment moment, bool planted, string folder,
            RenderTexture target, Texture2D texture, byte[] overGrey, StringBuilder report)
        {
            RenderCamera(rig.Eye, target);                                  // fills the font texture
            for (int i = 0; i < rig.Widgets.Length; i++) rig.Widgets[i].RefreshSizes();
            StepComposed(rig, Step);

            int handPixels;
            bool[] handMask = RenderHandMask(rig, target, texture, out handPixels);
            RenderCamera(rig.Eye, target);
            Color32[] pixels = ReadBack(target, texture);

            report.AppendLine("--- " + name + " (stage " + rig.Presenter.Stage + (planted ? ", PLANTED: must be flagged" : "") + ")");
            var overlaps = new List<string>();
            int learnParts, neckParts;
            CheckComposedOverlaps(rig, handMask, overlaps, out learnParts, out neckParts);
            for (int i = 0; i < overlaps.Count; i++) report.AppendLine("  " + (planted ? "(planted) " : "!! ") + "OVERLAP: " + overlaps[i]);
            report.AppendLine("  " + learnParts + " learn parts, " + neckParts + " neck parts (X/O, finger numbers, dots), hand silhouette "
                + handPixels + " px (" + rig.Presenter.HandKind + "): " + overlaps.Count + " overlap(s)");

            result.Frames++;
            if (planted)
            {
                result.Planted++;
                if (overlaps.Count > 0) result.PlantedFired++;
                else report.AppendLine("  !! PLANTED OVERLAP NOT FLAGGED: the check is blind to this case");
            }
            else
            {
                result.RealOverlaps += overlaps.Count;
            }

            for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
            AddThumbnail(result, pixels);
            WritePng(Path.Combine(folder, name + "-black.png"), pixels, texture);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                pixels[i] = new Color32(overGrey[p.r], overGrey[p.g], overGrey[p.b], 255);
            }
            WritePng(Path.Combine(folder, name + "-grey.png"), pixels, texture);
            report.AppendLine();

            var j = new StringBuilder(1024);
            j.Append("    { \"name\": ").Append(Q(name)).Append(", \"png\": ").Append(Q(name + "-black.png"))
             .Append(", \"chord\": ").Append(Q(rig.Presenter.Chord != null ? rig.Presenter.Chord.Name : ""))
             .Append(", \"moment\": ").Append(Q(moment.name)).Append(", \"stage\": ").Append(Q(rig.Presenter.Stage.ToString()))
             .Append(", \"largeText\": ").Append(FlagStore.Current.largeText ? "true" : "false")
             .Append(", \"hand\": ").Append(Q(rig.Presenter.HandKind)).Append(", \"marksInNeckColumn\": ").Append(rig.Presenter.MarksDocked ? "true" : "false")
             .Append(", \"planted\": ").Append(planted ? "true" : "false")
             .Append(", \"flagged\": ").Append(overlaps.Count > 0 ? "true" : "false")
             .Append(", \"learnParts\": ").Append(learnParts).Append(", \"neckParts\": ").Append(neckParts).Append(", \"handPixels\": ").Append(handPixels)
             .Append(", \"overlaps\": [");
            for (int i = 0; i < overlaps.Count; i++) j.Append(i > 0 ? ", " : "").Append(Q(overlaps[i]));
            j.Append("] }");
            result.FrameJson.Add(j.ToString());
        }

        // A render of the hand alone (every other renderer switched off for one frame): its lit pixels are its silhouette.
        static bool[] RenderHandMask(ComposedRig rig, RenderTexture target, Texture2D texture, out int count)
        {
            var mask = new bool[EyeWidth * EyeHeight];
            count = 0;
            GameObject hand = rig.Presenter.HandObject;
            if (hand == null || !hand.activeInHierarchy) return mask;
            Renderer[] all = rig.Root.GetComponentsInChildren<Renderer>(false);
            var was = new bool[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                was[i] = all[i].enabled;
                all[i].enabled = was[i] && all[i].transform.IsChildOf(hand.transform);
            }
            try
            {
                RenderCamera(rig.Eye, target);
                Color32[] px = ReadBack(target, texture);
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 p = px[i];
                    if (p.r <= LitThreshold && p.g <= LitThreshold && p.b <= LitThreshold) continue;
                    mask[i] = true;
                    count++;
                }
            }
            finally
            {
                for (int i = 0; i < all.Length; i++) all[i].enabled = was[i];
            }
            return mask;
        }

        static void CheckComposedOverlaps(ComposedRig rig, bool[] handMask, List<string> overlaps, out int learnCount, out int neckCount)
        {
            Camera camera = rig.Eye;
            var learn = new List<ScreenPart>();
            HudText[] texts = rig.Root.GetComponentsInChildren<HudText>(false);
            for (int i = 0; i < texts.Length; i++)
            {
                HudText text = texts[i];
                if (!text.Visible || text.Text.Length == 0) continue;
                var renderer = text.GetComponent<MeshRenderer>();
                if (renderer == null) continue;
                Bounds b = renderer.localBounds;
                var trueSize = new Bounds(b.center * TextTrueSizeInflation, b.size * TextTrueSizeInflation);   // about the text's anchor
                AddScreenPart(learn, camera, text.transform, trueSize, false, "learn text \"" + text.Text.Replace('\n', ' ') + "\" [" + text.name + "]");
            }
            HudShape[] shapes = rig.Root.GetComponentsInChildren<HudShape>(false);
            for (int i = 0; i < shapes.Length; i++)
            {
                HudShape shape = shapes[i];
                if (!shape.Visible || shape.Opacity <= 0f || shape.Glyph == LearnGlyph.Line) continue;
                var filter = shape.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                AddScreenPart(learn, camera, shape.transform, filter.sharedMesh.bounds, !LearnGlyphs.FitsUnitBox(shape.Glyph),
                    "learn shape " + shape.Glyph + " [" + shape.transform.parent.name + "/" + shape.name + "]");
            }

            var neck = new List<ScreenPart>();
            FloatingNeck n = rig.Neck;
            if (n != null && n.gameObject.activeInHierarchy && n.Layout != null)
            {
                for (int s = 1; s <= StringMarks.StringCount; s++)
                {
                    NeckMark mark = rig.Presenter.NeckMarkShown(s);
                    Vector3 c, along, across;
                    float half;
                    if (mark == NeckMark.None || !rig.Presenter.TryGetColumnSlot(s, out c, out along, out across, out half)) continue;
                    AddWorldQuad(neck, camera, "neck X/O", "neck " + (mark == NeckMark.Open ? "O" : "X") + " string " + s,
                        c - (along + across) * half, c + (along - across) * half, c + (along + across) * half, c + (across - along) * half);
                }
                NeckLabel[] labels = n.GetComponentsInChildren<NeckLabel>(false);
                for (int i = 0; i < labels.Length; i++)
                {
                    if (!labels[i].Visible) continue;
                    MeshRenderer mr = labels[i].GetComponentInChildren<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;
                    int before = neck.Count;
                    AddScreenPart(neck, camera, mr.transform, mr.localBounds, false, "neck finger number [" + labels[i].transform.parent.name + "]");
                    if (neck.Count > before) neck[neck.Count - 1].Kind = "neck finger number";
                }
                float r = n.Sizes.haloRadiusMm;
                float h = n.Layout.stringHeightMm;
                for (int i = 0; i < rig.Presenter.DotCount; i++)
                {
                    FingertipDot d = rig.Presenter.Dot(i);
                    Transform f = n.Socket;
                    AddWorldQuad(neck, camera, "neck dot", "neck dot finger " + d.finger + " string " + d.stringNumber,
                        f.TransformPoint(NeckMath.LocalMetres(d.xMm - r, h, d.zMm - r)), f.TransformPoint(NeckMath.LocalMetres(d.xMm + r, h, d.zMm - r)),
                        f.TransformPoint(NeckMath.LocalMetres(d.xMm + r, h, d.zMm + r)), f.TransformPoint(NeckMath.LocalMetres(d.xMm - r, h, d.zMm + r)));
                }
            }
            learnCount = learn.Count;
            neckCount = neck.Count;

            float pixelsPerDegree = EyeHeight / camera.fieldOfView;
            for (int i = 0; i < learn.Count; i++)
            {
                for (int k = 0; k < neck.Count; k++)
                {
                    float depth = OverlapDepthPx(learn[i].Quad, neck[k].Quad);
                    if (depth > OverlapTolerancePx)
                        overlaps.Add(neck[k].Kind + ": " + learn[i].Label + " on " + neck[k].Label + " (" + F(depth / pixelsPerDegree) + " deg deep)");
                }
                int covered = CountMaskInQuad(handMask, learn[i].Quad);
                if (covered > HandPixelTolerance) overlaps.Add("hand: " + learn[i].Label + " covers " + covered + " px of the ghost hand");
                for (int k = i + 1; k < learn.Count; k++)
                {
                    if (OverlapByDesign(learn[i], learn[k])) continue;
                    float depth = OverlapDepthPx(learn[i].Quad, learn[k].Quad);
                    if (depth > OverlapTolerancePx)
                        overlaps.Add("learn: " + learn[i].Label + " on " + learn[k].Label + " (" + F(depth / pixelsPerDegree) + " deg deep)");
                }
            }
        }

        static void AddWorldQuad(List<ScreenPart> parts, Camera camera, string kind, string label, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var part = new ScreenPart { Kind = kind, Name = label, Label = label };
            if (!ToEyePixels(camera, a, out part.Quad[0]) || !ToEyePixels(camera, b, out part.Quad[1])
                || !ToEyePixels(camera, c, out part.Quad[2]) || !ToEyePixels(camera, d, out part.Quad[3])) return;
            parts.Add(part);
        }

        /// <summary>How many set pixels of <paramref name="mask"/> (EyeWidth x EyeHeight, row 0 at the bottom) lie inside a convex quad. Pure.</summary>
        static int CountMaskInQuad(bool[] mask, Vector2[] quad)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                minX = Mathf.Min(minX, quad[i].x); maxX = Mathf.Max(maxX, quad[i].x);
                minY = Mathf.Min(minY, quad[i].y); maxY = Mathf.Max(maxY, quad[i].y);
            }
            int x0 = Mathf.Max(0, Mathf.FloorToInt(minX)), x1 = Mathf.Min(EyeWidth - 1, Mathf.CeilToInt(maxX));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(minY)), y1 = Mathf.Min(EyeHeight - 1, Mathf.CeilToInt(maxY));
            int count = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!mask[y * EyeWidth + x]) continue;
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    bool pos = false, neg = false;
                    for (int i = 0; i < 4; i++)
                    {
                        Vector2 e = quad[(i + 1) % 4] - quad[i];
                        float cross = e.x * (p.y - quad[i].y) - e.y * (p.x - quad[i].x);
                        if (cross > 0f) pos = true; else if (cross < 0f) neg = true;
                    }
                    if (!(pos && neg)) count++;
                }
            }
            return count;
        }

        static void AddThumbnail(ComposedResult result, Color32[] pixels)
        {
            int tw = EyeWidth / ThumbScale, th = EyeHeight / ThumbScale;
            int index = result.Index++;
            if (index >= SheetColumns * result.SheetRows) return;
            int col = index % SheetColumns, row = index / SheetColumns;
            int sheetW = SheetColumns * tw;
            int baseY = (result.SheetRows - 1 - row) * th;   // pixel rows run bottom-up: the first frame goes top left
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < ThumbScale; dy++)
                    {
                        int srow = (y * ThumbScale + dy) * EyeWidth;
                        for (int dx = 0; dx < ThumbScale; dx++)
                        {
                            Color32 p = pixels[srow + x * ThumbScale + dx];
                            r += p.r; g += p.g; b += p.b;
                        }
                    }
                    int n = ThumbScale * ThumbScale;
                    bool edge = x == 0 || y == 0;                     // a faint frame line between thumbnails
                    result.Sheet[(baseY + y) * sheetW + col * tw + x] = edge ? new Color32(60, 60, 60, 255)
                        : new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
        }

        static void WriteContactSheet(string path, ComposedResult result, Texture2D scratch)
        {
            if (result.Sheet == null || result.Index == 0) return;
            int w = SheetColumns * (EyeWidth / ThumbScale), h = result.SheetRows * (EyeHeight / ThumbScale);
            var sheet = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                sheet.SetPixels32(result.Sheet);
                sheet.Apply(false, false);
                File.WriteAllBytes(path, sheet.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder(s.Length + 2);
            b.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        static int CountChar(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == c) n++;
            return n;
        }

        static string F(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
#endif
