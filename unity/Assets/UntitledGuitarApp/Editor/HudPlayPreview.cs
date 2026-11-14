#if UNITY_EDITOR
// HudPlayPreview: renders the play-along HUD (Runtime/Hud/Play) the way one eye of the glasses shows it, to PNG files, with
// no glasses and no Play Mode, and checks the layout RELATIONALLY (every part against every other part and against the
// neck's dots, X / O marks and finger numbers).
//
//   In the open editor: menu Untitled Guitar App > HUD > Render Play HUD Previews (writes to <repo>/build/previews/play).
//   Batch mode (editor closed; do NOT pass -nographics, rendering needs the graphics device):
//     Unity -batchmode -quit -projectPath <repo>/unity -executeMethod UntitledGuitarApp.Editor.HudPlayPreview.RenderPreviews
//           -hudPlayPreviewOut <folder> -logFile -
//   A relative folder is taken from the repository root (the folder above unity/).
//
// Output: play-NN-<state>.png (one eye, 1920 x 1080, over black: exactly what the glasses add to the room),
// hud-play-contact-sheet.png (all states, numbered, 4 x 3), hud-play-report.json (every number) and hud-play-summary.txt
// (the problems in plain words). The log line "[HudPlayPreview] wrote N file(s) to <folder> ..." gives the problem count.
//
// THE STAGE. A temporary preview scene (EditorSceneManager.NewPreviewScene; the scene open in the editor is not touched and
// nothing is saved): the head at the origin looking along +z; the left eye camera set up exactly as StereoRig sets up its
// left eye (offset by half the IPD, StereoMath.EyeProjection from the render flags); the dev floating neck placed by
// FloatingNeck.PlaceFloating(head), as NeckPreview does; the NeckHudCard and the ApproachRings attached to the neck socket
// and the head, as in play mode; the ResultsCard head-locked 1 m ahead. The song is driven frame by frame (60 fps) from a
// song clock, the way a practice session would, so the beat dots, the cursor and the rings have their real history.
//
// TRUE TEXT SIZE. HudText draws text about 18% smaller than asked (fixed in another branch). The preview MEASURES that on
// this machine (a row of "H"s of known size, their lit rows counted) and scales every HUD text up by the measured ratio
// before the saved render, so the pictures and the overlap check show the layout at the size the fix will draw. Once the
// fix lands the measured ratio is 1 and nothing is scaled. The ratios are in the report.
//
// THE CHECK. Each visible part is rendered ALONE and its lit pixels become a PixelMask (Runtime/Hud/Play/PlayHudOverlap.cs,
// unit-tested): two parts overlap when a pixel is lit by both, so discs are judged by their round ink, rings by their
// stroke and text by its glyphs. PlayHudOverlap.Classify says which overlaps are by design. A HUD part that reaches the edge
// of the frame is reported as cut. The last state PLANTS two overlaps (the lyric strip moved onto the chord name, the
// pattern strip moved onto a fingertip dot); the run FAILS if the check does not report both.
//
// SOURCES (offline documentation of Unity 2021.3.3f1, Documentation/en; quoted in docs/apis/unity/hud-play-preview.md):
//  * SceneManagement.EditorSceneManager.NewPreviewScene.html / ClosePreviewScene.html; Camera-scene.html (renders only that
//    preview scene); SceneManagement.SceneManager.MoveGameObjectToScene.html ("You can only move root GameObjects").
//  * Camera.Render.html, Texture2D.ReadPixels.html, Texture2D.GetRawTextureData.html ("GetRawTextureData does not allocate
//    memory"), Unity.Collections.NativeArray_1.CopyTo.html, ImageConversion.EncodeToPNG.html.
//  * ShaderUtil-allowAsyncCompilation.html (false = synchronous: no placeholder shader in a picture).
//  * EditorApplication.Exit.html and Manual/EditorCommandLineArguments.html (-executeMethod, return code), MenuItem.html.
//  * Camera-worldToCameraMatrix.html and Camera-projectionMatrix.html (points to pixels, as HudLearnPreview does).
//
// NOT RUN YET: written without an editor (compiles offline with Unity 2021.3.3f1's reference assemblies). In Edit Mode
// Update/LateUpdate do not run: the script advances the card (NeckHudCard.Advance), the count-in (CountIn.Advance) and the
// rings (ApproachRings.Tick) itself, and lets a HudText whose glyph measurement is still pending retry after a render.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Hud.Play;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Editor
{
    /// <summary>Renders and checks the play-along HUD. Entry point (menu and -executeMethod): <see cref="RenderPreviews"/>.</summary>
    public static class HudPlayPreview
    {
        public const string OutArgument = "-hudPlayPreviewOut";
        public const string DefaultOutFolder = "build/previews/play";
        public const string MenuPath = "Untitled Guitar App/HUD/Render Play HUD Previews";
        public const string ReportFileName = "hud-play-report.json";
        public const string SummaryFileName = "hud-play-summary.txt";
        public const string SheetFileName = "hud-play-contact-sheet.png";

        const int EyeWidth = 1920;
        const int EyeHeight = 1080;
        const byte LitThreshold = 24;
        const float Frame = 1f / 60f;
        const float ResultsDistanceM = 1f;
        const float CrowdedGapDeg = 0.2f;

        // The chords-only demo song (StreamingAssets/songs/riptide-loop.song.json): 100 bpm, 4/4, eighth-note patterns.
        const float RiptideBpm = 100f;
        static readonly string[] StrumSlots = { "D", "-", "D", "U", "-", "U", "D", "U" };
        static readonly string[] PickSlots = { "B", "3", "2", "3", "1", "3", "2", "3" };
        // Happy Birthday (public domain; happy-birthday.song.json): 90 bpm, 3/4, its strum pattern.
        const float WaltzBpm = 90f;
        static readonly string[] WaltzSlots = { "D", "-", "D", "-", "D", "-" };

        // Appendix B2 chord rows, string 6 -> 1 (-1 = x, 0 = open; finger 0 = none).
        static readonly int[] GFrets = { 3, 2, 0, 0, 0, 3 }, GFingers = { 2, 1, 0, 0, 0, 3 };
        static readonly int[] CFrets = { -1, 3, 2, 0, 1, 0 }, CFingers = { 0, 3, 2, 0, 1, 0 };
        static readonly int[] AmFrets = { -1, 0, 2, 2, 1, 0 }, AmFingers = { 0, 0, 2, 3, 1, 0 };
        static readonly int[] DFrets = { -1, -1, 0, 2, 3, 2 }, DFingers = { 0, 0, 0, 1, 3, 2 };

        static readonly float[] CalibrationCaps = { 1.2f, 2f, 3f, 4f, 5f };

        static MethodInfo s_TextRetry;

        // ------------------------------------------------------------------ entry point

        /// <summary>Menu item and -executeMethod entry. Batch mode: exit code 1 on failure. Open editor: throws (no dialogs).</summary>
        [MenuItem(MenuPath, false, 220)]
        public static void RenderPreviews()
        {
            string folder = null;
            string failure = null;
            Outcome outcome = null;
            try
            {
                folder = ResolveOutFolder(Environment.GetCommandLineArgs());
                outcome = Render(folder);
            }
            catch (Exception e)
            {
                failure = e.ToString();
            }

            if (failure == null)
            {
                Debug.Log("[HudPlayPreview] wrote " + outcome.Files + " file(s) to " + folder + " | overlap problems: " + outcome.Problems
                    + " in " + outcome.States + " states (planted state excluded) | cut by the frame edge: " + outcome.Cut
                    + " | planted overlap detected: " + (outcome.PlantedDetected ? "yes" : "NO"));
                if (!outcome.PlantedDetected) failure = "the overlap check did not report the planted overlaps (see " + SummaryFileName + ")";
                else return;
            }

            Debug.LogError("[HudPlayPreview] FAILED: " + failure);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw new InvalidOperationException("HudPlayPreview failed: " + failure);
        }

        /// <summary>The folder after -hudPlayPreviewOut, or build/previews/play; relative paths start at the repository root.</summary>
        public static string ResolveOutFolder(string[] commandLine)
        {
            string folder = DefaultOutFolder;
            if (commandLine != null)
            {
                for (int i = 0; i + 1 < commandLine.Length; i++)
                {
                    if (!string.Equals(commandLine[i], OutArgument, StringComparison.OrdinalIgnoreCase)) continue;
                    string value = commandLine[i + 1];
                    if (!string.IsNullOrEmpty(value) && !value.StartsWith("-", StringComparison.Ordinal)) folder = value;
                    break;
                }
            }
            if (Path.IsPathRooted(folder)) return folder;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);   // <repo>/unity
            string repoRoot = Path.GetDirectoryName(projectRoot);               // <repo>
            return Path.Combine(repoRoot ?? string.Empty, folder);
        }

        // ------------------------------------------------------------------ the run

        sealed class Outcome
        {
            public int Files;
            public int Problems;
            public int Cut;
            public int States;
            public bool PlantedDetected;
        }

        sealed class Stage
        {
            public Scene Scene;
            public GameObject Root;
            public Transform Head;
            public Camera Eye;
            public FloatingNeck Neck;
            public RenderTexture Target;
            public Texture2D Readback;
            public Color32[] Pixels;
            public float FocalPx;
            public float VerticalFov;
        }

        // One state's HUD, rebuilt for every state.
        sealed class Play
        {
            public Stage Stage;
            public GameObject Hud;
            public NeckHudCard Card;
            public ApproachRings Rings;
            public ResultsCard Results;
            public GameObject ResultsHolder;
            public float Bpm = RiptideBpm;
            public int BeatsPerBar = 4;
            public FingertipDot[] Dots = new FingertipDot[FloatingNeck.MaxDots];
            public int DotCount;
            public StringMark[] Marks = new StringMark[NeckLayout.StringCount];
            public string ChordOnNeck = string.Empty;
            public bool Planted;
            public string Note = string.Empty;
            public float BeatSeconds { get { return 60f / Bpm; } }
        }

        sealed class StateSpec
        {
            public string Name;
            public string Description;
            public Action<Play> Setup;
        }

        static Outcome Render(string folder)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("HudPlayPreview needs the graphics device: run Unity without -nographics.");
            Directory.CreateDirectory(folder);
            bool sceneDirtyBefore = SceneManager.GetActiveScene().isDirty;

            var specs = new List<StateSpec>
            {
                new StateSpec { Name = "countin-3", Description = "count-in: 3 beats to go (riptide-loop, first chord Am)", Setup = p => CountInState(p, -2.6f) },
                new StateSpec { Name = "countin-2", Description = "count-in: 2", Setup = p => CountInState(p, -1.6f) },
                new StateSpec { Name = "countin-1", Description = "count-in: 1", Setup = p => CountInState(p, -0.6f) },
                new StateSpec { Name = "mid-chord", Description = "C, beat 2.5 of 4: beat dots counting down to Am", Setup = p => Riptide(p, 5.5f, false) },
                new StateSpec { Name = "one-beat-before-change", Description = "G, one beat before C: approach rings on C's fingertips (G dots on the neck)", Setup = p => Riptide(p, 3f, false) },
                new StateSpec { Name = "waiting", Description = "wait mode at the change to C: wait cue, rings landed (C dots on the neck)", Setup = WaitingState },
                new StateSpec { Name = "strum-pattern", Description = "strum D - D U - U D U, cursor on slot 6 (U)", Setup = p => Riptide(p, 6.5f, false) },
                new StateSpec { Name = "picking-pattern", Description = "picking B 3 2 3 1 3 2 3, bass string 5 (C), cursor on slot 5 (1)", Setup = p => Riptide(p, 6f, true) },
                new StateSpec { Name = "lyrics-chords-only", Description = "chords-only line Am 2 G 2 C 4 at beat 2.75 (G), next line underneath", Setup = p => Riptide(p, 2.75f, false) },
                new StateSpec { Name = "lyrics-words", Description = "Happy Birthday (public domain), 3/4: chords above the words, next line underneath", Setup = WordsState },
                new StateSpec { Name = "results", Description = "results card: 7 of 8 clean, 85 ms late", Setup = ResultsState },
                new StateSpec { Name = "planted-overlap", Description = "CHECKER SELF-TEST: mid-chord with the lyric strip moved onto the chord name and the pattern strip onto a G dot", Setup = PlantedState },
            };

            var json = new StringBuilder(64 * 1024);
            var summary = new StringBuilder(8 * 1024);
            var outcome = new Outcome();
            var thumbnails = new List<Color32[]>();
            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            Stage stage = null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                stage = BuildStage();

                FlagSet flags = FlagStore.Current;
                json.Append("{\n  \"tool\": \"HudPlayPreview\",\n");
                json.Append("  \"unity\": ").Append(Q(Application.unityVersion)).Append(", \"graphics\": ").Append(Q(SystemInfo.graphicsDeviceType.ToString()))
                    .Append(", \"colorSpace\": ").Append(Q(QualitySettings.activeColorSpace.ToString())).Append(", \"batchMode\": ").Append(B(Application.isBatchMode)).Append(",\n");
                json.Append("  \"eye\": {\"width\": ").Append(EyeWidth).Append(", \"height\": ").Append(EyeHeight)
                    .Append(", \"verticalFovDeg\": ").Append(F(stage.VerticalFov)).Append(", \"pixelsPerDegreeAtCentre\": ").Append(F(stage.FocalPx * Mathf.Deg2Rad))
                    .Append(", \"camera\": \"StereoRig left eye: StereoMath.EyeProjection + half-IPD offset, from the render flags\"},\n");
                json.Append("  \"flags\": {\"largeText\": ").Append(B(flags.largeText)).Append(", \"fingerColors\": ").Append(B(flags.ghost != null && flags.ghost.fingerColors))
                    .Append(", \"fovDiagonalDeg\": ").Append(F(flags.render.fovDiagonalDeg)).Append(", \"ipdMm\": ").Append(F(flags.render.ipdMm)).Append("},\n");
                json.Append("  \"neck\": {\"placement\": ").Append(Q(DescribePlacement(stage.Neck))).Append("},\n");

                List<float[]> calibration = Calibrate(stage);
                json.Append("  \"textScaleCalibration\": [");
                for (int i = 0; i < calibration.Count; i++)
                {
                    float[] c = calibration[i];
                    json.Append(i == 0 ? "\n" : ",\n").Append("    {\"capDeg\": ").Append(F(c[0])).Append(", \"askedPx\": ").Append(F(c[1]))
                        .Append(", \"renderedPx\": ").Append(F(c[2])).Append(", \"askedOverRendered\": ").Append(F(c[3])).Append(", \"measured\": ").Append(B(c[4] > 0.5f)).Append("}");
                }
                json.Append("\n  ],\n");
                json.Append("  \"note\": \"Texts are drawn at their ASKED size: each HUD text is scaled by askedOverRendered (interpolated by size) before the saved render and the check.\",\n");
                json.Append("  \"states\": [\n");

                summary.AppendLine("HudPlayPreview summary (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
                summary.AppendLine("text scale measured (asked / rendered): " + DescribeCalibration(calibration));
                summary.AppendLine();

                for (int s = 0; s < specs.Count; s++)
                {
                    StateSpec spec = specs[s];
                    string fileName = "play-" + (s + 1).ToString("00", CultureInfo.InvariantCulture) + "-" + spec.Name + ".png";
                    StateResult result = RenderState(stage, spec, calibration, Path.Combine(folder, fileName));
                    outcome.Files++;
                    thumbnails.Add(result.Thumbnail);
                    if (s > 0) json.Append(",\n");
                    json.Append(result.Json);

                    summary.AppendLine((s + 1).ToString("00", CultureInfo.InvariantCulture) + " " + spec.Name + " -- " + spec.Description);
                    summary.Append(result.Summary);
                    summary.AppendLine();

                    if (result.Planted)
                    {
                        outcome.PlantedDetected = result.PlantedDetected;
                    }
                    else
                    {
                        outcome.States++;
                        outcome.Problems += result.Problems;
                        outcome.Cut += result.Cut;
                    }
                }
                json.Append("\n  ],\n");

                WriteContactSheet(Path.Combine(folder, SheetFileName), thumbnails, stage);
                outcome.Files++;

                bool sceneDirtyAfter = SceneManager.GetActiveScene().isDirty;
                json.Append("  \"contactSheet\": {\"file\": ").Append(Q(SheetFileName)).Append(", \"layout\": \"4 columns x 3 rows, numbered in reading order = state number\"},\n");
                json.Append("  \"totals\": {\"problems\": ").Append(outcome.Problems).Append(", \"cutByFrameEdge\": ").Append(outcome.Cut)
                    .Append(", \"statesChecked\": ").Append(outcome.States).Append(", \"plantedOverlapDetected\": ").Append(B(outcome.PlantedDetected)).Append("},\n");
                json.Append("  \"activeSceneDirty\": {\"before\": ").Append(B(sceneDirtyBefore)).Append(", \"after\": ").Append(B(sceneDirtyAfter)).Append("},\n");
                json.Append("  \"hudPlayShader\": ").Append(Q(HudShape.ShaderSource)).Append("\n}\n");

                summary.AppendLine("TOTAL: " + outcome.Problems + " overlap problem(s) and " + outcome.Cut + " part(s) cut by the frame edge in "
                    + outcome.States + " states; planted overlap detected: " + (outcome.PlantedDetected ? "yes" : "NO (the checker is broken)"));
                summary.AppendLine("HUD shape shader: " + HudShape.ShaderSource + "; active scene dirty before/after: " + sceneDirtyBefore + "/" + sceneDirtyAfter);

                File.WriteAllText(Path.Combine(folder, ReportFileName), json.ToString());
                outcome.Files++;
                File.WriteAllText(Path.Combine(folder, SummaryFileName), summary.ToString());
                outcome.Files++;
            }
            finally
            {
                if (stage != null) DestroyStage(stage);
                ShaderUtil.allowAsyncCompilation = asyncBefore;
            }
            return outcome;
        }

        // ------------------------------------------------------------------ the stage (shared by every state)

        static Stage BuildStage()
        {
            var stage = new Stage();
            stage.Scene = EditorSceneManager.NewPreviewScene();
            stage.Root = new GameObject("HudPlayPreview (preview scene, not saved)");
            SceneManager.MoveGameObjectToScene(stage.Root, stage.Scene);

            var head = new GameObject("Head");
            head.transform.SetParent(stage.Root.transform, false);   // at the origin, looking along +z
            stage.Head = head.transform;

            RenderFlags render = FlagStore.Current.render ?? new RenderFlags();
            float diagonal = Sane(render.fovDiagonalDeg, 10f, 120f, 52f);
            float ipd = Sane(render.ipdMm, 0f, 90f, 63f);
            float near = Sane(render.nearM, 0.01f, 1f, 0.1f);
            float far = Sane(render.farM, near + 1f, 1000f, 20f);
            float convergence = Sane(render.convergenceM, 0f, 100f, 0f);
            float aspect = EyeWidth / (float)EyeHeight;

            var eyeObject = new GameObject("LeftEye");
            eyeObject.transform.SetParent(head.transform, false);
            eyeObject.transform.localPosition = new Vector3(StereoMath.EyeOffsetM(ipd, StereoEye.Left), 0f, 0f);
            Camera eye = eyeObject.AddComponent<Camera>();
            eye.enabled = false;                                   // rendered by hand with Camera.Render
            eye.cameraType = CameraType.Preview;
            eye.scene = stage.Scene;
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = new Color(0f, 0f, 0f, 1f);
            eye.allowHDR = false;
            eye.allowMSAA = false;
            eye.useOcclusionCulling = false;
            eye.nearClipPlane = near;
            eye.farClipPlane = far;
            eye.aspect = aspect;
            stage.VerticalFov = StereoMath.VerticalFovDeg(diagonal, aspect);
            eye.fieldOfView = stage.VerticalFov;
            eye.projectionMatrix = StereoMath.EyeProjection(diagonal, aspect, near, far, ipd, StereoEye.Left, convergence);
            stage.Eye = eye;
            stage.FocalPx = 0.5f * EyeHeight / Mathf.Tan(0.5f * stage.VerticalFov * Mathf.Deg2Rad);

            NeckLayout layout = DevNeckLayout.Create();
            FloatingNeck neck = FloatingNeck.Create(null);
            SceneManager.MoveGameObjectToScene(neck.gameObject, stage.Scene);
            neck.UseOptions(NeckOptions.FromFlags(FlagStore.Current));
            if (!neck.Build(layout)) throw new InvalidOperationException("the dev neck layout was rejected: " + string.Join(" | ", layout.Validate()));
            neck.PlaceFloating(stage.Head);
            stage.Neck = neck;

            stage.Target = RenderTexture.GetTemporary(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32);
            stage.Readback = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            stage.Pixels = new Color32[EyeWidth * EyeHeight];
            return stage;
        }

        static void DestroyStage(Stage stage)
        {
            if (stage.Target != null) RenderTexture.ReleaseTemporary(stage.Target);
            if (stage.Readback != null) UnityEngine.Object.DestroyImmediate(stage.Readback);
            EditorSceneManager.ClosePreviewScene(stage.Scene);   // destroys everything in it
        }

        static float Sane(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max) return fallback;
            return value;
        }

        // ------------------------------------------------------------------ text size calibration

        // A row of "H"s of known cap height, 0.5 m in front of the eye; their lit rows are counted. Returns
        // {capDeg, askedPx, renderedPx, asked/rendered, measured} per size.
        static List<float[]> Calibrate(Stage stage)
        {
            var result = new List<float[]>();
            var holder = new GameObject("TextCalibration");
            holder.transform.SetParent(stage.Eye.transform, false);
            var texts = new HudText[CalibrationCaps.Length];
            const float distance = 0.5f;
            bool neckWasActive = stage.Neck.gameObject.activeSelf;
            stage.Neck.gameObject.SetActive(false);
            try
            {
                for (int i = 0; i < texts.Length; i++)
                {
                    texts[i] = HudText.Create(holder.transform, "H" + i);
                    texts[i].transform.localPosition = new Vector3((i - 2) * 0.07f, 0f, distance);
                    texts[i].SetText("H");
                    texts[i].SetAngularHeight(CalibrationCaps[i], distance);
                }
                RenderAndSettleTexts(stage, holder.transform);
                ReadFrame(stage);

                for (int i = 0; i < texts.Length; i++)
                {
                    Vector2 centre;
                    ToEyePixels(stage.Eye, texts[i].transform.position, out centre);
                    float askedPx = 2f * stage.FocalPx * Mathf.Tan(0.5f * CalibrationCaps[i] * Mathf.Deg2Rad);
                    int halfWidth = Mathf.CeilToInt(askedPx * 0.9f);
                    int minY = int.MaxValue, maxY = -1;
                    for (int y = 0; y < EyeHeight; y++)
                    {
                        int row = y * EyeWidth;
                        for (int x = Mathf.Max(0, (int)centre.x - halfWidth); x <= Mathf.Min(EyeWidth - 1, (int)centre.x + halfWidth); x++)
                        {
                            Color32 p = stage.Pixels[row + x];
                            if (Mathf.Max(p.r, Mathf.Max(p.g, p.b)) < 128) continue;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                            break;
                        }
                    }
                    float renderedPx = maxY >= minY ? maxY - minY + 1 : 0f;
                    float ratio = renderedPx > 2f ? askedPx / renderedPx : 1f;
                    result.Add(new[] { CalibrationCaps[i], askedPx, renderedPx, ratio, texts[i].SizeMeasured ? 1f : 0f });
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
                stage.Neck.gameObject.SetActive(neckWasActive);
            }
            return result;
        }

        static float TrueSizeFactor(List<float[]> calibration, float capDeg)
        {
            if (calibration == null || calibration.Count == 0) return 1f;
            if (capDeg <= calibration[0][0]) return calibration[0][3];
            for (int i = 1; i < calibration.Count; i++)
            {
                if (capDeg > calibration[i][0]) continue;
                float t = (capDeg - calibration[i - 1][0]) / (calibration[i][0] - calibration[i - 1][0]);
                return Mathf.Lerp(calibration[i - 1][3], calibration[i][3], t);
            }
            return calibration[calibration.Count - 1][3];
        }

        static string DescribeCalibration(List<float[]> calibration)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < calibration.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(F(calibration[i][0])).Append(" deg -> ").Append(F(calibration[i][3])).Append(calibration[i][4] > 0.5f ? "" : " (NOT MEASURED)");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the song clock

        static Play NewPlay(Stage stage)
        {
            var play = new Play { Stage = stage };
            play.Hud = new GameObject("Hud");
            play.Hud.transform.SetParent(stage.Root.transform, false);
            play.Card = NeckHudCard.Create(play.Hud.transform);
            play.Rings = ApproachRings.Create(play.Hud.transform);
            play.ResultsHolder = new GameObject("ResultsHolder");
            play.ResultsHolder.transform.SetParent(stage.Head, false);
            play.ResultsHolder.transform.localPosition = new Vector3(0f, 0f, ResultsDistanceM);
            play.Results = ResultsCard.Create(play.ResultsHolder.transform, ResultsDistanceM);
            play.Card.Attach(stage.Neck.Socket, stage.Head);
            play.Rings.Attach(stage.Neck.Socket, stage.Head);
            return play;
        }

        static void DestroyPlay(Play play)
        {
            var meshes = new List<Mesh>();
            CollectOwnedMeshes(play.Hud, meshes);
            CollectOwnedMeshes(play.ResultsHolder, meshes);
            UnityEngine.Object.DestroyImmediate(play.Hud);
            UnityEngine.Object.DestroyImmediate(play.ResultsHolder);
            for (int i = 0; i < meshes.Count; i++) UnityEngine.Object.DestroyImmediate(meshes[i]);
        }

        // Per-object meshes (bars, approach rings) are not destroyed with their GameObjects; the shared unit meshes are kept.
        static void CollectOwnedMeshes(GameObject root, List<Mesh> into)
        {
            if (root == null) return;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || into.Contains(mesh)) continue;
                if (mesh == HudShape.Disc || mesh == HudShape.Halo || mesh == HudShape.Arrow || mesh == HudShape.Caret || mesh == HudShape.Ring) continue;
                into.Add(mesh);
            }
        }

        static void ShowChordOnNeck(Play play, string chord)
        {
            int[] frets = GFrets, fingers = GFingers;
            if (chord == "C") { frets = CFrets; fingers = CFingers; }
            else if (chord == "Am") { frets = AmFrets; fingers = AmFingers; }
            else if (chord == "D") { frets = DFrets; fingers = DFingers; }
            for (int i = 0; i < play.Marks.Length; i++) play.Marks[i] = StringMark.None;
            play.DotCount = DevNeckLayout.FromChordRow(play.Stage.Neck.Layout, frets, fingers, play.Dots, play.Marks);
            play.Stage.Neck.SetDots(play.Dots, play.DotCount);
            play.Stage.Neck.SetStringMarks(play.Marks);
            play.ChordOnNeck = chord;
        }

        static int TargetsFor(Play play, string chord, Vector3[] into)
        {
            int[] frets = GFrets, fingers = GFingers;
            if (chord == "C") { frets = CFrets; fingers = CFingers; }
            else if (chord == "Am") { frets = AmFrets; fingers = AmFingers; }
            else if (chord == "D") { frets = DFrets; fingers = DFingers; }
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            NeckLayout layout = play.Stage.Neck.Layout;
            int n = DevNeckLayout.FromChordRow(layout, frets, fingers, dots, marks);
            for (int i = 0; i < n && i < into.Length; i++) into[i] = new Vector3(dots[i].xMm, layout.stringHeightMm, dots[i].zMm);
            return Mathf.Min(n, into.Length);
        }

        // The riptide-loop line "Am 2, G 2, C 4" (chords-only) repeated; the next line is the same.
        static readonly LyricLine RiptideLine = new LyricLine(string.Empty, new LyricChord("Am", 2f), new LyricChord("G", 2f), new LyricChord("C", 4f));

        static string RiptideChordAt(float beat)
        {
            float inLine = PlayHudLayout.PositiveMod(beat, 8f);
            return inLine < 2f ? "Am" : inLine < 4f ? "G" : "C";
        }

        static float RiptideNextChange(float beat)
        {
            float lineStart = Mathf.Floor(beat / 8f) * 8f;
            float inLine = beat - lineStart;
            return lineStart + (inLine < 2f ? 2f : inLine < 4f ? 4f : 8f);
        }

        // Plays riptide-loop from a little before <beat> up to it, frame by frame, as a practice session would.
        static void Riptide(Play play, float beat, bool picking)
        {
            NeckHudCard card = play.Card;
            play.Bpm = RiptideBpm;
            play.BeatsPerBar = 4;
            card.Beats.BeatsPerSecond = play.Bpm / 60f;
            card.Pattern.SlotsPerBeat = 2;
            card.Pattern.SetPattern(picking ? PickSlots : StrumSlots, picking);
            card.Lyrics.BeatsPerBar = 4;
            card.Lyrics.SetLines(RiptideLine, RiptideLine);
            play.Rings.ApproachSeconds = ApproachRings.ApproachForBeat(play.BeatSeconds);

            float start = Mathf.Min(beat, Mathf.Max(Mathf.Floor(beat / 8f) * 8f, beat - 2.5f));
            var targets = new Vector3[ApproachRings.MaxRings];
            string neckChord = null;
            for (float b = start; ; b += Frame / play.BeatSeconds)
            {
                bool last = b >= beat - 1e-5f;
                if (last) b = beat;
                string chord = RiptideChordAt(b);
                float change = RiptideNextChange(b);
                string next = RiptideChordAt(change);
                card.SetChords(chord, next);
                card.Pattern.SetBassString(chord == "C" || chord == "Am" ? 5 : chord == "D" ? 4 : 6);
                if (chord != neckChord) { ShowChordOnNeck(play, chord); neckChord = chord; }
                int toChange = Mathf.CeilToInt(change - b - 1e-4f);
                card.Beats.Set(play.BeatsPerBar, PlayHudLayout.PositiveMod(b, play.BeatsPerBar), toChange);
                card.Pattern.SetCursor(b * card.Pattern.SlotsPerBeat);
                card.Lyrics.SetProgress(PlayHudLayout.PositiveMod(b, 8f));

                double now = b * play.BeatSeconds;
                double hit = change * play.BeatSeconds;
                if (hit - now <= play.Rings.ApproachSeconds)
                {
                    int n = TargetsFor(play, next, targets);
                    play.Rings.Schedule(targets, n, hit);
                }
                play.Rings.Tick(now);
                card.Advance(Frame);
                if (last) break;
            }
        }

        static void CountInState(Play play, float beat)
        {
            NeckHudCard card = play.Card;
            play.Bpm = RiptideBpm;
            card.Beats.BeatsPerSecond = play.Bpm / 60f;
            card.Pattern.SlotsPerBeat = 2;
            card.Pattern.SetPattern(StrumSlots, false);
            card.Lyrics.BeatsPerBar = 4;
            card.Lyrics.SetLines(RiptideLine, RiptideLine);
            ShowChordOnNeck(play, "Am");
            card.SetChords("Am", "G");
            for (float b = -4f; ; b += Frame / play.BeatSeconds)
            {
                bool last = b >= beat - 1e-5f;
                if (last) b = beat;
                card.CountIn.Show(Mathf.CeilToInt(-b - 1e-4f));
                card.Beats.Set(4, PlayHudLayout.PositiveMod(b, 4f), Mathf.CeilToInt(-b - 1e-4f));
                card.Pattern.SetCursor(b * 2f);
                card.Lyrics.SetProgress(b);
                card.CountIn.Advance(Frame);
                card.Advance(Frame);
                if (last) break;
            }
        }

        static void WaitingState(Play play)
        {
            Riptide(play, 4f, false);
            ShowChordOnNeck(play, "C");
            NeckHudCard card = play.Card;
            card.SetChords("C", "Am");
            card.SetWaiting(true);
            double held = 4f * play.BeatSeconds;
            for (int f = 0; f < 72; f++)                       // 1.2 s of holding
            {
                card.Beats.Set(4, 0f, 0);
                card.Pattern.SetCursor(8f);
                card.Lyrics.SetProgress(4f);
                play.Rings.Tick(held);
                card.Advance(Frame);
            }
        }

        static void WordsState(Play play)
        {
            NeckHudCard card = play.Card;
            play.Bpm = WaltzBpm;
            play.BeatsPerBar = 3;
            card.Beats.BeatsPerSecond = play.Bpm / 60f;
            card.Pattern.SlotsPerBeat = 2;
            card.Pattern.SetPattern(WaltzSlots, false);
            card.Lyrics.BeatsPerBar = 3;
            // happy-birthday.song.json, lines 1 and 2 (public domain).
            var line = new LyricLine("Happy birthday to you", new LyricChord("G", 4f, 0), new LyricChord("D", 2f, 18));
            var next = new LyricLine("Happy birthday to you", new LyricChord("D", 4f, 0), new LyricChord("G", 2f, 18));
            card.Lyrics.SetLines(line, next);
            ShowChordOnNeck(play, "G");
            card.SetChords("G", "D");
            for (float b = 0f; ; b += Frame / play.BeatSeconds)
            {
                bool last = b >= 1.5f - 1e-5f;
                if (last) b = 1.5f;
                card.Beats.Set(3, PlayHudLayout.PositiveMod(b, 3f), Mathf.CeilToInt(4f - b - 1e-4f));
                card.Pattern.SetCursor(b * 2f);
                card.Lyrics.SetProgress(b);
                card.Advance(Frame);
                if (last) break;
            }
        }

        static void ResultsState(Play play)
        {
            ShowChordOnNeck(play, "G");
            play.Card.gameObject.SetActive(false);
            play.Rings.Clear();
            play.Results.Title = "Riptide (loop)";
            play.Results.Show(7, 8, 85f, null);
        }

        static void PlantedState(Play play)
        {
            Riptide(play, 5.5f, false);
            NeckHudCard card = play.Card;
            // 1. HUD on HUD: the lyric strip's origin on the chord row, so its chord names sit on the big chord name.
            Transform chord = FindChild(card.transform, "ChordPivot");
            card.Lyrics.transform.position = chord.position;
            // 2. HUD on the neck: the first cell of the pattern strip on the first fingertip dot.
            Transform slot0 = FindChild(card.Pattern.transform, "Slot0");
            Vector3 dot = play.Stage.Neck.transform.TransformPoint(NeckMath.LocalMetres(play.Dots[0].xMm, play.Stage.Neck.Layout.stringHeightMm, play.Dots[0].zMm));
            card.Pattern.transform.position += dot - slot0.position;
            play.Planted = true;
        }

        static Transform FindChild(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            throw new InvalidOperationException("no child named " + name + " under " + root.name);
        }

        // ------------------------------------------------------------------ one state

        sealed class Part
        {
            public string Id;
            public string Label;
            public Renderer Renderer;
            public HudText Text;
            public PixelMask Mask;
        }

        sealed class StateResult
        {
            public string Json;
            public string Summary;
            public int Problems;
            public int Cut;
            public bool Planted;
            public bool PlantedDetected;
            public Color32[] Thumbnail;
        }

        static StateResult RenderState(Stage stage, StateSpec spec, List<float[]> calibration, string pngPath)
        {
            Play play = NewPlay(stage);
            var result = new StateResult();
            try
            {
                spec.Setup(play);
                RenderAndSettleTexts(stage, stage.Root.transform);

                // Draw every HUD text at its asked size (see the header).
                var json = new StringBuilder(16 * 1024);
                var sum = new StringBuilder(2048);
                Vector3 eyePos = stage.Eye.transform.position;
                int unmeasured = 0;
                foreach (HudText text in HudTexts(play))
                {
                    float distance = Vector3.Distance(eyePos, text.transform.position);
                    float cap = HudText.AngularHeightForSize(text.CapHeightMeters, distance);
                    float k = TrueSizeFactor(calibration, cap);
                    text.transform.localScale *= k;
                    if (!text.SizeMeasured) unmeasured++;
                }

                RenderCamera(stage);
                ReadFrame(stage);
                WritePng(stage, pngPath);
                result.Thumbnail = Thumbnail(stage.Pixels);
                PixelMask all = PixelMask.FromPixels(stage.Pixels, EyeWidth, EyeHeight, LitThreshold);

                List<Part> parts = CollectParts(play);
                MaskParts(stage, parts);

                // Overlaps, element against element.
                int problems = 0, notes = 0, byDesign = 0;
                var overlaps = new StringBuilder();
                bool plantedHudOnHud = false, plantedHudOnNeck = false;
                for (int i = 0; i < parts.Count; i++)
                {
                    for (int j = i + 1; j < parts.Count; j++)
                    {
                        Part a = parts[i], b = parts[j];
                        if (!PlayHudOverlap.ShouldCompare(a.Id, b.Id)) continue;
                        int shared = PixelMask.OverlapPixels(a.Mask, b.Mask);
                        if (shared < PlayHudOverlap.MinOverlapPixels) continue;
                        string why;
                        OverlapKind kind = PlayHudOverlap.Classify(a.Id, b.Id, out why);
                        if (kind == OverlapKind.Problem) problems++;
                        else if (kind == OverlapKind.Note) notes++;
                        else byDesign++;
                        int x0, y0, x1, y1;
                        PixelMask.OverlapBox(a.Mask, b.Mask, out x0, out y0, out x1, out y1);
                        string neckPart = NeckPartNear(play, a.Id, b.Id, 0.5f * (x0 + x1), 0.5f * (y0 + y1));
                        if (overlaps.Length > 0) overlaps.Append(",\n");
                        overlaps.Append("        {\"kind\": ").Append(Q(KindName(kind))).Append(", \"a\": ").Append(Q(a.Label)).Append(", \"b\": ").Append(Q(b.Label))
                            .Append(", \"pixels\": ").Append(shared).Append(", \"boxDeg\": ").Append(BoxDeg(stage, x0, y0, x1, y1))
                            .Append(neckPart != null ? ", \"neckPart\": " + Q(neckPart) : string.Empty).Append(", \"why\": ").Append(Q(why)).Append("}");
                        if (kind == OverlapKind.Problem)
                            sum.AppendLine("   !! OVERLAP " + a.Label + " x " + b.Label + (neckPart != null ? " (" + neckPart + ")" : "") + ": " + shared + " px, "
                                + BoxDeg(stage, x0, y0, x1, y1) + " deg");
                        if (play.Planted && kind == OverlapKind.Problem)
                        {
                            if (IsPlantedHudOnHud(a.Id, b.Id)) plantedHudOnHud = true;
                            if (IsPlantedHudOnNeck(a.Id, b.Id)) plantedHudOnNeck = true;
                        }
                    }
                }

                // Parts cut by the frame edge, crowded texts, sizes.
                int cut = 0;
                var cutList = new StringBuilder();
                var partsJson = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    Part p = parts[i];
                    bool hud = PlayHudOverlap.Group(p.Id) != PlayHudOverlap.NeckGroup;
                    if (hud && p.Mask.TouchesEdge)
                    {
                        cut++;
                        if (cutList.Length > 0) cutList.Append(", ");
                        cutList.Append(Q(p.Label));
                        sum.AppendLine("   !! CUT BY THE FRAME EDGE: " + p.Label + " " + BoxDeg(stage, p.Mask.MinX, p.Mask.MinY, p.Mask.MaxX, p.Mask.MaxY) + " deg");
                    }
                    if (partsJson.Length > 0) partsJson.Append(",\n");
                    partsJson.Append("        {\"id\": ").Append(Q(p.Label)).Append(", \"litPixels\": ").Append(p.Mask.Count)
                        .Append(", \"boxDeg\": ").Append(p.Mask.IsEmpty ? "null" : BoxDeg(stage, p.Mask.MinX, p.Mask.MinY, p.Mask.MaxX, p.Mask.MaxY));
                    if (p.Text != null)
                    {
                        float distance = Vector3.Distance(eyePos, p.Text.transform.position);
                        float cap = HudText.AngularHeightForSize(p.Text.CapHeightMeters, distance);
                        partsJson.Append(", \"text\": ").Append(Q(p.Text.Text)).Append(", \"capDeg\": ").Append(F(cap)).Append(", \"measured\": ").Append(B(p.Text.SizeMeasured));
                        bool chordName = p.Id.EndsWith("ChordPivot/CurrentChord", StringComparison.Ordinal);
                        float min = chordName ? HudText.ChordNameMinDegrees : HudText.MinTextDegrees;
                        if (cap < min - 0.02f)
                        {
                            problems++;
                            sum.AppendLine("   !! TOO SMALL: " + p.Label + " \"" + p.Text.Text + "\" " + F(cap) + " deg (minimum " + F(min) + ")");
                        }
                    }
                    partsJson.Append("}");
                }

                var crowded = new StringBuilder();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].Text == null) continue;
                    for (int j = i + 1; j < parts.Count; j++)
                    {
                        if (parts[j].Text == null && PlayHudOverlap.Group(parts[j].Id) != PlayHudOverlap.NeckGroup) continue;
                        int gap = PixelMask.BoxGap(parts[i].Mask, parts[j].Mask);
                        float gapDeg = gap / (stage.FocalPx * Mathf.Deg2Rad);
                        if (gap <= 0 || gapDeg >= CrowdedGapDeg) continue;
                        if (crowded.Length > 0) crowded.Append(",\n");
                        crowded.Append("        {\"a\": ").Append(Q(parts[i].Label)).Append(", \"b\": ").Append(Q(parts[j].Label)).Append(", \"boxGapDeg\": ").Append(F(gapDeg)).Append("}");
                    }
                }

                result.Problems = problems;
                result.Cut = cut;
                result.Planted = play.Planted;
                result.PlantedDetected = plantedHudOnHud && plantedHudOnNeck;
                if (play.Planted)
                {
                    sum.Insert(0, "   (checker self-test; not counted) planted text-on-text detected: " + (plantedHudOnHud ? "yes" : "NO")
                        + ", planted HUD-on-neck detected: " + (plantedHudOnNeck ? "yes" : "NO") + "\n");
                }
                else if (problems == 0 && cut == 0)
                {
                    sum.AppendLine("   no problems: " + parts.Count + " parts, " + notes + " note(s), " + byDesign + " by-design overlap(s)");
                }
                else
                {
                    sum.AppendLine("   " + problems + " problem(s), " + cut + " cut, " + notes + " note(s), " + byDesign + " by design; " + parts.Count + " parts");
                }
                if (unmeasured > 0) sum.AppendLine("   !! " + unmeasured + " text(s) whose glyph size HudText could not measure (fallback scale)");

                json.Append("    {\n      \"name\": ").Append(Q(spec.Name)).Append(", \"png\": ").Append(Q(Path.GetFileName(pngPath)))
                    .Append(", \"description\": ").Append(Q(spec.Description)).Append(", \"planted\": ").Append(B(play.Planted)).Append(",\n");
                json.Append("      \"chordOnNeck\": ").Append(Q(play.ChordOnNeck)).Append(", \"cardViewingDistanceM\": ").Append(F(play.Card.ViewingDistanceM))
                    .Append(", \"cardAnchorDeg\": ").Append(DirectionDeg(stage, play.Card.transform.position)).Append(",\n");
                json.Append("      \"litPixels\": ").Append(all.Count).Append(", \"litAreaDeg\": ")
                    .Append(all.IsEmpty ? "null" : BoxDeg(stage, all.MinX, all.MinY, all.MaxX, all.MaxY)).Append(",\n");
                json.Append("      \"problems\": ").Append(problems).Append(", \"cutByFrameEdge\": ").Append(cut).Append(", \"notes\": ").Append(notes)
                    .Append(", \"byDesign\": ").Append(byDesign).Append(", \"unmeasuredTexts\": ").Append(unmeasured).Append(",\n");
                if (play.Planted)
                    json.Append("      \"plantedDetected\": {\"textOnText\": ").Append(B(plantedHudOnHud)).Append(", \"hudOnNeck\": ").Append(B(plantedHudOnNeck)).Append("},\n");
                json.Append("      \"cut\": [").Append(cutList).Append("],\n");
                json.Append("      \"overlaps\": [\n").Append(overlaps).Append("\n      ],\n");
                json.Append("      \"crowded\": [\n").Append(crowded).Append("\n      ],\n");
                json.Append("      \"parts\": [\n").Append(partsJson).Append("\n      ]\n    }");
                result.Json = json.ToString();
                result.Summary = sum.ToString();
            }
            finally
            {
                DestroyPlay(play);
            }
            return result;
        }

        static bool IsPlantedHudOnHud(string a, string b)
        {
            string pa = PlayHudOverlap.PathOf(a), pb = PlayHudOverlap.PathOf(b);
            bool aLyric = PlayHudOverlap.Group(a) == PlayHudOverlap.CardGroup && pa.StartsWith("LyricStrip/", StringComparison.Ordinal);
            bool bLyric = PlayHudOverlap.Group(b) == PlayHudOverlap.CardGroup && pb.StartsWith("LyricStrip/", StringComparison.Ordinal);
            return (aLyric && pb == "ChordPivot/CurrentChord") || (bLyric && pa == "ChordPivot/CurrentChord");
        }

        static bool IsPlantedHudOnNeck(string a, string b)
        {
            bool aSlot = PlayHudOverlap.Group(a) == PlayHudOverlap.CardGroup && PlayHudOverlap.PathOf(a).StartsWith("PatternStrip/Slot0/", StringComparison.Ordinal);
            bool bSlot = PlayHudOverlap.Group(b) == PlayHudOverlap.CardGroup && PlayHudOverlap.PathOf(b).StartsWith("PatternStrip/Slot0/", StringComparison.Ordinal);
            return (aSlot && PlayHudOverlap.Group(b) == PlayHudOverlap.NeckGroup) || (bSlot && PlayHudOverlap.Group(a) == PlayHudOverlap.NeckGroup);
        }

        static IEnumerable<HudText> HudTexts(Play play)
        {
            var list = new List<HudText>();
            list.AddRange(play.Hud.GetComponentsInChildren<HudText>(false));
            list.AddRange(play.ResultsHolder.GetComponentsInChildren<HudText>(false));
            return list;
        }

        // Every visible renderer of the HUD and of the neck, with its id ("group:path").
        static List<Part> CollectParts(Play play)
        {
            var parts = new List<Part>();
            AddParts(parts, play.Card != null && play.Card.gameObject.activeInHierarchy ? play.Card.transform : null, PlayHudOverlap.CardGroup);
            AddParts(parts, play.Rings.transform, PlayHudOverlap.RingsGroup);
            AddParts(parts, play.Results.transform, PlayHudOverlap.ResultsGroup);
            AddParts(parts, play.Stage.Neck.transform, PlayHudOverlap.NeckGroup);
            return parts;
        }

        static void AddParts(List<Part> parts, Transform root, string group)
        {
            if (root == null) return;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                HudText text = renderer.GetComponent<HudText>();
                if (text != null && (!text.Visible || text.Text.Length == 0)) continue;
                string path = PathBelow(root, renderer.transform);
                if (group == PlayHudOverlap.NeckGroup && !(path == "Lines" || path == "Dots" || path.StartsWith("FingerNumber", StringComparison.Ordinal))) continue;
                string id = PlayHudOverlap.Id(group, path);
                parts.Add(new Part { Id = id, Label = id + (text != null ? " \"" + text.Text + "\"" : string.Empty), Renderer = renderer, Text = text });
            }
        }

        static string PathBelow(Transform root, Transform t)
        {
            var names = new List<string>();
            for (Transform c = t; c != null && c != root; c = c.parent) names.Add(c.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        // Renders each part alone and keeps its lit pixels.
        static void MaskParts(Stage stage, List<Part> parts)
        {
            Renderer[] all = stage.Root.GetComponentsInChildren<Renderer>(true);
            var wasEnabled = new bool[all.Length];
            for (int i = 0; i < all.Length; i++) wasEnabled[i] = all[i].enabled;
            try
            {
                for (int p = 0; p < parts.Count; p++)
                {
                    for (int i = 0; i < all.Length; i++) all[i].enabled = all[i] == parts[p].Renderer;
                    RenderCamera(stage);
                    ReadFrame(stage);
                    parts[p].Mask = PixelMask.FromPixels(stage.Pixels, EyeWidth, EyeHeight, LitThreshold);
                }
            }
            finally
            {
                for (int i = 0; i < all.Length; i++) all[i].enabled = wasEnabled[i];
            }
        }

        // For an overlap with the neck's dots mesh: which dot or X / O mark is nearest (by its centre on the screen).
        static string NeckPartNear(Play play, string a, string b, float px, float py)
        {
            string neckPath = PlayHudOverlap.Group(a) == PlayHudOverlap.NeckGroup ? PlayHudOverlap.PathOf(a)
                : PlayHudOverlap.Group(b) == PlayHudOverlap.NeckGroup ? PlayHudOverlap.PathOf(b) : null;
            if (neckPath != "Dots") return null;
            FloatingNeck neck = play.Stage.Neck;
            NeckLayout layout = neck.Layout;
            float h = layout.stringHeightMm;
            string best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < play.DotCount; i++)
            {
                Vector2 s;
                if (!ToEyePixels(play.Stage.Eye, neck.transform.TransformPoint(NeckMath.LocalMetres(play.Dots[i].xMm, h, play.Dots[i].zMm)), out s)) continue;
                float d = (s - new Vector2(px, py)).sqrMagnitude;
                if (d < bestD) { bestD = d; best = "fingertip dot, string " + play.Dots[i].stringNumber + ", finger " + play.Dots[i].finger; }
            }
            var options = new NeckBuildOptions();
            options.hasViewer = true;
            options.viewerMm = neck.transform.InverseTransformPoint(play.Stage.Head.position) / NeckMath.MetresPerMm;
            Vector3 row = new Vector3(neck.Sizes.markRowXMm, h, 0f);
            Vector3 view, along, across;
            NeckMeshBuilder.MarkFrame(options, row, out view, out along, out across);
            for (int s = 1; s <= NeckLayout.StringCount; s++)
            {
                if (play.Marks[s - 1] == StringMark.None) continue;
                Vector3 mm = row + across * (NeckMeshBuilder.MarkSlotOffset(s) * neck.Sizes.markPitchMm);
                Vector2 p;
                if (!ToEyePixels(play.Stage.Eye, neck.transform.TransformPoint(mm * NeckMath.MetresPerMm), out p)) continue;
                float d = (p - new Vector2(px, py)).sqrMagnitude;
                if (d < bestD) { bestD = d; best = (play.Marks[s - 1] == StringMark.Muted ? "X" : "O") + " mark, string " + s; }
            }
            return best;
        }

        // ------------------------------------------------------------------ rendering

        // Renders twice and lets texts whose glyph measurement is pending retry in between (Edit Mode runs no LateUpdate).
        static void RenderAndSettleTexts(Stage stage, Transform under)
        {
            if (s_TextRetry == null) s_TextRetry = typeof(HudText).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                RenderCamera(stage);
                bool pending = false;
                foreach (HudText text in under.GetComponentsInChildren<HudText>(true))
                {
                    if (text.SizeMeasured || !text.enabled) continue;
                    pending = true;
                    if (s_TextRetry != null) s_TextRetry.Invoke(text, null);
                }
                if (!pending) break;
            }
            RenderCamera(stage);
        }

        static void RenderCamera(Stage stage)
        {
            stage.Eye.targetTexture = stage.Target;
            stage.Eye.Render();
            stage.Eye.targetTexture = null;
        }

        // The last render into stage.Pixels (row 0 at the bottom). GetRawTextureData is a view: copied out at once.
        static void ReadFrame(Stage stage)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = stage.Target;
            stage.Readback.ReadPixels(new Rect(0f, 0f, EyeWidth, EyeHeight), 0, 0, false);
            RenderTexture.active = previous;
            NativeArray<Color32> data = stage.Readback.GetRawTextureData<Color32>();
            data.CopyTo(stage.Pixels);
        }

        static void WritePng(Stage stage, string path)
        {
            var opaque = new Color32[stage.Pixels.Length];
            for (int i = 0; i < opaque.Length; i++)
            {
                Color32 p = stage.Pixels[i];
                opaque[i] = new Color32(p.r, p.g, p.b, 255);   // colour only: what reaches the eye
            }
            var texture = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(opaque);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // ------------------------------------------------------------------ contact sheet

        const int ThumbScale = 4;
        const int ThumbWidth = EyeWidth / ThumbScale;
        const int ThumbHeight = EyeHeight / ThumbScale;
        const int SheetColumns = 4;
        const int SheetGap = 6;

        static Color32[] Thumbnail(Color32[] pixels)
        {
            var thumb = new Color32[ThumbWidth * ThumbHeight];
            for (int ty = 0; ty < ThumbHeight; ty++)
            {
                for (int tx = 0; tx < ThumbWidth; tx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int y = 0; y < ThumbScale; y++)
                    {
                        int row = (ty * ThumbScale + y) * EyeWidth + tx * ThumbScale;
                        for (int x = 0; x < ThumbScale; x++)
                        {
                            Color32 p = pixels[row + x];
                            r += p.r; g += p.g; b += p.b;
                        }
                    }
                    int n = ThumbScale * ThumbScale;
                    thumb[ty * ThumbWidth + tx] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
            return thumb;
        }

        static void WriteContactSheet(string path, List<Color32[]> thumbs, Stage stage)
        {
            int rows = (thumbs.Count + SheetColumns - 1) / SheetColumns;
            int width = SheetColumns * ThumbWidth + (SheetColumns + 1) * SheetGap;
            int height = rows * ThumbHeight + (rows + 1) * SheetGap;
            var sheet = new Color32[width * height];
            var gap = new Color32(48, 48, 48, 255);
            for (int i = 0; i < sheet.Length; i++) sheet[i] = gap;
            for (int k = 0; k < thumbs.Count; k++)
            {
                int column = k % SheetColumns, rowFromTop = k / SheetColumns;
                int x0 = SheetGap + column * (ThumbWidth + SheetGap);
                int y0 = height - (SheetGap + rowFromTop * (ThumbHeight + SheetGap)) - ThumbHeight;   // texture rows run bottom-up
                for (int y = 0; y < ThumbHeight; y++)
                    Array.Copy(thumbs[k], y * ThumbWidth, sheet, (y0 + y) * width + x0, ThumbWidth);
                DrawNumber(sheet, width, x0 + 8, y0 + ThumbHeight - 8, k + 1, 3);
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(sheet);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static readonly string[] Digits5x7 =
        {
            "01110100011001110101110011000101110", "00100011000010000100001000010001110", "01110100010000100010001000100011111",
            "11110000010000101110000010000111110", "00010001100101010010111110001000010", "11111100001111000001000011000101110",
            "00110010001000011110100011000101110", "11111000010001000100010000100001000", "01110100011000101110100011000101110",
            "01110100011000101111000010001001100",
        };

        // A state number in the top left corner of its thumbnail (yellow, 5 x 7 digits); (left, top) in texture coordinates.
        static void DrawNumber(Color32[] sheet, int width, int left, int top, int number, int scale)
        {
            string s = number.ToString(CultureInfo.InvariantCulture);
            var ink = new Color32(255, 200, 0, 255);
            for (int c = 0; c < s.Length; c++)
            {
                string glyph = Digits5x7[s[c] - '0'];
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 5; gx++)
                    {
                        if (glyph[gy * 5 + gx] != '1') continue;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int x = left + c * 6 * scale + gx * scale + dx;
                                int y = top - gy * scale - dy;
                                if (x >= 0 && x < width && y >= 0 && y * width + x < sheet.Length) sheet[y * width + x] = ink;
                            }
                    }
            }
        }

        // ------------------------------------------------------------------ geometry and formatting

        // A world point in pixels of the saved eye frame (origin bottom left), through the camera's own matrices.
        static bool ToEyePixels(Camera camera, Vector3 world, out Vector2 pixel)
        {
            Vector4 clip = camera.projectionMatrix * camera.worldToCameraMatrix * new Vector4(world.x, world.y, world.z, 1f);
            pixel = Vector2.zero;
            if (!(clip.w > 1e-6f)) return false;
            pixel = new Vector2((clip.x / clip.w * 0.5f + 0.5f) * EyeWidth, (clip.y / clip.w * 0.5f + 0.5f) * EyeHeight);
            return true;
        }

        static float PxToDegX(Stage stage, float px)
        {
            return Mathf.Atan((px + 0.5f - 0.5f * EyeWidth) / stage.FocalPx) * Mathf.Rad2Deg;
        }

        static float PxToDegY(Stage stage, float py)
        {
            return Mathf.Atan((py + 0.5f - 0.5f * EyeHeight) / stage.FocalPx) * Mathf.Rad2Deg;
        }

        // [left, right, bottom, top] in degrees from the centre of the eye's view (+x right, +y up).
        static string BoxDeg(Stage stage, int x0, int y0, int x1, int y1)
        {
            return "[" + F(PxToDegX(stage, x0 - 0.5f)) + ", " + F(PxToDegX(stage, x1 + 0.5f)) + ", " + F(PxToDegY(stage, y0 - 0.5f)) + ", "
                + F(PxToDegY(stage, y1 + 0.5f)) + "]";
        }

        static string DirectionDeg(Stage stage, Vector3 world)
        {
            Vector2 p;
            if (!ToEyePixels(stage.Eye, world, out p)) return "null";
            return "[" + F(PxToDegX(stage, p.x - 0.5f)) + ", " + F(PxToDegY(stage, p.y - 0.5f)) + "]";
        }

        static string DescribePlacement(FloatingNeck neck)
        {
            FloatingPlacement f = neck.Floating;
            return "floating: nut " + F(f.distanceM) + " m, azimuth " + F(f.azimuthDeg) + " deg, elevation " + F(f.elevationDeg) + " deg, tilt "
                + F(f.tiltDeg) + " deg, scale " + F(f.scale) + "; dev layout (frets 0-5); card anchor at fret " + F(PlayHudLayout.DefaultAnchorFret);
        }

        static string KindName(OverlapKind kind)
        {
            return kind == OverlapKind.Problem ? "problem" : kind == OverlapKind.ByDesign ? "byDesign" : "note";
        }

        static string F(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return "null";
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string B(bool value)
        {
            return value ? "true" : "false";
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
#endif
