// GhostHandPreview: renders the capsule ghost hand to PNG files without anyone opening the editor window.
//
// BATCH MODE (run from the repository root; Unity must NOT already have the project open; do NOT add -nographics,
// the previews need a graphics device):
//   /Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath unity \
//       -executeMethod UntitledGuitarApp.Editor.GhostHandPreview.RenderPreviews -ghostPreviewOut build/previews \
//       -logFile logs/ghost-preview.log
//   Exit code 0 = images written with the real ghost shader, 3 = images written but the ghost shader is missing or has
//   compile errors (they are printed to the log, and the images show the plain emergency look), 1 = failed.
//   -ghostPreviewOut <folder> is optional (default build/previews); a relative folder is taken from the project folder.
//
// IN THE EDITOR: menu "Untitled Guitar App > Ghost Hand > Render Previews" does the same without quitting, and
// "... > Spawn Demo Hand In Scene" puts an unsaved demo hand (a G shape) at the origin to look at from all sides.
//
// WHAT IT RENDERS (each once over black = what the glasses show, once over mid-grey = so the translucency is visible):
//   rest            the fretting hand hovering, no finger used
//   G               literal G-shaped fingertip targets on a straight virtual neck in the pinned neck frame
//   G_finger2       the same with finger 2 active (the rest at 30%)
//   G_faded         the same faded to 40%
//   plus G seen from roughly where the player's eyes are.
// The thin grey lines (nut, frets, strings) and the orange marks (the targets) are only there to judge the pose.
//
// APIS (Unity 2021.3 Scripting Reference / Manual, local copy under Unity.app/Contents/Documentation/en, online under
// https://docs.unity3d.com/2021.3/Documentation/):
//   Manual/EditorCommandLineArguments   "-executeMethod <NamespaceName.ClassName.MethodName>": "To pass parameters, add them
//                                       to the command line and retrieve them inside the function using
//                                       System.Environment.GetCommandLineArgs"; "either throw an exception which causes Unity
//                                       to exit with return code 1, or call EditorApplication.Exit with a non-zero return code"
//   EditorApplication.Exit              "public static void Exit(int returnValue)"
//   Application-isBatchMode             "Returns true when Unity is launched with the -batchmode flag"
//   SceneManagement.EditorSceneManager.NewScene   "public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode = NewSceneMode.Single)"
//   SceneManagement.EditorSceneManager.CloseScene "public static bool CloseScene(Scene scene, bool removeScene)"
//   Camera.Render                       "Render the camera manually ... It will use the camera's clear flags, target texture
//                                       and all other settings" (and its example: RenderTexture.active, ReadPixels, Apply)
//   RenderTexture-active                "Currently active render texture"
//   Texture2D.ReadPixels                "public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps = true)":
//                                       "copies a rectangular pixel area from the currently active render target"
//   ImageConversion.EncodeToPNG         "public static byte[] EncodeToPNG(Texture2D tex)": "no alpha channel for RGB24 textures"
//   ShaderUtil.ShaderHasError, ShaderUtil.GetShaderMessages, ShaderMessage (file, line, message)
//   GL                                  the built-in "Hidden/Internal-Colored" shader (_SrcBlend, _DstBlend, _Cull, _ZWrite),
//                                       used here for the grey reference lines only
//   Quaternion.LookRotation             "public static Quaternion LookRotation(Vector3 forward, Vector3 upwards = Vector3.up)"
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UntitledGuitarApp.Runtime.GhostHand;

namespace UntitledGuitarApp.Editor
{
    public static class GhostHandPreview
    {
        const string OutArgument = "-ghostPreviewOut";
        const string DefaultOut = "build/previews";
        const int Width = 1600, Height = 1200;

        // ---- a straight virtual neck in the pinned frame, literal numbers (preview only; the app gets them from Core) ----
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

        /// <summary>Batch-mode entry point (-executeMethod). In batch mode it quits the editor with an exit code.</summary>
        public static void RenderPreviews()
        {
            int code;
            try
            {
                code = Run(OutputFolder());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[GhostHandPreview] FAILED: " + e.Message);
                if (!Application.isBatchMode) throw;
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        [MenuItem("Untitled Guitar App/Ghost Hand/Render Previews")]
        static void RenderPreviewsMenu()
        {
            RenderPreviews();
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

        static string OutputFolder()
        {
            string folder = DefaultOut;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == OutArgument) folder = args[i + 1];
            if (!Path.IsPathRooted(folder))
                folder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, folder);
            return Path.GetFullPath(folder);
        }

        // ------------------------------------------------------------------------------------------------ the work

        static int Run(string folder)
        {
            Directory.CreateDirectory(folder);
            bool shaderOk = ReportShader();

            // A temporary scene that is never saved. A fresh batch-mode editor only has its untitled scene, which cannot
            // take an additive neighbour, so batch mode replaces it; inside a running editor the scene is added and removed.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive);
            RenderTexture target = null;
            Texture2D readback = null;
            List<string> written = new List<string>();
            try
            {
                SceneManager.SetActiveScene(scene);
                GameObject neck = new GameObject("NeckFrame");              // identity: the neck frame is the world frame
                Vector3[] g = GChordTargets();
                CapsuleHand hand = CapsuleHand.Create(neck.transform, true);
                GameObject reference = AddReference(neck.transform, g);

                GameObject cameraObject = new GameObject("PreviewCamera");
                Camera cam = cameraObject.AddComponent<Camera>();
                cam.enabled = false;                                        // rendered by hand with Camera.Render
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

                // 1. rest
                hand.SetFingertipTargets(null, true);
                hand.Tick(1f);
                Aim(cam, threeQuarterEye, threeQuarterLook, up, 24f);
                reference.SetActive(false);
                Shoot(cam, readback, folder, "ghost_rest", written);

                // 2. G shape
                reference.SetActive(true);
                hand.SetFingertipTargets(g, true);
                hand.Tick(1f);
                Shoot(cam, readback, folder, "ghost_G", written);
                Aim(cam, playerEye, playerLook, up, 22f);
                Shoot(cam, readback, folder, "ghost_G_player", written);
                Aim(cam, threeQuarterEye, threeQuarterLook, up, 24f);

                // 3. finger 2 active
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
            }
            finally
            {
                RenderTexture.active = null;
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (!Application.isBatchMode) EditorSceneManager.CloseScene(scene, true);
            }

            Debug.Log("[GhostHandPreview] wrote " + written.Count + " images to " + folder +
                      (shaderOk ? "" : "   WARNING: ghost shader unusable, these show the emergency look"));
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
