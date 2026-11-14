#if UNITY_EDITOR
// HudLearnPreview: renders the Learn HUD the way one eye of the glasses shows it, to PNG files, with no glasses and no Play Mode.
//
// Batch mode (do NOT pass -nographics: rendering needs the graphics device):
//   Unity -batchmode -quit -projectPath <repo>/unity -executeMethod UntitledGuitarApp.Editor.HudLearnPreview.RenderPreviews
//         -hudPreviewOut <folder> -logFile -
// In the editor: menu UGA/Render Learn HUD Previews. Output folder: the argument after -hudPreviewOut (default build/previews,
// relative to the Unity project folder). Nothing is saved to the project: each state is built in a temporary preview scene.
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
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Spectator;

namespace UntitledGuitarApp.Editor
{
    /// <summary>Renders preview images of the Learn HUD. Entry point for -executeMethod: <see cref="RenderPreviews"/>.</summary>
    public static class HudLearnPreview
    {
        public const string OutArgument = "-hudPreviewOut";
        public const string DefaultOutFolder = "build/previews";
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
            string folder = Render(Path.Combine(ProjectRoot, DefaultOutFolder));
            EditorUtility.RevealInFinder(Path.Combine(folder, ReportFileName));
        }

        static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        /// <summary>The folder named after -hudPreviewOut (relative paths are relative to the project folder), or the default.</summary>
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
            return Path.IsPathRooted(folder) ? folder : Path.Combine(projectRoot ?? string.Empty, folder);
        }

        // ------------------------------------------------------------------ the run

        static string Render(string folder)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("HudLearnPreview needs the graphics device: run Unity without -nographics.");

            Directory.CreateDirectory(folder);
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
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(texture);
                LearnGraphics.ReleaseShared();
            }

            report.AppendLine(problems == 0 ? "RESULT: no problems flagged" : "RESULT: " + problems + " problem(s) flagged (search for !!)");
            report.AppendLine("shape shader: " + (ShaderFallbackSeen ? "FALLBACK (font shader); HudLearn.shader did not load" : "HudLearn.shader"));
            string reportPath = Path.Combine(folder, ReportFileName);
            File.WriteAllText(reportPath, report.ToString());
            Debug.Log("[HudLearnPreview] wrote 16 PNG files and " + ReportFileName + " to " + folder + " (" + problems + " problem(s) flagged)");
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
