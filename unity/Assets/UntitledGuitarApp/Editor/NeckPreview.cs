// NeckPreview: renders the floating neck with the dev G chord to PNG files, without Play Mode and without the glasses, so
// its size and legibility can be judged from pictures. Run it with the editor closed (do NOT pass -nographics: it renders):
//
//   Unity -batchmode -quit -projectPath unity -executeMethod UntitledGuitarApp.Editor.NeckPreview.RenderPreviews
//         -neckPreviewOut build/previews -logFile build/neck-preview.log
//
// or, in an open editor, menu Untitled Guitar App > Neck > Render Previews. Output folder: the argument after
// -neckPreviewOut (default build/previews; a relative path is taken from the repository root, the folder above unity/).
// Every picture is taken from a seated player's eye (the head at the origin, looking along +z):
//   neck_glasses_eye_1920x1080.png   exactly one eye of the glasses: 1920 x 1080 over the 52 degree diagonal field of view
//                                    (about 46 x 27 degrees), over black. What the wearer sees; judge size and legibility here.
//   neck_closeup_black.png           the same eye, zoomed in on the neck, over black (what the glasses add to the world)
//   neck_closeup_grey.png            the same over mid-grey: anything that is not pure added light shows up here
//   neck_glasses_eye_lefthanded.png  the eye view with the neck mirrored for a left-handed player
//   neck_closeup_fingercolors.png    per-finger colours on, finger 1 active (the others at 30%)
//   neck_closeup_c_chord.png         the C chord: X and O marks together, the widest fan of strings to the X / O row
//   neck_preview.txt                 the sizes in millimetres, degrees and glasses pixels
// The close-ups frame everything from the X / O row beyond the nut to the last fret.
// The scene is a temporary preview scene: it is never saved, and the scene open in the editor is not touched.
// On failure the process exits with code 1 in batch mode; inside an open editor it throws instead (never closes the editor).
//
// Sources (Unity 2021.3.3f1 offline docs, /Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/):
//   SceneManagement.EditorSceneManager.NewPreviewScene.html   "Creates a new preview Scene. Any object added to a preview Scene
//                                                             will only be rendered in that Scene."  (+ ClosePreviewScene.html)
//   Camera-scene.html             "If not null, the camera will only render the contents of the specified Scene. Currently, only
//                                 Scenes created by EditorSceneManager.NewPreviewScene are supported."
//   SceneManagement.SceneManager.MoveGameObjectToScene.html   root GameObjects only
//   Camera.Render.html            "Render the camera manually ... It will use the camera's clear flags, target texture and all
//                                 other settings." Its example reads the result back with Texture2D.ReadPixels, as here.
//   Camera-fieldOfView.html       "The vertical field of view of the Camera, in degrees."
//   CameraType.Preview.html       "a camera that is used for rendering previews in the Editor"
//   RenderTexture.GetTemporary.html, Texture2D.ReadPixels.html, ImageConversion.EncodeToPNG.html
//   ShaderUtil-allowAsyncCompilation.html   false = "force synchronous compilation", so no picture shows a placeholder shader
//   EditorApplication.Exit.html, Application-isBatchMode.html (the same failure pattern as BuildScript.cs)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;

namespace UntitledGuitarApp.Editor
{
    public static class NeckPreview
    {
        public const string OutArgument = "-neckPreviewOut";
        public const string DefaultOutFolder = "build/previews";

        // The glasses: one eye is 1920 x 1080 over a 52 degree diagonal (XREAL Air 2 Ultra).
        const float GlassesDiagonalFovDeg = 52f;
        const int EyeWidth = 1920;
        const int EyeHeight = 1080;
        const float CloseupVerticalFovDeg = 10.5f;
        const string SceneObjectName = "FloatingNeck (dev preview, not saved)";

        // Appendix B2 row for C, string 6 -> 1 (-1 = x, 0 = open; finger 0 = none): X on string 6, O on strings 3 and 1.
        static readonly int[] CFrets = { -1, 3, 2, 0, 1, 0 };
        static readonly int[] CFingers = { 0, 3, 2, 0, 1, 0 };

        /// <summary>Entry point for -executeMethod and for the menu.</summary>
        [MenuItem("Untitled Guitar App/Neck/Render Previews")]
        public static void RenderPreviews()
        {
            string failure = null;
            List<string> written = null;
            string folder = null;
            try
            {
                folder = ResolveOutFolder(Environment.GetCommandLineArgs());
                written = Render(folder);
            }
            catch (Exception e)
            {
                failure = e.ToString();
            }

            if (failure == null)
            {
                Debug.Log("[NeckPreview] wrote " + written.Count + " file(s) to " + folder + ":\n  " + string.Join("\n  ", written));
                return;
            }
            Debug.LogError("[NeckPreview] FAILED: " + failure);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            else
                throw new InvalidOperationException("Neck preview failed: " + failure);
        }

        /// <summary>
        /// Puts the dev neck (G chord) into the scene that is open in the editor, 0.9 m in front of the world origin, so it can
        /// be looked at from any side in the Scene view. It is marked DontSave: it never ends up in a saved scene.
        /// </summary>
        [MenuItem("Untitled Guitar App/Neck/Show Dev Neck In Scene")]
        public static void ShowDevNeckInScene()
        {
            RemoveDevNeckFromScene();
            FloatingNeck neck = CreateDevNeck();
            neck.gameObject.name = SceneObjectName;
            neck.PlaceFloating(null);
            foreach (Transform t in neck.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.DontSave;
            Selection.activeGameObject = neck.gameObject;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
        }

        [MenuItem("Untitled Guitar App/Neck/Remove Dev Neck From Scene")]
        public static void RemoveDevNeckFromScene()
        {
            GameObject existing = GameObject.Find(SceneObjectName);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        }

        /// <summary>The folder after -neckPreviewOut, or the default; relative paths start at the repository root.</summary>
        public static string ResolveOutFolder(string[] commandLine)
        {
            string folder = DefaultOutFolder;
            if (commandLine != null)
            {
                for (int i = 0; i < commandLine.Length - 1; i++)
                {
                    if (commandLine[i] == OutArgument && !string.IsNullOrEmpty(commandLine[i + 1]))
                    {
                        folder = commandLine[i + 1];
                        break;
                    }
                }
            }
            if (Path.IsPathRooted(folder)) return folder;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);   // <repo>/unity
            string repoRoot = Path.GetDirectoryName(projectRoot);               // <repo>
            return Path.Combine(repoRoot, folder);
        }

        static FloatingNeck CreateDevNeck()
        {
            NeckLayout layout = DevNeckLayout.Create();
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int count = DevNeckLayout.GChord(layout, dots, marks);

            FloatingNeck neck = FloatingNeck.Create(null);
            neck.UseOptions(new NeckOptions());   // right-handed, white dots, normal text, no debug axes: whatever the flags file says
            if (!neck.Build(layout))
                throw new InvalidOperationException("the dev layout was rejected: " + string.Join(" | ", layout.Validate()));
            neck.SetDots(dots, count);
            neck.SetStringMarks(marks);
            return neck;
        }

        static List<string> Render(string folder)
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();

            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;

                // The seated player's head: at the origin, looking along +z. The neck is placed relative to it.
                var head = new GameObject("PreviewHead");
                SceneManager.MoveGameObjectToScene(head, scene);

                FloatingNeck neck = CreateDevNeck();
                SceneManager.MoveGameObjectToScene(neck.gameObject, scene);
                neck.PlaceFloating(head.transform);

                var cameraObject = new GameObject("PreviewEye");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera eye = cameraObject.AddComponent<Camera>();
                eye.enabled = false;                       // rendered by hand with Camera.Render
                eye.cameraType = CameraType.Preview;
                eye.scene = scene;
                eye.clearFlags = CameraClearFlags.SolidColor;
                eye.nearClipPlane = 0.05f;
                eye.farClipPlane = 10f;
                eye.allowHDR = false;
                eye.allowMSAA = false;                     // worst case for thin lines: only the shader's own anti-aliasing

                float horizontalFov, verticalFov;
                NeckMath.FovFromDiagonal(GlassesDiagonalFovDeg, EyeWidth / (float)EyeHeight, out horizontalFov, out verticalFov);
                Color black = new Color(0f, 0f, 0f, 1f);
                Color grey = new Color(0.5f, 0.5f, 0.5f, 1f);

                // 1. One eye of the glasses.
                eye.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                eye.fieldOfView = verticalFov;
                written.Add(Save(eye, black, Path.Combine(folder, "neck_glasses_eye_1920x1080.png")));

                // 2 and 3. Zoomed in on the fretted part of the neck, from the same eye.
                AimCloseup(eye, neck);
                written.Add(Save(eye, black, Path.Combine(folder, "neck_closeup_black.png")));
                written.Add(Save(eye, grey, Path.Combine(folder, "neck_closeup_grey.png")));

                // 4. Left-handed: the mirror image.
                neck.UseOptions(new NeckOptions { leftHanded = true });
                eye.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                eye.fieldOfView = verticalFov;
                written.Add(Save(eye, black, Path.Combine(folder, "neck_glasses_eye_lefthanded.png")));

                // 5. Per-finger colours, finger 1 active.
                neck.UseOptions(new NeckOptions { fingerColors = true });
                neck.SetActiveFinger(1);
                AimCloseup(eye, neck);
                written.Add(Save(eye, black, Path.Combine(folder, "neck_closeup_fingercolors.png")));
                neck.SetActiveFinger(-1);
                neck.UseOptions(new NeckOptions());

                // 6. The C chord: an X and two O marks, at both ends of the row.
                var cDots = new FingertipDot[FloatingNeck.MaxDots];
                var cMarks = new StringMark[NeckLayout.StringCount];
                int cCount = DevNeckLayout.FromChordRow(neck.Layout, CFrets, CFingers, cDots, cMarks);
                neck.SetDots(cDots, cCount);
                neck.SetStringMarks(cMarks);
                AimCloseup(eye, neck);
                written.Add(Save(eye, black, Path.Combine(folder, "neck_closeup_c_chord.png")));

                string report = Path.Combine(folder, "neck_preview.txt");
                File.WriteAllText(report, Describe(neck, horizontalFov, verticalFov));
                written.Add(report);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                EditorSceneManager.ClosePreviewScene(scene);   // destroys everything that was moved into it
            }
            return written;
        }

        // Looks from the eye at the middle of everything from the X / O row beyond the nut to the last fret, zoomed in.
        static void AimCloseup(Camera eye, FloatingNeck neck)
        {
            NeckLayout layout = neck.Layout;
            NeckSizes sizes = neck.Sizes;
            float fromX = sizes.markRowXMm - sizes.markRadiusMm;
            Vector3 middle = neck.transform.TransformPoint(NeckMath.LocalMetres(0.5f * (fromX + layout.LastFretXMm), layout.stringHeightMm, 0f));
            eye.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(middle.normalized, Vector3.up));
            eye.fieldOfView = CloseupVerticalFovDeg;
        }

        static string Save(Camera eye, Color background, string path)
        {
            RenderTexture target = RenderTexture.GetTemporary(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
            RenderTexture activeBefore = RenderTexture.active;
            var image = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            try
            {
                eye.backgroundColor = background;
                eye.targetTexture = target;
                eye.Render();
                RenderTexture.active = target;   // ReadPixels reads the active render texture
                image.ReadPixels(new Rect(0, 0, EyeWidth, EyeHeight), 0, 0, false);

                // The picture is what reaches the eye: colour only. Alpha-blended text leaves partial alpha behind; make the
                // file opaque so an image viewer does not show its own background through it.
                Color32[] pixels = image.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                image.SetPixels32(pixels);
                image.Apply(false);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                eye.targetTexture = null;
                RenderTexture.active = activeBefore;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
            return path;
        }

        // The numbers behind the picture: every size in millimetres on the neck, degrees of view and glasses pixels.
        static string Describe(FloatingNeck neck, float horizontalFov, float verticalFov)
        {
            FloatingPlacement placement = neck.Floating;
            NeckSizes sizes = neck.Sizes;
            float pixelsPerDegree = NeckMath.PixelsPerDegreeAtCentre(verticalFov, EyeHeight);
            var sb = new StringBuilder();
            sb.AppendLine("Floating neck preview (dev layout, G chord). Not a measurement of the demo guitar.");
            sb.AppendLine("glasses eye: " + EyeWidth + " x " + EyeHeight + " px, " + F(horizontalFov) + " x " + F(verticalFov) + " deg (" +
                          F(GlassesDiagonalFovDeg) + " diagonal), " + F(pixelsPerDegree) + " px/deg at the centre");
            sb.AppendLine("placement: nut " + F(placement.distanceM) + " m away, " + F(placement.azimuthDeg) + " deg azimuth, " +
                          F(placement.elevationDeg) + " deg elevation, tilt " + F(placement.tiltDeg) + " deg, scale " + F(placement.scale));
            sb.AppendLine("size on the neck (mm, before scale) | on the display (deg) | glasses px");
            Row(sb, "string 1 (thinnest)", sizes.stringThinMm, placement, pixelsPerDegree);
            Row(sb, "string 6 (thickest)", sizes.stringThickMm, placement, pixelsPerDegree);
            Row(sb, "fret", sizes.fretMm, placement, pixelsPerDegree);
            Row(sb, "nut bar", sizes.nutMm, placement, pixelsPerDegree);
            Row(sb, "fretboard edge", sizes.edgeMm, placement, pixelsPerDegree);
            Row(sb, "fingertip dot diameter", sizes.dotRadiusMm * 2f, placement, pixelsPerDegree);
            Row(sb, "dot glow diameter", sizes.haloRadiusMm * 2f, placement, pixelsPerDegree);
            Row(sb, "X / O mark height", sizes.markRadiusMm * 2f, placement, pixelsPerDegree);
            Row(sb, "X / O mark stroke", sizes.markStrokeMm, placement, pixelsPerDegree);
            Row(sb, "X / O row: mark spacing", sizes.markPitchMm, placement, pixelsPerDegree);
            Row(sb, "X / O row: beyond the nut", -sizes.markRowXMm, placement, pixelsPerDegree);
            Row(sb, "finger number height (asked)", sizes.labelCapMm, placement, pixelsPerDegree);
            Row(sb, "string spacing at the nut", NeckMath.StringPitchAtNutMm(neck.Layout), placement, pixelsPerDegree);
            Row(sb, "nut to last fret", neck.Layout.LastFretXMm, placement, pixelsPerDegree);
            sb.AppendLine("Degrees are at the nut's distance. Measure the drawn digits in neck_glasses_eye_1920x1080.png: NeckLabel's size");
            sb.AppendLine("constant (DigitUnitsPerFontSize) comes from a measurement of an earlier render, so the picture is the check.");
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string what, float localMm, FloatingPlacement placement, float pixelsPerDegree)
        {
            float degrees = NeckMath.AngleForSize(localMm * placement.scale * NeckMath.MetresPerMm, placement.distanceM);
            sb.AppendLine("  " + what.PadRight(28) + F(localMm).PadLeft(8) + " mm | " + F(degrees).PadLeft(6) + " deg | " +
                          F(degrees * pixelsPerDegree).PadLeft(6) + " px");
        }

        static string F(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
