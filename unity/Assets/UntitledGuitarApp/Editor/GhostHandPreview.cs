// GhostHandPreview: renders the ghost hands to PNG files (plus a JSON report) without Play Mode and without the glasses.
//
// BATCH MODE (run from the repository root; Unity must NOT already have the project open; do NOT add -nographics,
// the previews need a graphics device):
//   /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath unity \
//       -executeMethod UntitledGuitarApp.Editor.GhostHandPreview.RenderPreviews -ghostPreviewOut build/previews \
//       -logFile logs/ghost-preview.log
//   (...GhostHandPreview.RenderStrumPreviews renders only pass 2.)
//   Exit code 0 = images written with the real ghost shader, 3 = images written but the ghost shader is missing or has
//   compile errors (they are printed to the log, and the capsule images show the plain emergency look), 1 = failed (also
//   when a Blender-posed hand is unavailable for pass 2: the log says why).
//   -ghostPreviewOut <folder> is optional (default build/previews); a relative folder is taken from the REPOSITORY root
//   (the folder above unity/), as NeckPreview does.
//
// IN THE EDITOR: menu "Untitled Guitar App > Ghost Hand > Render Previews" (both passes) or "... > Render Strumming
// Previews" (pass 2) do the same without quitting, into <repo>/build/previews; "... > Spawn Demo Hand In Scene" puts an
// unsaved demo capsule hand (a G shape) at the origin. Every pass renders in its own temporary PREVIEW scene
// (EditorSceneManager.NewPreviewScene): it works with an untitled, unsaved scene open and never touches that scene.
//
// PASS 1, the capsule hand (each once over black = what the glasses show, once over mid-grey = so the translucency shows):
//   ghost_rest, ghost_G, ghost_G_player, ghost_G_nut, ghost_G_finger2, ghost_G_faded  (see the comments in RunCapsulePass)
//   The thin grey lines (nut, frets, strings) and the orange marks (the targets) are only there to judge the pose.
// PASS 2, both Blender-posed hands on the floating neck as the app builds it (CoreNeckLayout's default guitar, the
// strumming hand under the strum zone by GhostHandsDirector.CreateStrummingHand), driven by GhostHandsDirector through the
// riptide-loop pattern (D - D U - U D U, 100 bpm, Am 2 / G 2 / C 4 beats) from a 4-beat count-in at 60 frames a second,
// stopped at 8 beats of bar 1 (a quarter into each slot), seen by the glasses' LEFT eye (StereoRig's camera: head at the
// origin, the eye IPD/2 to the left, StereoMath.EyeProjection with the default optics flags):
//   strum_sheet_eye.png        the 8 frames, whole left-eye view (1920 x 1080 each, drawn at 960 x 540), left to right,
//                              top row frames 0-3, bottom row 4-7, over black
//   strum_sheet_closeup.png    the same 8 frames from the same eye, zoomed onto the neck (fixed framing), over black
//   strum_eye_<n>.png          frame n at full eye resolution
//   strum_preview.json         per frame: beat, slot, strumming phase and poses, both wrists in neck mm, the pick point,
//                              the fretting chord / morph / fade and every fingertip's distance to its dot, plus the
//                              relational checks (pick over the drawn strings, gap between the hands) and a PLANTED case
//                              that must fire (the same pick with the hand at the socket, as if not moved to the strum zone)
//
// APIS (Unity 2021.3 Scripting Reference / Manual, local copy under Unity.app/Contents/Documentation/en, online under
// https://docs.unity3d.com/2021.3/Documentation/; excerpts in docs/apis/unity/preview-render.md):
//   Manual/EditorCommandLineArguments   "-executeMethod <NamespaceName.ClassName.MethodName>": "To pass parameters, add them
//                                       to the command line and retrieve them inside the function using
//                                       System.Environment.GetCommandLineArgs"; "either throw an exception which causes Unity
//                                       to exit with return code 1, or call EditorApplication.Exit with a non-zero return code"
//   EditorApplication.Exit              "public static void Exit(int returnValue)"
//   Application-isBatchMode             "Returns true when Unity is launched with the -batchmode flag"
//   SceneManagement.EditorSceneManager.NewPreviewScene   "Creates a new preview Scene. Any object added to a preview Scene
//                                       will only be rendered in that Scene."  (+ ClosePreviewScene: "Closes a preview Scene")
//   Camera-scene                        "If not null, the camera will only render the contents of the specified Scene.
//                                       Currently, only Scenes created by EditorSceneManager.NewPreviewScene are supported."
//   SceneManagement.SceneManager.MoveGameObjectToScene   "You can only move root GameObjects from one Scene to another"
//   Camera-projectionMatrix             "Set a custom projection matrix ... This lasts until you call ResetProjectionMatrix"
//   Camera.Render                       "Render the camera manually ... It will use the camera's clear flags, target texture
//                                       and all other settings" (and its example: RenderTexture.active, ReadPixels, Apply)
//   Texture2D.ReadPixels                "public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps = true)":
//                                       "Reads the pixels from the current render target ... and writes them to the texture"
//                                       at destX / destY (the contact sheets are assembled this way)
//   ImageConversion.EncodeToPNG         "public static byte[] EncodeToPNG(Texture2D tex)": "no alpha channel for RGB24 textures"
//   ShaderUtil-allowAsyncCompilation    false = "force synchronous compilation", so no picture shows a placeholder shader
//   ShaderUtil.ShaderHasError, ShaderUtil.GetShaderMessages, ShaderMessage (file, line, message)
//   GL                                  the built-in "Hidden/Internal-Colored" shader (_SrcBlend, _DstBlend, _Cull, _ZWrite),
//                                       used here for the grey reference lines only
//   Quaternion.LookRotation             "public static Quaternion LookRotation(Vector3 forward, Vector3 upwards = Vector3.up)"
//   SkinnedMeshRenderer-updateWhenOffscreen  "If enabled, the Skinned Mesh will be updated when offscreen" (bounds follow the
//                                       posed bones, so a moved hand is never culled by stale bounds)
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
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Practice;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Editor
{
    public static class GhostHandPreview
    {
        const string OutArgument = "-ghostPreviewOut";
        const string DefaultOut = "build/previews";
        const int Width = 1600, Height = 1200;

        // ---- pass 2: the glasses' eye and the riptide-loop timeline (riptide-loop.song.json, read-only song data) ----------
        const int CellWidth = 960, CellHeight = 540, SheetColumns = 4, SheetRows = 2;
        static readonly string[] RiptideSlots = { "D", "-", "D", "U", "-", "U", "D", "U" };
        static readonly string[] RiptideChords = { "Am", "G", "C" };
        static readonly double[] RiptideChordBeats = { 2.0, 2.0, 4.0 };
        const float RiptideBpm = 100f;
        const int RiptideSlotsPerBeat = 2;       // 8 slots a bar / 4 beats a bar
        const double CountInBeats = 4.0;
        const float FrameSeconds = 1f / 60f;
        /// <summary>The beats pass 2 stops at: a quarter into each of bar 1's eight slots.</summary>
        static readonly double[] StrumBeats = { 0.125, 0.625, 1.125, 1.625, 2.125, 2.625, 3.125, 3.625 };
        // the pick may be this far outside the outer strings: the authored strokes start and end beyond them (pose_hands.py
        // STRUM_POSES: down-start 12 mm outside string 6, down-end 12 mm outside string 1, up-start 10 mm)
        const float PickMarginMm = 15f;
        const float HandsMinGapMm = 30f;          // the two hands' joints must stay this far apart

        // ---- pass 1: a straight virtual neck in the pinned frame, literal numbers (preview only; the app gets them from Core) ----
        const float ScaleLength = 0.648f;       // 25.5 inch scale
        const float NutEToE = 0.035f, SaddleEToE = 0.0535f;
        const float NutHalfWidth = 0.0215f;

        static float FretDistance(int fret) { return ScaleLength * (1f - Mathf.Pow(2f, -fret / 12f)); }

        // where a fingertip goes: 75% of the way from fret n-1 to fret n, on the string (string 1 = high E at +z)
        static Vector3 Dot(int stringNumber, int fret)
        {
            float x = Mathf.Lerp(FretDistance(fret - 1), FretDistance(fret), 0.75f);
            float eToE = Mathf.Lerp(NutEToE, SaddleEToE, x / ScaleLength);
            return new Vector3(x, 0f, (3.5f - stringNumber) * eToE / 5f);
        }

        /// <summary>Fingertip targets of an open G chord for fingers 1-4 (finger 4 unused): 320003, fingers 2 1 - - - 3.</summary>
        public static Vector3[] GChordTargets()
        {
            return new[] { Dot(5, 2), Dot(6, 3), Dot(1, 3), new Vector3(float.NaN, 0f, 0f) };
        }

        // ------------------------------------------------------------------------------------------------ entry points

        /// <summary>Batch-mode entry point (-executeMethod): both passes. In batch mode it quits the editor with an exit code.</summary>
        public static void RenderPreviews()
        {
            Run(true, true);
        }

        /// <summary>Batch-mode entry point: pass 2 only (both posed hands on the floating neck, strumming riptide).</summary>
        public static void RenderStrumPreviews()
        {
            Run(false, true);
        }

        [MenuItem("Untitled Guitar App/Ghost Hand/Render Previews")]
        static void RenderPreviewsMenu()
        {
            RenderPreviews();
        }

        [MenuItem("Untitled Guitar App/Ghost Hand/Render Strumming Previews")]
        static void RenderStrumPreviewsMenu()
        {
            RenderStrumPreviews();
        }

        [MenuItem("Untitled Guitar App/Ghost Hand/Spawn Demo Hand In Scene")]
        static void SpawnDemoHand()
        {
            GameObject neck = new GameObject("GhostHandDemo (not saved)");
            CapsuleHand hand = CapsuleHand.Create(neck.transform, true);
            hand.SetFingertipTargets(GChordTargets(), true);
            AddReference(neck.transform, GChordTargets());
            foreach (Transform t in neck.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave;
            Selection.activeGameObject = neck;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            Debug.Log("[GhostHandPreview] demo hand spawned at the origin (neck frame = world axes). It is not saved with the scene." +
                      (CapsuleHand.UsingFallbackLook ? " WARNING: the ghost shader is unusable, this is the emergency look." : ""));
        }

        static void Run(bool capsulePass, bool strumPass)
        {
            int code = 0;
            string folder = null;
            var written = new List<string>();
            try
            {
                folder = OutputFolder(Environment.GetCommandLineArgs());
                Directory.CreateDirectory(folder);
                if (capsulePass) code = RunCapsulePass(folder, written);
                if (strumPass) RunStrumPass(folder, written);
                Debug.Log("[GhostHandPreview] wrote " + written.Count + " file(s) to " + folder + ":\n  " + string.Join("\n  ", written.ToArray()) +
                          (code == 3 ? "\n   WARNING: ghost shader unusable, the capsule images show the emergency look" : ""));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[GhostHandPreview] FAILED: " + e.Message + (written.Count > 0 ? " (" + written.Count + " file(s) were written to " + folder + ")" : ""));
                if (!Application.isBatchMode) throw;
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>The folder after -ghostPreviewOut, or build/previews; a relative path starts at the repository root.</summary>
        public static string OutputFolder(string[] commandLine)
        {
            string folder = DefaultOut;
            if (commandLine != null)
                for (int i = 0; i < commandLine.Length - 1; i++)
                    if (commandLine[i] == OutArgument && !string.IsNullOrEmpty(commandLine[i + 1])) folder = commandLine[i + 1];
            if (!Path.IsPathRooted(folder))
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);   // <repo>/unity
                folder = Path.Combine(Path.GetDirectoryName(projectRoot), folder); // <repo>/<folder>
            }
            return Path.GetFullPath(folder);
        }

        // ------------------------------------------------------------------------------------------------ pass 1: capsule hand

        static int RunCapsulePass(string folder, List<string> written)
        {
            bool shaderOk = ReportShader();
            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject neck = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D readback = null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                neck = new GameObject("NeckFrame");                        // identity: the neck frame is the world frame
                Vector3[] g = GChordTargets();
                CapsuleHand hand = CapsuleHand.Create(neck.transform, true);
                GameObject reference = AddReference(neck.transform, g);
                SceneManager.MoveGameObjectToScene(neck, scene);           // the whole hierarchy, built in the active scene

                cameraObject = new GameObject("PreviewCamera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera cam = cameraObject.AddComponent<Camera>();
                cam.enabled = false;                                        // rendered by hand with Camera.Render
                cam.cameraType = CameraType.Preview;
                cam.scene = scene;                                          // only the preview scene's objects
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 10f;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                target = new RenderTexture(Width, Height, 24);
                target.name = "GhostHandPreview";
                cam.targetTexture = target;
                readback = new Texture2D(Width, Height, TextureFormat.RGB24, false);

                // +z (toward string 1) points at the floor for a right-handed player, so "up" in these pictures is -z
                Vector3 up = new Vector3(0f, 0f, -1f);
                Vector3 threeQuarterEye = new Vector3(0.30f, 0.32f, -0.22f), threeQuarterLook = new Vector3(0.07f, 0.01f, 0.03f);
                Vector3 playerEye = new Vector3(0.40f, 0.10f, -0.36f), playerLook = new Vector3(0.07f, 0.01f, 0.02f);
                Vector3 nutEye = new Vector3(-0.40f, 0f, 0.03f), nutLook = new Vector3(0.07f, -0.01f, 0.03f);      // up = +y here

                // 1. rest: the fretting hand hovering, no finger used
                hand.SetFingertipTargets(null, true);
                hand.Tick(1f);
                Aim(cam, threeQuarterEye, threeQuarterLook, up, 24f);
                reference.SetActive(false);
                Shoot(cam, readback, folder, "ghost_rest", written);

                // 2. G shape (literal G-shaped targets), from three quarters, the player's eyes and along the neck from the
                //    headstock (the fretboard is the bright line, the thumb should rest below it, behind the neck)
                reference.SetActive(true);
                hand.SetFingertipTargets(g, true);
                hand.Tick(1f);
                Shoot(cam, readback, folder, "ghost_G", written);
                Aim(cam, playerEye, playerLook, up, 22f);
                Shoot(cam, readback, folder, "ghost_G_player", written);
                Aim(cam, nutEye, nutLook, Vector3.up, 18f);
                Shoot(cam, readback, folder, "ghost_G_nut", written);
                Aim(cam, threeQuarterEye, threeQuarterLook, up, 24f);

                // 3. finger 2 active (the rest at 30%)
                hand.SetActiveFinger(2);
                hand.Tick(1f);
                Shoot(cam, readback, folder, "ghost_G_finger2", written);

                // 4. faded to 40% (all fingers again)
                hand.SetActiveFinger(CapsuleHand.AllFingers);
                hand.SetFade(0.4f);
                hand.Tick(1f);
                Shoot(cam, readback, folder, "ghost_G_faded", written);

                float worst = 0f;
                for (int finger = 1; finger <= 3; finger++) worst = Mathf.Max(worst, hand.GetReachError(finger));
                Debug.Log("[GhostHandPreview] G shape: worst fingertip miss " + (worst * 1000f).ToString("F2") + " mm (acceptance: 3 mm)");
                // GetFingertipPad is the point below the tip centre along the neck normal; add the radius back for the centre
                float thumbRadius = hand.Solver.dims.TipRadius(CapsuleHandDimensions.ThumbIndex) * hand.scale;
                Vector3 thumb = hand.GetFingertipPad(5) + hand.Solver.Normal * thumbRadius;
                Debug.Log("[GhostHandPreview] G shape: thumb tip centre " + (thumb.y * 1000f).ToString("F1") + " mm from the fretboard plane " +
                          "(negative = behind it; the back of the neck is at about -" + (hand.Solver.neckDepth * 1000f).ToString("F0") + " mm), " +
                          (thumb.z * 1000f).ToString("F1") + " mm across from the neck's centre line, miss " +
                          (hand.GetReachError(5) * 1000f).ToString("F1") + " mm");
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                RenderTexture.active = null;
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                EditorSceneManager.ClosePreviewScene(scene);                // destroys everything that was moved into it
                DestroyLeftovers(neck, cameraObject);                       // anything a failure left in the active scene
            }
            return shaderOk ? 0 : 3;
        }

        // Says whether the ghost shader loaded and compiled; prints the compiler's messages if it did not.
        static bool ReportShader()
        {
            Shader shader = Resources.Load<Shader>(CapsuleHand.ShaderResourcePath);
            if (shader == null)
            {
                Debug.LogError("[GhostHandPreview] Resources.Load<Shader>(\"" + CapsuleHand.ShaderResourcePath + "\") returned null: " +
                               "Assets/UntitledGuitarApp/Runtime/Resources/Shaders/GhostHand.shader is missing or was not imported");
                return false;
            }
            bool hasError = ShaderUtil.ShaderHasError(shader);
            ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
            for (int i = 0; i < messages.Length; i++)
            {
                string line = "[GhostHandPreview] shader " + messages[i].severity + ": " + messages[i].message +
                              " (" + messages[i].file + ":" + messages[i].line + ", " + messages[i].platform + ") " + messages[i].messageDetails;
                if (hasError) Debug.LogError(line); else Debug.LogWarning(line);
            }
            if (hasError || !shader.isSupported)
            {
                Debug.LogError("[GhostHandPreview] the ghost shader '" + shader.name + "' did not compile or is not supported on this GPU");
                return false;
            }
            Debug.Log("[GhostHandPreview] ghost shader '" + shader.name + "' compiled, " + messages.Length + " compiler message(s)");
            return true;
        }

        static void Aim(Camera cam, Vector3 eye, Vector3 lookAt, Vector3 up, float verticalFov)
        {
            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation(lookAt - eye, up);
            cam.fieldOfView = verticalFov;
        }

        // One state, twice: over black (what the glasses add to the world) and over mid-grey (shows how see-through it is).
        static void Shoot(Camera cam, Texture2D readback, string folder, string name, List<string> written)
        {
            Capture(cam, readback, Color.black, Path.Combine(folder, name + "_black.png"), written);
            Capture(cam, readback, new Color(0.5f, 0.5f, 0.5f, 1f), Path.Combine(folder, name + "_grey.png"), written);
        }

        static void Capture(Camera cam, Texture2D readback, Color background, string path, List<string> written)
        {
            cam.backgroundColor = background;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = cam.targetTexture;
            cam.Render();
            readback.ReadPixels(new Rect(0, 0, cam.targetTexture.width, cam.targetTexture.height), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(readback));
            written.Add(path);
        }

        static void DestroyLeftovers(params GameObject[] roots)
        {
            for (int i = 0; i < roots.Length; i++)
                if (roots[i] != null) UnityEngine.Object.DestroyImmediate(roots[i]);
        }

        // ------------------------------------------------------------------------------------------------ pass 2: strumming

        static void RunStrumPass(string folder, List<string> written)
        {
            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject head = null, eyeObject = null;
            FloatingNeck neck = null;
            RenderTexture eyeTarget = null, cellTarget = null;
            Texture2D eyeImage = null, sheetEye = null, sheetClose = null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;

                // The neck and both hands, built in the active scene and then moved (root and all) into the preview scene.
                NeckLayout layout = CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry);   // as Bootstrap builds it
                neck = FloatingNeck.Create(null);
                neck.UseOptions(new NeckOptions());          // right-handed, white dots, no debug axes, whatever the flags say
                if (!neck.Build(layout))
                    throw new InvalidOperationException("the Core neck layout was rejected: " + string.Join(" | ", layout.Validate().ToArray()));
                var library = new GhostPoseLibrary();
                library.Reload();
                PosedGhostHand fretting = PosedGhostHand.Create(neck.Socket, GhostPose.HandFretting, library);
                PosedGhostHand strumming = GhostHandsDirector.CreateStrummingHand(neck, library);
                if (!fretting.IsAvailable || !strumming.IsAvailable)
                    throw new InvalidOperationException("a Blender-posed hand is unavailable (" + library.Count + " poses loaded from " + library.Folder +
                        "): fretting: " + (fretting.IsAvailable ? "ok" : fretting.UnavailableReason) +
                        "; strumming: " + (strumming.IsAvailable ? "ok" : strumming.UnavailableReason));
                foreach (SkinnedMeshRenderer skin in neck.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skin.updateWhenOffscreen = true;
                SceneManager.MoveGameObjectToScene(neck.gameObject, scene);

                head = new GameObject("PreviewHead");                      // the seated player's head: origin, looking along +z
                SceneManager.MoveGameObjectToScene(head, scene);
                neck.PlaceFloating(head.transform);

                // The glasses' left eye, exactly as StereoRig sets it up with the default optics (flags.default.json's render.*).
                var optics = new RenderFlags();
                float aspect = optics.eyeWidth / (float)optics.eyeHeight;
                eyeObject = new GameObject("PreviewLeftEye");
                SceneManager.MoveGameObjectToScene(eyeObject, scene);
                Camera eye = eyeObject.AddComponent<Camera>();
                eye.enabled = false;
                eye.cameraType = CameraType.Preview;
                eye.scene = scene;
                eye.clearFlags = CameraClearFlags.SolidColor;
                eye.backgroundColor = Color.black;
                eye.nearClipPlane = optics.nearM;
                eye.farClipPlane = optics.farM;
                eye.allowHDR = false;
                eye.allowMSAA = false;
                Vector3 eyePosition = head.transform.TransformPoint(new Vector3(StereoMath.EyeOffsetM(optics.ipdMm, StereoEye.Left), 0f, 0f));
                Matrix4x4 eyeProjection = StereoMath.EyeProjection(optics.fovDiagonalDeg, aspect, optics.nearM, optics.farM, optics.ipdMm,
                                                                   StereoEye.Left, optics.convergenceM);
                Quaternion closeRotation;
                float closeFov;
                CloseupFraming(neck, eyePosition, head.transform.up, aspect, out closeRotation, out closeFov);

                eyeTarget = new RenderTexture(optics.eyeWidth, optics.eyeHeight, 24);
                cellTarget = new RenderTexture(CellWidth, CellHeight, 24);
                eyeImage = new Texture2D(optics.eyeWidth, optics.eyeHeight, TextureFormat.RGB24, false);
                sheetEye = new Texture2D(CellWidth * SheetColumns, CellHeight * SheetRows, TextureFormat.RGB24, false);
                sheetClose = new Texture2D(CellWidth * SheetColumns, CellHeight * SheetRows, TextureFormat.RGB24, false);

                GhostHandsDirector director = GhostHandsDirector.Create(fretting, strumming);
                float bps = RiptideBpm / 60f;
                var dots = new FingertipDot[FloatingNeck.MaxDots];
                var marks = new StringMark[NeckLayout.StringCount];
                var json = new StringBuilder();
                json.Append("{\n  \"what\": \"GhostHandPreview pass 2: both posed hands on the floating neck, riptide-loop D - D U - U D U at ")
                    .Append(F(RiptideBpm)).Append(" bpm, left eye\",\n");
                json.Append("  \"neck\": { \"layout\": \"CoreNeckLayout.DefaultGeometry\", \"lastFretXMm\": ").Append(F(layout.LastFretXMm))
                    .Append(", \"stringHeightMm\": ").Append(F(layout.stringHeightMm))
                    .Append(", \"strumZoneXMm\": ").Append(F(neck.StrumZone.localPosition.x / NeckMath.MetresPerMm))
                    .Append(", \"stringsSolidToXMm\": ").Append(F(StringsSolidToXMm(neck)))
                    .Append(", \"strummingHandOffsetMm\": ").Append(V(strumming.transform.localPosition / NeckMath.MetresPerMm))
                    .Append(", \"anchor\": \"strumming hand under FloatingNeck.StrumZone at (-").Append(F(GhostHandsDirector.AuthoredSoundholeXMm))
                    .Append(" mm, -stringHeight, 0): the authored soundhole on the strum zone\" },\n");
                json.Append("  \"frames\": [\n");

                // Count-in, then the song, at 60 frames a second; each eases exactly as Update would tick it.
                director.Idle(null, 0f);
                fretting.Tick(1f);
                strumming.Tick(1f);
                double beat = -CountInBeats;
                int plantedFired = 0, checksFailed = 0;
                for (int k = 0; k < StrumBeats.Length; k++)
                {
                    double target = StrumBeats[k];
                    while (beat + FrameSeconds * bps < target)
                    {
                        beat += FrameSeconds * bps;
                        Step(director, fretting, strumming, RiptideFrame(beat, bps), FrameSeconds);
                    }
                    float last = (float)((target - beat) / bps);
                    beat = target;
                    PracticeFrame frame = RiptideFrame(beat, bps);
                    Step(director, fretting, strumming, frame, last);

                    // the dots of the chord sounding now (what the play HUD shows), from Core's ChordLibrary
                    string dotChord = frame.Chord ?? frame.NextChord;
                    int dotCount = Mathf.Max(0, CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, dotChord, dots, marks));
                    neck.SetDots(dots, dotCount);
                    neck.SetStringMarks(marks);

                    // whole eye, then the close-up, each into its cell of the sheet
                    eye.transform.SetPositionAndRotation(eyePosition, head.transform.rotation);
                    eye.projectionMatrix = eyeProjection;
                    string eyePath = Path.Combine(folder, "strum_eye_" + k + ".png");
                    RenderTo(eye, eyeTarget, eyeImage, 0, 0);
                    File.WriteAllBytes(eyePath, ImageConversion.EncodeToPNG(eyeImage));
                    written.Add(eyePath);
                    int col = k % SheetColumns, row = k / SheetColumns;
                    int destX = col * CellWidth, destY = (SheetRows - 1 - row) * CellHeight;   // ReadPixels' y runs up
                    RenderTo(eye, cellTarget, sheetEye, destX, destY);
                    eye.ResetProjectionMatrix();
                    eye.transform.SetPositionAndRotation(eyePosition, closeRotation);
                    eye.fieldOfView = closeFov;
                    eye.aspect = aspect;
                    RenderTo(eye, cellTarget, sheetClose, destX, destY);

                    if (k > 0) json.Append(",\n");
                    bool plantedFiredHere, checksOk;
                    AppendFrame(json, k, beat, bps, frame, director, neck, layout, fretting, strumming, dots, dotCount, dotChord, eyePath,
                                out plantedFiredHere, out checksOk);
                    if (plantedFiredHere) plantedFired++;
                    if (!checksOk) checksFailed++;
                }
                json.Append("\n  ],\n  \"summary\": { \"frames\": ").Append(StrumBeats.Length).Append(", \"framesFailingAChecks\": ").Append(checksFailed)
                    .Append(", \"plantedCaseFiredInFrames\": ").Append(plantedFired).Append(" }\n}\n");

                sheetEye.Apply(false);
                sheetClose.Apply(false);
                string sheetEyePath = Path.Combine(folder, "strum_sheet_eye.png");
                string sheetClosePath = Path.Combine(folder, "strum_sheet_closeup.png");
                File.WriteAllBytes(sheetEyePath, ImageConversion.EncodeToPNG(sheetEye));
                File.WriteAllBytes(sheetClosePath, ImageConversion.EncodeToPNG(sheetClose));
                written.Add(sheetEyePath);
                written.Add(sheetClosePath);
                string jsonPath = Path.Combine(folder, "strum_preview.json");
                File.WriteAllText(jsonPath, json.ToString());
                written.Add(jsonPath);
                Debug.Log("[GhostHandPreview] strumming pass: " + StrumBeats.Length + " frames, " + checksFailed + " failing a relational check, planted case fired in " +
                          plantedFired + " of " + StrumBeats.Length + " (it must fire in all)");
                if (plantedFired != StrumBeats.Length)
                    Debug.LogError("[GhostHandPreview] the planted case (hand left at the socket) did not fire in every frame: the pick-over-strings check is not trustworthy");
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                RenderTexture.active = null;
                if (eyeTarget != null) UnityEngine.Object.DestroyImmediate(eyeTarget);
                if (cellTarget != null) UnityEngine.Object.DestroyImmediate(cellTarget);
                if (eyeImage != null) UnityEngine.Object.DestroyImmediate(eyeImage);
                if (sheetEye != null) UnityEngine.Object.DestroyImmediate(sheetEye);
                if (sheetClose != null) UnityEngine.Object.DestroyImmediate(sheetClose);
                EditorSceneManager.ClosePreviewScene(scene);
                DestroyLeftovers(neck != null ? neck.gameObject : null, head, eyeObject);
            }
        }

        static void Step(GhostHandsDirector director, PosedGhostHand fretting, PosedGhostHand strumming, PracticeFrame frame, float dt)
        {
            director.Apply(frame, dt);
            fretting.Tick(dt);       // what PosedGhostHand.Update does in Play Mode
            strumming.Tick(dt);
        }

        /// <summary>What the play-along producer gives for riptide-loop at 100%: a count-in on negative beats, then Am 2, G 2,
        /// C 4 beats, looping every 8 beats.</summary>
        static PracticeFrame RiptideFrame(double beat, float bps)
        {
            PracticeFrame f = PracticeFrame.Idle;
            f.Active = true;
            f.BeatsPerSecond = bps;
            f.BeatsPerBar = 4;
            f.Beat = beat;
            f.StrumSlots = RiptideSlots;
            f.SlotsPerBeat = RiptideSlotsPerBeat;
            if (beat < 0.0)
            {
                f.Chord = null;
                f.NextChord = RiptideChords[0];
                f.BeatsToChange = -beat;
                return f;
            }
            double loop = 0.0;
            for (int i = 0; i < RiptideChordBeats.Length; i++) loop += RiptideChordBeats[i];
            double t = beat % loop, start = 0.0;
            for (int i = 0; i < RiptideChords.Length; i++)
            {
                double end = start + RiptideChordBeats[i];
                if (t < end || i == RiptideChords.Length - 1)
                {
                    f.Chord = RiptideChords[i];
                    f.NextChord = RiptideChords[(i + 1) % RiptideChords.Length];
                    f.BeatsToChange = end - t;
                    return f;
                }
                start = end;
            }
            return f;
        }

        // A fixed close-up for all frames: from the same eye, centred between the nut and just past the strum zone, wide
        // enough for both (plus the hands' height above and below the neck).
        static void CloseupFraming(FloatingNeck neck, Vector3 eyePosition, Vector3 up, float aspect, out Quaternion rotation, out float verticalFov)
        {
            float strumX = neck.StrumZone.localPosition.x / NeckMath.MetresPerMm;
            Vector3 a = neck.transform.TransformPoint(NeckMath.LocalMetres(-40f, 0f, 0f));
            Vector3 b = neck.transform.TransformPoint(NeckMath.LocalMetres(strumX + 90f, 0f, 0f));
            Vector3 da = (a - eyePosition).normalized, db = (b - eyePosition).normalized;
            rotation = Quaternion.LookRotation((da + db).normalized, up);
            float spanDeg = Vector3.Angle(da, db) * 1.15f + 6f;                         // the hands reach past both ends
            float vertical = 2f * Mathf.Atan(Mathf.Tan(0.5f * spanDeg * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
            verticalFov = Mathf.Clamp(vertical, 9f, 27f);
        }

        static void RenderTo(Camera cam, RenderTexture target, Texture2D image, int destX, int destY)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, target.width, target.height), destX, destY, false);
                if (destX == 0 && destY == 0 && image.width == target.width) image.Apply(false);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
            }
        }

        // Where the floating neck's strings stop being solid (NeckMeshBuilder.BuildLines: strum zone + strumHalfMm, at least
        // the last fret, at most the saddle).
        static float StringsSolidToXMm(FloatingNeck neck)
        {
            NeckLayout layout = neck.Layout;
            float strumX = neck.StrumZone.localPosition.x / NeckMath.MetresPerMm;
            return Mathf.Max(layout.LastFretXMm, Mathf.Min(strumX + neck.Sizes.strumHalfMm, layout.scaleLengthMm));
        }

        static Vector3 NeckMm(FloatingNeck neck, Vector3 world)
        {
            return neck.transform.InverseTransformPoint(world) / NeckMath.MetresPerMm;
        }

        // The 16 bone heads and 5 fingertips of a posed hand, neck-frame mm.
        static int Joints(FloatingNeck neck, PosedGhostHand hand, Vector3[] result)
        {
            int n = 0;
            for (int b = 0; b < GhostPose.BoneCount; b++)
            {
                Transform bone = hand.Driver.GetBone(b);
                if (bone != null) result[n++] = NeckMm(neck, bone.position);
            }
            for (int d = 0; d < GhostPose.DigitCount; d++)
            {
                Vector3 tip;
                if (hand.TryGetFingertip(d, out tip)) result[n++] = NeckMm(neck, tip);
            }
            return n;
        }

        static void AppendFrame(StringBuilder json, int index, double beat, float bps, PracticeFrame frame, GhostHandsDirector director,
                                FloatingNeck neck, NeckLayout layout, PosedGhostHand fretting, PosedGhostHand strumming,
                                FingertipDot[] dots, int dotCount, string dotChord, string eyePath, out bool plantedFired, out bool checksOk)
        {
            StrumSample s = director.LastStrum;
            Vector3 strumWrist = NeckMm(neck, strumming.Driver.GetBone(GhostPose.Wrist).position);
            Vector3 fretWrist = NeckMm(neck, fretting.Driver.GetBone(GhostPose.Wrist).position);
            Vector3 thumb, index1;
            strumming.TryGetFingertip(0, out thumb);
            strumming.TryGetFingertip(1, out index1);
            Vector3 pick = NeckMm(neck, 0.5f * (thumb + index1));           // the pinch of thumb and index, where a pick is held
            float solidTo = StringsSolidToXMm(neck);
            string why;
            bool pickOk = GhostHandChecks.PickOverStrings(pick, 0f, solidTo, NeckMath.StringZMm(layout, 1, pick.x), NeckMath.StringZMm(layout, 6, pick.x),
                                                          PickMarginMm, out why);
            // PLANTED: the same pick if the hand had been left at the socket (the pose frame on the neck frame, no offset)
            Vector3 handOrigin = NeckMm(neck, strumming.transform.position);
            Vector3 plantedPick = pick - handOrigin;
            string plantedWhy;
            bool plantedOk = GhostHandChecks.PickOverStrings(plantedPick, 0f, solidTo, NeckMath.StringZMm(layout, 1, plantedPick.x),
                                                             NeckMath.StringZMm(layout, 6, plantedPick.x), PickMarginMm, out plantedWhy);
            plantedFired = !plantedOk;
            var a = new Vector3[GhostPose.JointCount];
            var b = new Vector3[GhostPose.JointCount];
            float gap = GhostHandChecks.MinDistanceMm(a, Joints(neck, fretting, a), b, Joints(neck, strumming, b));
            bool apart = gap >= HandsMinGapMm;
            checksOk = pickOk && apart;

            json.Append("    { \"index\": ").Append(index)
                .Append(", \"beat\": ").Append(F((float)beat)).Append(", \"seconds\": ").Append(F((float)(beat / bps)))
                .Append(", \"eyeImage\": \"").Append(Path.GetFileName(eyePath)).Append("\",\n");
            json.Append("      \"strum\": { \"slot\": ").Append(s.Slot).Append(", \"kind\": \"").Append(s.Kind == '\0' ? "" : s.Kind.ToString())
                .Append("\", \"slotPhase\": ").Append(F(s.SlotPhase)).Append(", \"phase\": \"").Append(s.Phase.ToString())
                .Append("\", \"from\": \"").Append(s.From).Append("\", \"to\": \"").Append(s.To).Append("\", \"blend\": ").Append(F(s.Blend))
                .Append(", \"striking\": ").Append(s.Striking ? "true" : "false")
                .Append(", \"wristNeckMm\": ").Append(V(strumWrist)).Append(", \"pickNeckMm\": ").Append(V(pick)).Append(" },\n");
            json.Append("      \"fretting\": { \"chord\": \"").Append(frame.Chord ?? "").Append("\", \"from\": \"").Append(director.FrettingFrom)
                .Append("\", \"to\": \"").Append(director.FrettingTo).Append("\", \"morph\": ").Append(F(director.FrettingMorph))
                .Append(", \"fade\": ").Append(F(director.FrettingFade)).Append(", \"wristNeckMm\": ").Append(V(fretWrist))
                .Append(", \"dotsOf\": \"").Append(dotChord ?? "").Append("\", \"tips\": [");
            for (int i = 0; i < dotCount; i++)
            {
                int finger = dots[i].finger;
                Vector3 tipWorld;
                bool has = finger >= 1 && finger <= 4 && fretting.TryGetFingertip(finger, out tipWorld);
                if (i > 0) json.Append(", ");
                json.Append("{ \"finger\": ").Append(finger).Append(", \"string\": ").Append(dots[i].stringNumber)
                    .Append(", \"dotMm\": ").Append(V(new Vector3(dots[i].xMm, layout.stringHeightMm, dots[i].zMm)));
                if (has)
                {
                    fretting.TryGetFingertip(finger, out tipWorld);
                    Vector3 tip = NeckMm(neck, tipWorld);
                    float errXZ = new Vector2(tip.x - dots[i].xMm, tip.z - dots[i].zMm).magnitude;
                    json.Append(", \"tipMm\": ").Append(V(tip)).Append(", \"errorAcrossFretboardMm\": ").Append(F(errXZ))
                        .Append(", \"tipAboveStringsMm\": ").Append(F(tip.y - layout.stringHeightMm));
                }
                json.Append(" }");
            }
            json.Append("] },\n");
            json.Append("      \"checks\": { \"pickOverStrings\": ").Append(pickOk ? "true" : "false")
                .Append(", \"pickWhy\": \"").Append(why ?? "").Append("\", \"handsMinGapMm\": ").Append(F(gap))
                .Append(", \"handsApart\": ").Append(apart ? "true" : "false")
                .Append(", \"planted\": { \"case\": \"hand left at the socket (no strum-zone offset)\", \"pickNeckMm\": ").Append(V(plantedPick))
                .Append(", \"pickOverStrings\": ").Append(plantedOk ? "true" : "false").Append(", \"fired\": ").Append(plantedFired ? "true" : "false")
                .Append(", \"why\": \"").Append(plantedWhy ?? "").Append("\" } } }");
            Debug.Log("[GhostHandPreview] frame " + index + " beat " + F((float)beat) + ": strum " + s.Phase + " " + s.From + "->" + s.To + " " + F(s.Blend) +
                      " (slot " + s.Slot + " " + s.Kind + "), wrist " + V(strumWrist) + " mm, pick " + (pickOk ? "over the strings" : why) +
                      "; fretting " + director.FrettingFrom + "->" + director.FrettingTo + " " + F(director.FrettingMorph) + "; hands " + F(gap) + " mm apart");
        }

        static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string V(Vector3 v)
        {
            return "[" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + "]";
        }

        // ------------------------------------------------------------------------------------------------ reference lines

        // Thin boxes for the nut, the frets and the strings of the virtual neck, and small marks at the targets, in one mesh
        // drawn additively with vertex colours. Purely a visual aid for the preview pictures.
        static GameObject AddReference(Transform parent, Vector3[] targets)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Color32> colors = new List<Color32>();
            List<int> triangles = new List<int>();
            Color32 grey = new Color32(70, 70, 70, 255), bright = new Color32(120, 120, 120, 255), orange = new Color32(255, 120, 30, 255);

            const int frets = 5;
            float end = FretDistance(frets) + 0.012f;
            for (int fret = 0; fret <= frets; fret++)
            {
                float x = FretDistance(fret);
                float half = NutHalfWidth + (0.028f - NutHalfWidth) * (x / (ScaleLength * 0.5f));
                AddBox(vertices, colors, triangles, new Vector3(x, 0f, -half), new Vector3(x, 0f, half), fret == 0 ? 0.0012f : 0.0005f, fret == 0 ? bright : grey);
            }
            for (int s = 1; s <= 6; s++)
            {
                float z0 = (3.5f - s) * NutEToE / 5f;
                float z1 = (3.5f - s) * Mathf.Lerp(NutEToE, SaddleEToE, end / ScaleLength) / 5f;
                AddBox(vertices, colors, triangles, new Vector3(0f, 0f, z0), new Vector3(end, 0f, z1), 0.0004f, grey);
            }
            if (targets != null)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (float.IsNaN(targets[i].x)) continue;
                    AddBox(vertices, colors, triangles, targets[i] + new Vector3(-0.003f, 0f, 0f), targets[i] + new Vector3(0.003f, 0f, 0f), 0.0008f, orange);
                    AddBox(vertices, colors, triangles, targets[i] + new Vector3(0f, 0f, -0.003f), targets[i] + new Vector3(0f, 0f, 0.003f), 0.0008f, orange);
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "GhostHandPreview reference";
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            Material material = new Material(Shader.Find("Hidden/Internal-Colored"));
            material.hideFlags = HideFlags.HideAndDontSave;
            material.SetInt("_SrcBlend", (int)BlendMode.One);
            material.SetInt("_DstBlend", (int)BlendMode.One);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.SetInt("_ZWrite", 0);

            GameObject go = new GameObject("NeckReference");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        // A box of square cross-section from a to b. Only its six faces' outsides matter (culling is off for this material).
        static void AddBox(List<Vector3> vertices, List<Color32> colors, List<int> triangles, Vector3 a, Vector3 b, float halfThickness, Color32 color)
        {
            Vector3 axis = (b - a).normalized;
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized * halfThickness;
            Vector3 lift = Vector3.Cross(axis, side).normalized * halfThickness;
            int o = vertices.Count;
            for (int end = 0; end < 2; end++)
            {
                Vector3 c = end == 0 ? a : b;
                vertices.Add(c - side - lift); vertices.Add(c + side - lift); vertices.Add(c + side + lift); vertices.Add(c - side + lift);
            }
            for (int i = 0; i < 8; i++) colors.Add(color);
            int[] quads =
            {
                0, 1, 5, 4,   1, 2, 6, 5,   2, 3, 7, 6,   3, 0, 4, 7,   0, 3, 2, 1,   4, 5, 6, 7,
            };
            for (int q = 0; q < quads.Length; q += 4)
            {
                triangles.Add(o + quads[q]); triangles.Add(o + quads[q + 1]); triangles.Add(o + quads[q + 2]);
                triangles.Add(o + quads[q]); triangles.Add(o + quads[q + 2]); triangles.Add(o + quads[q + 3]);
            }
        }
    }
}
