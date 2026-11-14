// NeckAlignPreview: runs the REAL 4-glance alignment (NeckAligner -> NeckAlignFlow -> Core's NeckSolver -> FloatingNeck.SetWorldPose)
// against a synthetic real guitar and renders what the glasses' left eye would see, so the overlay error can be judged from
// pictures without Play Mode, the glasses or a guitar. Run it with the editor closed (do NOT pass -nographics: it renders):
//
//   Unity -batchmode -quit -projectPath unity -executeMethod UntitledGuitarApp.Editor.NeckAlignPreview.RenderPreviews
//         -alignPreviewOut build/previews/align -logFile -
//
// or, in an open editor, menu Untitled Guitar App > Neck > Render Align Preview. Output folder: the argument after
// -alignPreviewOut (default build/previews/align; a relative path starts at the repository root). The scene is a temporary
// preview scene (EditorSceneManager.NewPreviewScene): nothing is saved, no dialog opens, the scene open in the editor is untouched.
//
// THE SET-UP. A seated player's head pivot at (0, 1.15, 0) m. The real guitar (synthetic truth, the "lap" scene of
// NeckAlignerTests): nut 0.42 m left, 0.32 m down and 0.30 m ahead of the eyes (0.60 m away), neck running right, a little
// down and toward the body, fretboard tipped back 45 degrees. It is drawn in AMBER: the low E and high E strings from the nut
// to fret 12, the nut, frets 5 and 12, and MAGENTA balls on the 4 dwell targets (on the strings). The virtual neck the
// aligner placed is the app's own FloatingNeck (G chord dots). Head motion per glance: a 0.3 s turn to the target (plus the
// case's aim error), then holding with 0.1 degree tremor at 60 frames per second until the steady-dwell capture fires.
//   align_prompt_step1.png          first prompt, looking at the nut on low E (the neck is hidden while aligning)
//   align_clean_left_eye.png        no aim error: the placed neck on the truth, with the result HUD
//   align_noise05_left_eye.png      0.5 degree aim error on each glance (seeded): the placed neck vs the truth
//   align_noise05_nut_closeup.png   the same, zoomed on the nut (10 degree tall view)
//   align_drift_before_reanchor.png the guitar moved (3 degree yaw about the head + 10 mm): old alignment vs moved truth
//   align_reanchor_left_eye.png     after a one-glance re-anchor at the nut
//   align_preview.json              per case: residuals, nut error vs the truth, per-target errors, axis errors, jitter
// Eye camera: left eye = pivot + head rotation x (-IPD/2, 0, 0), IPD 63 mm, symmetric 52-degree-diagonal frustum (StereoRig's
// asymmetric convergence shift is left out: both the truth and the neck go through the same camera).
// On failure the process exits with code 1 in batch mode; inside an open editor it throws (never closes the editor).
//
// Sources: the same Unity 2021.3.3f1 offline docs as Editor/NeckPreview.cs (NewPreviewScene, Camera.scene, Camera.Render,
// RenderTexture.GetTemporary, Texture2D.ReadPixels, EncodeToPNG, ShaderUtil.allowAsyncCompilation, EditorApplication.Exit),
// plus GameObject.CreatePrimitive and Shader.Find("Hidden/Internal-Colored") (docs/apis/unity/neck-align.md).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UntitledGuitarApp.Core.Geometry;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Neck;
// Core.Theory has its own FingertipDot; the neck's is meant here.
using FingertipDot = UntitledGuitarApp.Runtime.Neck.FingertipDot;

namespace UntitledGuitarApp.Editor
{
    public static class NeckAlignPreview
    {
        public const string OutArgument = "-alignPreviewOut";
        public const string DefaultOutFolder = "build/previews/align";
        public const string MenuPath = "Untitled Guitar App/Neck/Render Align Preview";

        const float GlassesDiagonalFovDeg = 52f;
        const int EyeWidth = 1920;
        const int EyeHeight = 1080;
        const float IpdMm = 63f;
        const float Fps = 60f;
        const float TremorDeg = 0.1f;
        const float CloseupVerticalFovDeg = 10f;
        static readonly Vector3 HeadPivot = new Vector3(0f, 1.15f, 0f);
        static readonly Color TruthColor = new Color(1f, 0.55f, 0.08f, 1f);
        static readonly Color MarkerColor = new Color(1f, 0.2f, 0.9f, 1f);
        static readonly Color ReticleColor = new Color(0.8f, 0.95f, 1f, 1f);

        sealed class Stage
        {
            public Scene scene;
            public GameObject root;
            public Transform head;
            public FloatingNeck neck;
            public NeckAligner aligner;
            public Camera eye;
            public Material truthMaterial;
            public Material markerMaterial;
            public Material reticleMaterial;
            public GameObject truthGroup;
            public float time;
            public int frames;
        }

        [MenuItem(MenuPath)]
        public static void RenderPreviews()
        {
            string failure = null;
            List<string> written = null;
            string folder = null;
            try
            {
                folder = ResolveOutFolder(Environment.GetCommandLineArgs());
                Debug.Log("[NeckAlignPreview] writing to " + folder);
                written = Render(folder);
            }
            catch (Exception e)
            {
                failure = e.ToString();
            }

            if (failure == null)
            {
                Debug.Log("[NeckAlignPreview] wrote " + written.Count + " file(s) to " + folder + ":\n  " + string.Join("\n  ", written));
                return;
            }
            Debug.LogError("[NeckAlignPreview] FAILED: " + failure);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            else
                throw new InvalidOperationException("Neck align preview failed: " + failure);
        }

        /// <summary>The folder after -alignPreviewOut, or the default; relative paths start at the repository root.</summary>
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

        // ------------------------------------------------------------------ the synthetic real guitar

        static AlignedPose LapTruth()
        {
            Vector3 along = new Vector3(1f, -0.25f, -0.30f).normalized;
            float tilt = 45f * Mathf.Deg2Rad;
            Vector3 hint = new Vector3(0f, Mathf.Sin(tilt), Mathf.Cos(tilt));
            Vector3 up = (hint - along * Vector3.Dot(hint, along)).normalized;
            return new AlignedPose(HeadPivot + new Vector3(-0.42f, -0.32f, 0.30f), NeckPlacement.RotationFromAxes(along, up));
        }

        static Vector3 TargetWorld(AlignedPose truth, NeckSolver solver, AlignTarget target)
        {
            return AlignMath.NeckToWorld(truth, false, AlignMath.NeckLocalMetres(solver.ModelPoint(target), CoreNeckLayout.StringHeightMm));
        }

        static Vector3 StringWorld(AlignedPose truth, int stringNumber, int fret)
        {
            var geometry = CoreNeckLayout.DefaultGeometry;
            double along = geometry.FretDistanceMm(fret);
            var point = new NeckPoint(along, geometry.StringAcrossMm(stringNumber, along));
            return AlignMath.NeckToWorld(truth, false, AlignMath.NeckLocalMetres(point, CoreNeckLayout.StringHeightMm));
        }

        // ------------------------------------------------------------------ rendering run

        static List<string> Render(string folder)
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            var json = new StringBuilder();
            json.Append("{\n  \"generator\": \"UntitledGuitarApp.Editor.NeckAlignPreview\",\n");
            json.Append("  \"headPivotM\": ").Append(Vec(HeadPivot)).Append(",\n");
            json.Append("  \"note\": \"nutErrorMm is the placed neck's nut vs the synthetic truth; the residual is Core's point-to-ray fit and understates it (G1 caution)\",\n");
            json.Append("  \"cases\": [\n");

            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                AlignedPose truth = LapTruth();

                // Case 1: clean glances (+ the first prompt, + a drift and a one-glance re-anchor on the same stage).
                Stage clean = BuildStage(scene, "clean");
                SetTruth(clean, truth);
                if (!clean.aligner.Begin()) throw new InvalidOperationException("Begin refused: " + clean.aligner.LastResult.Failure);
                LookAt(clean, TargetWorld(truth, clean.aligner.Flow.Solver, AlignTarget.NutLowE), 0f, new System.Random(1), 10);
                written.Add(Save(clean, Path.Combine(folder, "align_prompt_step1.png"), TargetWorld(truth, clean.aligner.Flow.Solver, AlignTarget.NutLowE), false));
                AlignOutcome cleanOutcome = RunGlances(clean, truth, 0f, 1);
                if (cleanOutcome == null || !cleanOutcome.Success)
                    throw new InvalidOperationException("the clean alignment failed: " + (cleanOutcome != null ? cleanOutcome.Failure : "no outcome"));
                written.Add(Save(clean, Path.Combine(folder, "align_clean_left_eye.png"), NeckMiddle(truth, clean), false));
                AppendCase(json, "clean", 0f, cleanOutcome, truth, clean, "align_clean_left_eye.png", false);

                // Drift: the guitar turns 3 degrees about the head and shifts 10 mm left; then a one-glance re-anchor.
                AlignedPose moved = AlignMath.RotateAbout(truth, HeadPivot, AlignMath.AxisAngle(Vector3.up, 3f));
                moved.nut += new Vector3(-0.010f, 0f, 0f);
                SetTruth(clean, moved);
                written.Add(Save(clean, Path.Combine(folder, "align_drift_before_reanchor.png"), NeckMiddle(moved, clean), false));
                AppendDrift(json, cleanOutcome.Pose, moved, clean);
                if (!clean.aligner.BeginReanchor()) throw new InvalidOperationException("BeginReanchor refused: " + clean.aligner.LastResult.Failure);
                Glance(clean, TargetWorld(moved, clean.aligner.Flow.Solver, AlignTarget.NutLowE), 0f, new System.Random(5));
                AlignOutcome reanchor = clean.aligner.LastResult;
                written.Add(Save(clean, Path.Combine(folder, "align_reanchor_left_eye.png"), NeckMiddle(moved, clean), false));
                AppendCase(json, "reanchor_after_drift", 0f, reanchor, moved, clean, "align_reanchor_left_eye.png", true);
                UnityEngine.Object.DestroyImmediate(clean.root);

                // Case 2: 0.5 degree aim error on every glance.
                Stage noisy = BuildStage(scene, "noise05");
                SetTruth(noisy, truth);
                if (!noisy.aligner.Begin()) throw new InvalidOperationException("Begin refused: " + noisy.aligner.LastResult.Failure);
                AlignOutcome noisyOutcome = RunGlances(noisy, truth, 0.5f, 11);
                written.Add(Save(noisy, Path.Combine(folder, "align_noise05_left_eye.png"), NeckMiddle(truth, noisy), false));
                written.Add(Save(noisy, Path.Combine(folder, "align_noise05_nut_closeup.png"), TargetWorld(truth, noisy.aligner.Flow.Solver, AlignTarget.NutLowE), true));
                json.Append(",\n");
                AppendCase(json, "noise05", 0.5f, noisyOutcome, truth, noisy, "align_noise05_left_eye.png", false);
                UnityEngine.Object.DestroyImmediate(noisy.root);

                json.Append("\n  ]\n}\n");
                string report = Path.Combine(folder, "align_preview.json");
                File.WriteAllText(report, json.ToString());
                written.Add(report);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                EditorSceneManager.ClosePreviewScene(scene);   // destroys everything that was moved into it
            }
            return written;
        }

        static Stage BuildStage(Scene scene, string name)
        {
            var st = new Stage { scene = scene };
            st.root = new GameObject("AlignPreview_" + name + " (not saved)");
            SceneManager.MoveGameObjectToScene(st.root, scene);

            var headObject = new GameObject("PreviewHead");
            headObject.transform.SetParent(st.root.transform, false);
            st.head = headObject.transform;
            st.head.position = HeadPivot;

            // The app's neck with the G chord, right-handed whatever the flags say.
            st.neck = FloatingNeck.Create(st.root.transform);
            st.neck.UseOptions(new NeckOptions());
            if (!st.neck.Build(CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry))) throw new InvalidOperationException("the Core neck layout was rejected");
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int count = Mathf.Max(0, CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, "G", dots, marks));
            st.neck.SetDots(dots, count);
            st.neck.SetStringMarks(marks);
            st.neck.PlaceFloating(st.head, false);

            st.aligner = NeckAligner.Create(st.root.transform, st.head, st.neck, null, null);
            st.aligner.RespectAnchorModeFlag = false;     // the preview must not depend on the flags file
            st.aligner.SuspendGazeWhileAligning = false;  // no GazeDwell here
            st.aligner.ConfirmKey = KeyCode.None;

            Shader colored = Shader.Find("Hidden/Internal-Colored");
            if (colored == null) throw new InvalidOperationException("Hidden/Internal-Colored not found");
            st.truthMaterial = MakeMaterial(colored, TruthColor);
            st.markerMaterial = MakeMaterial(colored, MarkerColor);
            st.reticleMaterial = MakeMaterial(colored, ReticleColor);

            // A stand-in for the app's reticle (Runtime/Input/Reticle positions itself in LateUpdate, which Edit Mode never runs).
            Segment(st.head, new Vector3(-0.012f, 0f, 1f), new Vector3(-0.004f, 0f, 1f), 0.0012f, st.reticleMaterial);
            Segment(st.head, new Vector3(0.004f, 0f, 1f), new Vector3(0.012f, 0f, 1f), 0.0012f, st.reticleMaterial);
            Segment(st.head, new Vector3(0f, -0.012f, 1f), new Vector3(0f, -0.004f, 1f), 0.0012f, st.reticleMaterial);
            Segment(st.head, new Vector3(0f, 0.004f, 1f), new Vector3(0f, 0.012f, 1f), 0.0012f, st.reticleMaterial);

            var cameraObject = new GameObject("PreviewLeftEye");
            cameraObject.transform.SetParent(st.root.transform, false);
            st.eye = cameraObject.AddComponent<Camera>();
            st.eye.enabled = false;
            st.eye.cameraType = CameraType.Preview;
            st.eye.scene = scene;
            st.eye.clearFlags = CameraClearFlags.SolidColor;
            st.eye.backgroundColor = Color.black;
            st.eye.nearClipPlane = 0.05f;
            st.eye.farClipPlane = 10f;
            st.eye.allowHDR = false;
            st.eye.allowMSAA = false;
            return st;
        }

        static Material MakeMaterial(Shader shader, Color color)
        {
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor("_Color", color);
            material.SetInt("_Cull", 0);
            return material;
        }

        static void SetTruth(Stage st, AlignedPose truth)
        {
            if (st.truthGroup != null) UnityEngine.Object.DestroyImmediate(st.truthGroup);
            st.truthGroup = new GameObject("SyntheticRealGuitar");
            st.truthGroup.transform.SetParent(st.root.transform, false);
            Transform g = st.truthGroup.transform;
            const float wire = 0.0014f;
            Segment(g, StringWorld(truth, 6, 0), StringWorld(truth, 6, 12), wire, st.truthMaterial);
            Segment(g, StringWorld(truth, 1, 0), StringWorld(truth, 1, 12), wire, st.truthMaterial);
            Segment(g, StringWorld(truth, 6, 0), StringWorld(truth, 1, 0), wire * 2f, st.truthMaterial);
            Segment(g, StringWorld(truth, 6, 5), StringWorld(truth, 1, 5), wire, st.truthMaterial);
            Segment(g, StringWorld(truth, 6, 12), StringWorld(truth, 1, 12), wire, st.truthMaterial);
            var solver = st.aligner.Flow.Solver;
            for (int i = 0; i < NeckSolver.TargetCount; i++) Ball(g, TargetWorld(truth, solver, (AlignTarget)i), 0.0045f, st.markerMaterial);
        }

        static void Segment(Transform parent, Vector3 a, Vector3 b, float width, Material material)
        {
            GameObject c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.DestroyImmediate(c.GetComponent<Collider>());
            c.transform.SetParent(parent, false);
            Vector3 d = b - a;
            c.transform.localPosition = 0.5f * (a + b);
            c.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d);
            c.transform.localScale = new Vector3(width, 0.5f * d.magnitude, width);   // the primitive is 2 units tall
            c.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        static void Ball(Transform parent, Vector3 at, float radius, Material material)
        {
            GameObject s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.DestroyImmediate(s.GetComponent<Collider>());
            s.transform.SetParent(parent, false);
            s.transform.localPosition = at;
            s.transform.localScale = Vector3.one * (2f * radius);
            s.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        // ------------------------------------------------------------------ head motion

        static AlignOutcome RunGlances(Stage st, AlignedPose truth, float aimNoiseDeg, int seed)
        {
            var random = new System.Random(seed);
            NeckSolver solver = st.aligner.Flow.Solver;
            for (int i = 0; i < NeckSolver.TargetCount && st.aligner.IsRunning; i++)
                Glance(st, TargetWorld(truth, solver, (AlignTarget)i), aimNoiseDeg, random);
            if (st.aligner.IsRunning) throw new InvalidOperationException("the flow did not capture all 4 glances (step " + st.aligner.Flow.Step + ")");
            return st.aligner.LastResult;
        }

        // A 0.3 s turn to the target (plus the aim error), then holding with tremor until the capture fires (at most 4 s).
        static void Glance(Stage st, Vector3 target, float aimNoiseDeg, System.Random random)
        {
            Vector3 from = st.head.forward;
            Vector3 aim = Perturb((target - st.head.position).normalized, aimNoiseDeg, random);
            int step = st.aligner.Flow.Step;
            for (int i = 1; i <= 18; i++) Tick(st, Vector3.Slerp(from, aim, i / 18f));
            for (int i = 0; i < 240 && st.aligner.IsRunning && st.aligner.Flow.Step == step; i++) Tick(st, Perturb(aim, TremorDeg, random));
        }

        static void LookAt(Stage st, Vector3 target, float aimNoiseDeg, System.Random random, int frames)
        {
            Vector3 aim = Perturb((target - st.head.position).normalized, aimNoiseDeg, random);
            for (int i = 0; i < frames; i++) Tick(st, Perturb(aim, TremorDeg, random));
        }

        static void Tick(Stage st, Vector3 direction)
        {
            st.head.rotation = Quaternion.LookRotation(direction, Vector3.up);
            st.aligner.Step(st.time, false);
            st.time += 1f / Fps;
            st.frames++;
        }

        static Vector3 Perturb(Vector3 d, float degrees, System.Random random)
        {
            if (degrees <= 0f) return d;
            Vector3 a = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 b = Vector3.Cross(d, a).normalized;
            float phi = (float)(random.NextDouble() * 2.0 * Math.PI);
            Vector3 side = a * Mathf.Cos(phi) + b * Mathf.Sin(phi);
            return AlignMath.AxisAngle(Vector3.Cross(d, side), degrees) * d;
        }

        static Vector3 NeckMiddle(AlignedPose truth, Stage st)
        {
            NeckSolver solver = st.aligner.Flow.Solver;
            return 0.5f * (TargetWorld(truth, solver, AlignTarget.NutLowE) + TargetWorld(truth, solver, AlignTarget.Fret5LowE));
        }

        // ------------------------------------------------------------------ pictures

        static string Save(Stage st, string path, Vector3 lookAt, bool closeup)
        {
            // The head turns to look at the point (the HUD and the reticle stand-in turn with it); the left eye renders.
            st.head.rotation = Quaternion.LookRotation((lookAt - st.head.position).normalized, Vector3.up);
            float horizontalFov, verticalFov;
            NeckMath.FovFromDiagonal(GlassesDiagonalFovDeg, EyeWidth / (float)EyeHeight, out horizontalFov, out verticalFov);
            st.eye.transform.SetPositionAndRotation(st.head.position + st.head.rotation * new Vector3(-0.5f * IpdMm / 1000f, 0f, 0f), st.head.rotation);
            st.eye.fieldOfView = closeup ? CloseupVerticalFovDeg : verticalFov;

            RenderTexture target = RenderTexture.GetTemporary(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
            RenderTexture activeBefore = RenderTexture.active;
            var image = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            try
            {
                st.eye.targetTexture = target;
                st.eye.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, EyeWidth, EyeHeight), 0, 0, false);
                Color32[] pixels = image.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                image.SetPixels32(pixels);
                image.Apply(false);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                st.eye.targetTexture = null;
                RenderTexture.active = activeBefore;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
            return path;
        }

        // ------------------------------------------------------------------ report

        static void AppendCase(StringBuilder json, string name, float aimNoiseDeg, AlignOutcome o, AlignedPose truth, Stage st, string image, bool reanchor)
        {
            NeckSolver solver = st.aligner.Flow.Solver;
            json.Append("    {\n");
            json.Append("      \"case\": \"").Append(name).Append("\",\n");
            json.Append("      \"aimErrorDeg\": ").Append(F(aimNoiseDeg)).Append(", \"tremorDeg\": ").Append(F(TremorDeg)).Append(",\n");
            json.Append("      \"success\": ").Append(o != null && o.Success ? "true" : "false").Append(",\n");
            json.Append("      \"failure\": ").Append(o == null || o.Failure == null ? "null" : "\"" + o.Failure.Replace("\"", "'") + "\"").Append(",\n");
            if (o != null)
            {
                json.Append("      \"reanchor\": ").Append(o.Reanchor ? "true" : "false").Append(",\n");
                json.Append("      \"residualRmsMm\": ").Append(D(o.ResidualRmsMm)).Append(", \"residualMaxMm\": ").Append(D(o.ResidualMaxMm)).Append(",\n");
                json.Append("      \"targetResidualMm\": [");
                for (int i = 0; i < o.TargetResidualMm.Length; i++) json.Append(i > 0 ? ", " : "").Append(D(o.TargetResidualMm[i]));
                json.Append("],\n");
                json.Append("      \"maxJitterDeg\": ").Append(F(o.MaxJitterDeg)).Append(", \"spaceCaptures\": ").Append(o.ConfirmedCount).Append(",\n");
                json.Append("      \"nutDistanceM\": ").Append(F(o.NutDistanceM)).Append(", \"viewerElevationDeg\": ").Append(F(o.ViewerElevationDeg)).Append(",\n");
                if (reanchor) json.Append("      \"nutShiftMm\": ").Append(F(o.NutShiftMm)).Append(",\n");
                if (o.HasPose)
                {
                    json.Append("      \"nutErrorMm\": ").Append(F((o.Pose.nut - truth.nut).magnitude * 1000f)).Append(",\n");
                    json.Append("      \"targetErrorMm\": [");
                    for (int i = 0; i < NeckSolver.TargetCount; i++)
                    {
                        Vector3 drawn = AlignMath.NeckToWorld(o.Pose, false, AlignMath.NeckLocalMetres(solver.ModelPoint((AlignTarget)i), CoreNeckLayout.StringHeightMm));
                        json.Append(i > 0 ? ", " : "").Append(F((drawn - TargetWorld(truth, solver, (AlignTarget)i)).magnitude * 1000f));
                    }
                    json.Append("],\n");
                    json.Append("      \"alongAxisErrorDeg\": ").Append(F(AlignMath.AngleDeg(o.Pose.AlongAxis, truth.AlongAxis)));
                    json.Append(", \"upAxisErrorDeg\": ").Append(F(AlignMath.AngleDeg(o.Pose.UpAxis, truth.UpAxis))).Append(",\n");
                    json.Append("      \"placedNeckMode\": \"").Append(st.neck.Mode.ToString()).Append("\", \"placedNutM\": ").Append(Vec(st.neck.transform.position)).Append(",\n");
                }
            }
            json.Append("      \"framesSoFar\": ").Append(st.frames).Append(", \"secondsSoFar\": ").Append(F(st.time)).Append(",\n");
            json.Append("      \"image\": \"").Append(image).Append("\"\n");
            json.Append("    }");
        }

        static void AppendDrift(StringBuilder json, AlignedPose old, AlignedPose moved, Stage st)
        {
            json.Append(",\n    {\n      \"case\": \"drift_before_reanchor\",\n");
            json.Append("      \"nutErrorMm\": ").Append(F((old.nut - moved.nut).magnitude * 1000f)).Append(",\n");
            json.Append("      \"image\": \"align_drift_before_reanchor.png\"\n    },\n");
        }

        static string F(float v)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string D(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) || v > 1e9 ? "null" : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string Vec(Vector3 v)
        {
            return "[" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + "]";
        }
    }
}
