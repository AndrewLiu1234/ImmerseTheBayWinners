// HudTextCalibration: measures, in renders made with one eye of the glasses, how tall HudText really draws its capital
// letters against the size it was asked for, so HudText.CapUnitsPerFontSize is a measurement and not a guess.
//
// Run it (do NOT pass -nographics: it renders):
//   in the open editor: menu Untitled Guitar App > HUD > Calibrate HudText          (writes to <repo>/build/previews/hudtext)
//   batch mode, editor closed:
//   Unity -batchmode -quit -projectPath <repo>/unity -executeMethod UntitledGuitarApp.Editor.HudTextCalibration.Calibrate
//         -hudTextCalOut <folder> -logFile -
// A relative -hudTextCalOut folder starts at the repository root (the folder above unity/). Everything is built in a
// temporary preview scene (never saved; the scene open in the editor is not touched) and no dialog is shown. On failure:
// exit code 1 in batch mode, an exception in the editor.
//
// What it renders, one element at a time, straight ahead of the eye, white on black:
//   hudtext-const  HudText "H" as compiled (the constant path: HudText.CapUnitsPerFontSize), asked 1.2 / 2 / 3 / 4 degrees,
//                  at 1.0 m and 1.2 m.
//   hudtext-legacy the same requests sized by the code HudText had before 14 Nov 12:00 (Renderer.localBounds of an "H" against
//                  Font.GetCharacterInfo, falling back to 0.1 unit per font pixel), copied below from HudText.cs at bd22a9a.
//   necklabel      NeckLabel "1" (Runtime/Neck, its own measured constant DigitUnitsPerFontSize) at the same requests.
//   control        POSITIVE CONTROL: a plain white quad exactly as tall as the asked cap height, measured by the same code.
//   planted        a quad built at 0.80 of the asked height: the measurement must report 0.80 (shows that it can see an error).
// Each cap height is measured from the pixels: per stem column, sub-pixel 50% crossings of the flat top and the flat bottom
// (the "H" stems, the "1" stem, the quad), in linear light; the columns are averaged. Pixels become metres at the element's
// own depth (h = px * depth / focalPx, focalPx = projectionMatrix.m11 * eyeHeight / 2), and metres become degrees with the
// same formula the request used (HudText.AngularHeightForSize), so "ratio" = drawn cap height / asked cap height.
// Output: hudtext-calibration.json (asked vs measured for every case, the units-per-font-size each render implies, and the
// recommended constant), plus one PNG per case (the full 1920x1080 eye frame).
//
// The camera is one glasses eye as StereoRig builds it: 1920 x 1080, projection = StereoMath.EyeProjection with the render
// flags' default diagonal FOV (52 degrees, RenderFlags.fovDiagonalDeg; IPD 0 and convergence 0 give that eye's symmetric
// frustum), at the head origin looking along +z.
//
// SOURCES (Unity 2021.3.3f1 offline docs, /Applications/Unity/Hub/Editor/2021.3.3f1/Documentation/en/ScriptReference/;
// quoted in docs/apis/unity/hudtext-calibration.md): Camera-projectionMatrix.html, RenderTexture-antiAliasing.html,
// Font.RequestCharactersInTexture.html, QualitySettings-activeColorSpace.html, Shader.Find.html, MenuItem.html, plus the
// ones NeckPreview.cs already cites (NewPreviewScene / ClosePreviewScene, Camera-scene, MoveGameObjectToScene, Camera.Render,
// Texture2D.ReadPixels, EncodeToPNG, ShaderUtil-allowAsyncCompilation, EditorApplication.Exit).
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
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Editor
{
    /// <summary>Measures HudText's real cap height in glasses-eye renders. Entry point: <see cref="Calibrate"/>.</summary>
    public static class HudTextCalibration
    {
        public const string OutArgument = "-hudTextCalOut";
        public const string DefaultOutFolder = "build/previews/hudtext";
        public const string MenuPath = "Untitled Guitar App/HUD/Calibrate HudText";
        public const string JsonFileName = "hudtext-calibration.json";

        public const int EyeWidth = 1920;
        public const int EyeHeight = 1080;
        const int Msaa = 4;                        // anti-aliased quad edges, so the control's edges are sub-pixel too
        const float NearM = 0.05f, FarM = 20f;

        static readonly float[] AskedDegrees = { 1.2f, 2f, 3f, 4f };
        static readonly float[] DistancesM = { 1.0f, 1.2f };
        const float PlantedFactor = 0.8f;
        const float PlantedDegrees = 3f;

        // The pre-12:00 HudText constants (HudText.cs at bd22a9a).
        const float LegacyFallbackWorldPerFontPixel = 0.1f;
        const float LegacyCapHeightEm = 0.716f;
        const int LegacyMaxMeasureFrames = 3;
        const string ReferenceGlyph = "H";
        const string DigitGlyph = "1";

        static readonly string[] QuadShaderNames = { "Unlit/Color", "Hidden/Internal-Colored", "Sprites/Default" };

        // ------------------------------------------------------------------ entry points

        /// <summary>Entry point for -executeMethod and for the menu. No dialogs; never touches the open scene.</summary>
        [MenuItem(MenuPath)]
        public static void Calibrate()
        {
            string failure = null;
            string folder = null;
            Result result = null;
            try
            {
                folder = ResolveOutFolder(Environment.GetCommandLineArgs());
                result = Run(folder);
            }
            catch (Exception e)
            {
                failure = e.ToString();
            }

            if (failure == null)
            {
                Debug.Log("[HudTextCalibration] wrote " + result.Files.Count + " file(s) to " + folder + "\n" + result.Summary);
                return;
            }
            Debug.LogError("[HudTextCalibration] FAILED: " + failure);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            else
                throw new InvalidOperationException("HudText calibration failed: " + failure);
        }

        /// <summary>The folder after -hudTextCalOut, or the default; relative paths start at the repository root.</summary>
        public static string ResolveOutFolder(string[] commandLine)
        {
            string folder = DefaultOutFolder;
            if (commandLine != null)
            {
                for (int i = 0; i < commandLine.Length - 1; i++)
                {
                    if (commandLine[i] == OutArgument && !string.IsNullOrEmpty(commandLine[i + 1]) && !commandLine[i + 1].StartsWith("-", StringComparison.Ordinal))
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

        // ------------------------------------------------------------------ data

        sealed class Result
        {
            public readonly List<string> Files = new List<string>();
            public string Summary = "";
        }

        sealed class Case
        {
            public string Kind;            // hudtext-const | hudtext-legacy | necklabel | control | planted
            public string Glyph;
            public float AskedDeg;
            public float DistanceM;
            public float AskedCapM;        // what the request means: SizeForAngularHeight(asked, distance)
            public float ExpectedRatio = 1f;
            public int FontSize;           // 0 for the quads
            public float WorldScale;       // lossy scale y of the element (text: TextMesh units -> metres)
            public string SizePath = "";   // legacy only: how the old code got its size
            public float LegacyCapUnitsPerFontSize;
            public float DepthM;
            public Edge Edge;
            public float MeasuredCapM;
            public float MeasuredDeg;
            public float Ratio;
            public float UnitsPerFontSize; // text only: measured cap height in TextMesh units per unit of fontSize
            public float ExpectedPx;       // quads only: f * h / depth
            public string File;
        }

        sealed class Edge
        {
            public bool Ok;
            public string Why = "";
            public float HeightPx, TopPx, BottomPx, MinHeightPx, MaxHeightPx;
            public float Crossing50Px;     // the same columns read by interpolating the 50% level between pixel centres
            public int Columns;
        }

        // ------------------------------------------------------------------ the run

        static Result Run(string folder)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("HudTextCalibration needs the graphics device: run Unity without -nographics.");
            Directory.CreateDirectory(folder);

            var result = new Result();
            var cases = new List<Case>();
            float diagonal = new RenderFlags().fovDiagonalDeg;              // what StereoRig applies unless the flags file overrides it
            float aspect = StereoMath.AspectOf(EyeWidth, EyeHeight);
            Matrix4x4 projection = StereoMath.EyeProjection(diagonal, aspect, NearM, FarM, 0f, StereoEye.Left, 0f);
            float focalPx = projection.m11 * EyeHeight * 0.5f;
            float verticalFov = StereoMath.VerticalFovDeg(diagonal, aspect);
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = RenderTexture.GetTemporary(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, Msaa);
            var image = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            Material quadMaterial = null;
            int msaaUsed = target.antiAliasing;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;   // no placeholder shader in any picture

                var root = new GameObject("HudTextCalibration (preview, not saved)");
                SceneManager.MoveGameObjectToScene(root, scene);

                var eyeObject = new GameObject("Eye");
                eyeObject.transform.SetParent(root.transform, false);
                Camera eye = eyeObject.AddComponent<Camera>();
                eye.enabled = false;                       // rendered by hand with Camera.Render
                eye.cameraType = CameraType.Preview;
                eye.scene = scene;
                eye.clearFlags = CameraClearFlags.SolidColor;
                eye.backgroundColor = new Color(0f, 0f, 0f, 1f);
                eye.allowHDR = false;
                eye.allowMSAA = true;
                eye.useOcclusionCulling = false;
                eye.nearClipPlane = NearM;
                eye.farClipPlane = FarM;
                eye.aspect = aspect;
                eye.fieldOfView = verticalFov;
                eye.projectionMatrix = projection;         // exactly one eye of StereoRig (Camera-projectionMatrix.html)
                eye.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                quadMaterial = CreateQuadMaterial();
                PrewarmFont();

                for (int d = 0; d < DistancesM.Length; d++)
                {
                    for (int a = 0; a < AskedDegrees.Length; a++)
                    {
                        float deg = AskedDegrees[a], dist = DistancesM[d];
                        cases.Add(MeasureHudText(root.transform, eye, target, image, linear, focalPx, folder, deg, dist));
                        cases.Add(MeasureLegacy(root.transform, eye, target, image, linear, focalPx, folder, deg, dist));
                        cases.Add(MeasureNeckLabel(root.transform, eye, target, image, linear, focalPx, folder, deg, dist));
                        cases.Add(MeasureQuad(root.transform, eye, target, image, linear, focalPx, folder, quadMaterial, deg, dist, 1f, "control"));
                    }
                }
                cases.Add(MeasureQuad(root.transform, eye, target, image, linear, focalPx, folder, quadMaterial, PlantedDegrees, 1f, PlantedFactor, "planted"));
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(image);
                if (quadMaterial != null) UnityEngine.Object.DestroyImmediate(quadMaterial);
                EditorSceneManager.ClosePreviewScene(scene);   // destroys everything that was moved into it
            }

            for (int i = 0; i < cases.Count; i++) result.Files.Add(cases[i].File);
            string failure = Check(cases, msaaUsed);
            string json = ToJson(cases, diagonal, verticalFov, focalPx, linear, msaaUsed, failure, out string summary);
            string jsonPath = Path.Combine(folder, JsonFileName);
            File.WriteAllText(jsonPath, json);
            result.Files.Add(jsonPath);
            result.Summary = summary;
            if (failure != null) throw new InvalidOperationException(failure + " (details in " + jsonPath + ")");
            return result;
        }

        // The measurement must be shown to work before any text number is believed: every control quad within its pixel
        // tolerance, the planted 0.80 quad read as 0.80, and every case measured at all.
        static string Check(List<Case> cases, int msaaUsed)
        {
            float tolPx = msaaUsed >= 4 ? 0.35f : 1.05f;   // 4x MSAA: edges within 1/8 px each; none: within half a pixel each
            var problems = new StringBuilder();
            for (int i = 0; i < cases.Count; i++)
            {
                Case c = cases[i];
                if (!c.Edge.Ok)
                {
                    problems.Append(c.Kind + " " + F(c.AskedDeg) + " deg at " + F(c.DistanceM) + " m: not measured (" + c.Edge.Why + "); ");
                    continue;
                }
                if (c.Kind == "control" || c.Kind == "planted")
                {
                    float expectedPx = c.ExpectedPx;
                    if (Mathf.Abs(c.Edge.HeightPx - expectedPx) > tolPx)
                        problems.Append(c.Kind + " " + F(c.AskedDeg) + " deg at " + F(c.DistanceM) + " m: " + F(c.Edge.HeightPx) + " px measured, "
                            + F(expectedPx) + " px projected (tolerance " + F(tolPx) + "); ");
                }
            }
            return problems.Length == 0 ? null : "MEASUREMENT NOT TRUSTWORTHY: " + problems;
        }

        // ------------------------------------------------------------------ the cases

        static Case MeasureHudText(Transform root, Camera eye, RenderTexture target, Texture2D image, bool linear, float focalPx,
            string folder, float deg, float dist)
        {
            var c = NewCase("hudtext-const", ReferenceGlyph, deg, dist, 1f);
            HudText text = HudText.Create(root, "HudText H");
            try
            {
                text.transform.localPosition = new Vector3(0f, 0f, dist);
                text.SetAngularHeight(deg, dist);
                text.SetText(ReferenceGlyph);
                TextMesh mesh = text.GetComponent<TextMesh>();
                c.FontSize = mesh != null ? mesh.fontSize : 0;
                c.WorldScale = text.transform.lossyScale.y;
                Finish(c, text.transform, eye, target, image, linear, focalPx, folder);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(text.gameObject);
            }
            return c;
        }

        static Case MeasureNeckLabel(Transform root, Camera eye, RenderTexture target, Texture2D image, bool linear, float focalPx,
            string folder, float deg, float dist)
        {
            var c = NewCase("necklabel", DigitGlyph, deg, dist, 1f);
            NeckLabel label = NeckLabel.Create(root, "NeckLabel 1");
            try
            {
                label.transform.localPosition = new Vector3(0f, 0f, dist);
                label.SetText(DigitGlyph);
                label.SetAngularHeight(deg, dist);
                TextMesh mesh = label.GetComponent<TextMesh>();
                c.FontSize = mesh != null ? mesh.fontSize : 0;
                c.WorldScale = label.transform.lossyScale.y;
                Finish(c, label.transform, eye, target, image, linear, focalPx, folder);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(label.gameObject);
            }
            return c;
        }

        // The old HudText, step by step as it ran: Init (font size for 1.2 degrees, empty text), SetAngularHeight (fallback scale,
        // then an immediate localBounds measurement), the words set afterwards (as ChordLabel does), and, only if the immediate
        // measurement failed, up to three re-measurements after a rendered frame (its LateUpdate), else the fallback.
        static Case MeasureLegacy(Transform root, Camera eye, RenderTexture target, Texture2D image, bool linear, float focalPx,
            string folder, float deg, float dist)
        {
            var c = NewCase("hudtext-legacy", ReferenceGlyph, deg, dist, 1f);
            var go = new GameObject("Legacy HudText H");
            go.transform.SetParent(root, false);
            try
            {
                go.transform.localPosition = new Vector3(0f, 0f, dist);
                TextMesh mesh = go.AddComponent<TextMesh>();
                MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = go.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                Font font = HudText.BuiltinFont;
                if (font != null)
                {
                    mesh.font = font;
                    renderer.sharedMaterial = font.material;
                }
                mesh.fontStyle = FontStyle.Bold;
                mesh.richText = false;
                mesh.characterSize = 1f;
                mesh.anchor = TextAnchor.MiddleCenter;
                mesh.alignment = TextAlignment.Center;
                mesh.color = Color.white;
                mesh.fontSize = HudText.FontSizeForCapDegrees(HudText.MinTextDegrees, HudText.DefaultPixelsPerDegree);
                mesh.text = string.Empty;

                // SetAngularHeight
                float degrees = HudText.ClampCapDegrees(deg, HudText.MinTextDegrees);
                float targetCapM = HudText.SizeForAngularHeight(degrees, dist);
                int size = HudText.FontSizeForCapDegrees(degrees, HudText.DefaultPixelsPerDegree);
                mesh.fontSize = size;
                float capLocal = size * LegacyFallbackWorldPerFontPixel * LegacyCapHeightEm;
                LegacyApplyScale(go.transform, targetCapM, capLocal);
                string real = string.Empty;
                if (LegacyTryMeasureCap(mesh, renderer, size, real, out float measured))
                {
                    capLocal = measured;
                    LegacyApplyScale(go.transform, targetCapM, capLocal);
                    c.SizePath = "bounds measured at once";
                }
                else
                {
                    // SetText after sizing, then LateUpdate's retries (one per rendered frame)
                    mesh.text = ReferenceGlyph;
                    real = ReferenceGlyph;
                    c.SizePath = "fallback (0.1 unit per font pixel): bounds unusable for " + LegacyMaxMeasureFrames + " frames";
                    for (int frame = 1; frame <= LegacyMaxMeasureFrames; frame++)
                    {
                        RenderOnce(eye, target);
                        if (LegacyTryMeasureCap(mesh, renderer, size, real, out measured))
                        {
                            capLocal = measured;
                            LegacyApplyScale(go.transform, targetCapM, capLocal);
                            c.SizePath = "bounds measured after " + frame + " rendered frame(s)";
                            break;
                        }
                    }
                }
                mesh.text = ReferenceGlyph;
                c.FontSize = size;
                c.LegacyCapUnitsPerFontSize = capLocal / size;
                c.WorldScale = go.transform.lossyScale.y;
                Finish(c, go.transform, eye, target, image, linear, focalPx, folder);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
            return c;
        }

        static Case MeasureQuad(Transform root, Camera eye, RenderTexture target, Texture2D image, bool linear, float focalPx,
            string folder, Material material, float deg, float dist, float factor, string kind)
        {
            var c = NewCase(kind, "quad", deg, dist, factor);
            var go = new GameObject(kind + " quad");
            go.transform.SetParent(root, false);
            Mesh quad = null;
            try
            {
                float height = c.AskedCapM * factor;
                float width = 2f * height;
                quad = BuildQuad(width, height);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                // a little off the pixel grid, so the edges do not sit exactly on pixel boundaries
                go.transform.localPosition = new Vector3(0.0011f * dist, 0.0007f * dist, dist);
                c.WorldScale = go.transform.lossyScale.y;
                c.ExpectedPx = focalPx * height / dist;
                Finish(c, go.transform, eye, target, image, linear, focalPx, folder);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                if (quad != null) UnityEngine.Object.DestroyImmediate(quad);
            }
            return c;
        }

        static Case NewCase(string kind, string glyph, float deg, float dist, float expectedRatio)
        {
            var c = new Case();
            c.Kind = kind;
            c.Glyph = glyph;
            c.AskedDeg = deg;
            c.DistanceM = dist;
            c.AskedCapM = HudText.SizeForAngularHeight(deg, dist);
            c.ExpectedRatio = expectedRatio;
            return c;
        }

        // Render twice (the first fills the font texture and builds the text mesh), read the second, measure, save the PNG.
        static void Finish(Case c, Transform element, Camera eye, RenderTexture target, Texture2D image, bool linear, float focalPx, string folder)
        {
            RenderOnce(eye, target);
            RenderOnce(eye, target);
            Color32[] pixels = ReadBack(target, image);
            float[] lum = Luminance(pixels, linear);
            c.Edge = MeasureFlatHeightPx(lum, EyeWidth, EyeHeight);
            c.DepthM = eye.worldToCameraMatrix.MultiplyPoint(element.position).z * -1f;   // camera space looks along -z
            if (c.Edge.Ok)
            {
                c.MeasuredCapM = c.Edge.HeightPx * c.DepthM / focalPx;
                c.MeasuredDeg = HudText.AngularHeightForSize(c.MeasuredCapM, c.DepthM);
                c.Ratio = c.AskedCapM > 0f ? c.MeasuredCapM / c.AskedCapM : 0f;
                if (c.FontSize > 0 && c.WorldScale > 0f) c.UnitsPerFontSize = c.MeasuredCapM / (c.WorldScale * c.FontSize);
            }
            string name = c.Kind + "-" + c.Glyph + "-" + F1(c.AskedDeg) + "deg-" + F1(c.DistanceM) + "m.png";
            for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
            image.SetPixels32(pixels);
            image.Apply(false);
            c.File = Path.Combine(folder, name);
            File.WriteAllBytes(c.File, image.EncodeToPNG());
        }

        // ------------------------------------------------------------------ the pre-12:00 HudText sizing path (copied, bd22a9a)

        // Cap height of the reference glyph in the TextMesh's local units (at localScale 1, characterSize 1, current fontSize).
        static bool LegacyTryMeasureCap(TextMesh mesh, MeshRenderer renderer, int fontSize, string real, out float capLocal)
        {
            capLocal = 0f;
            Font font = mesh.font;
            if (font == null) return false;

            bool haveInfo = false;
            float glyphW = 0f, glyphH = 0f;
            font.RequestCharactersInTexture(ReferenceGlyph, fontSize, FontStyle.Bold);
            if (font.GetCharacterInfo(ReferenceGlyph[0], out CharacterInfo info, fontSize, FontStyle.Bold))
            {
                glyphW = Mathf.Abs(info.maxX - info.minX);
                glyphH = Mathf.Abs(info.maxY - info.minY);
                haveInfo = glyphW > 0.5f && glyphH > 0.5f;
            }

            bool wasVisible = renderer.enabled;
            if (!wasVisible) renderer.enabled = true;
            mesh.text = ReferenceGlyph;
            Vector3 size = renderer.localBounds.size;
            mesh.text = real;
            if (!wasVisible) renderer.enabled = false;

            if (!Usable(size.x) || !Usable(size.y)) return false;

            if (haveInfo)
            {
                float measuredRatio = size.x / size.y;
                float expectedRatio = glyphW / glyphH;
                if (Mathf.Abs(measuredRatio / expectedRatio - 1f) > 0.3f) return false;
                capLocal = size.x / glyphW * glyphH;
            }
            else
            {
                capLocal = size.y;
            }
            return Usable(capLocal);
        }

        static void LegacyApplyScale(Transform t, float targetCapM, float capLocal)
        {
            float parentScale = 1f;
            Transform parent = t.parent;
            if (parent != null)
            {
                float ps = parent.lossyScale.y;
                if (ps > 1e-6f) parentScale = ps;
            }
            float s = targetCapM / (capLocal * parentScale);
            if (Usable(s)) t.localScale = new Vector3(s, s, s);
        }

        static bool Usable(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && v > 1e-6f;
        }

        // ------------------------------------------------------------------ measuring pixels

        /// <summary>
        /// Height in pixels of the one bright element in the frame, from its flat top and flat bottom, in every column whose
        /// lit run is (nearly) the longest in the frame and fully bright inside (the stems of an "H", the stem of a "1", a
        /// quad; not the crossbar, the flag or an anti-aliased side column). Each edge is where the coverage crosses 50%, found
        /// by the AREA method: edge = a row inside the plateau minus (or plus) the light beyond it divided by the plateau. That is
        /// exact for an anti-aliased (area-sampled) straight edge and for any blur that keeps the light, unlike interpolating the
        /// 50% level between two pixel centres, which misplaces a one-pixel-sharp edge by up to 0.1 px (an offline check of
        /// this function on synthetic edges, 14 Nov, showed both); that interpolated figure is still reported (Crossing50Px).
        /// Columns whose top or bottom is more than half a pixel from the median are dropped. <paramref name="lum"/> is linear
        /// light, row 0 at the bottom.
        /// </summary>
        public static float MeasureFlatHeightPx(float[] lum, int width, int height, out int columns, out string why)
        {
            Edge e = MeasureFlatHeightPx(lum, width, height);
            columns = e.Columns;
            why = e.Why;
            return e.Ok ? e.HeightPx : 0f;
        }

        static Edge MeasureFlatHeightPx(float[] lum, int width, int height)
        {
            var e = new Edge();
            float max = 0f;
            for (int i = 0; i < lum.Length; i++) if (lum[i] > max) max = lum[i];
            if (max < 0.05f)
            {
                e.Why = "nothing lit";
                return e;
            }

            float runThreshold = 0.25f * max;
            var runStart = new int[width];
            var runLength = new int[width];
            int longest = 0;
            for (int x = 0; x < width; x++)
            {
                int bestStart = -1, bestLen = 0, start = -1;
                for (int y = 0; y <= height; y++)
                {
                    bool lit = y < height && lum[y * width + x] >= runThreshold;
                    if (lit && start < 0) start = y;
                    if (!lit && start >= 0)
                    {
                        int len = y - start;
                        if (len > bestLen) { bestLen = len; bestStart = start; }
                        start = -1;
                    }
                }
                runStart[x] = bestStart;
                runLength[x] = bestLen;
                if (bestLen > longest) longest = bestLen;
            }
            if (longest < 6)
            {
                e.Why = "element too small (" + longest + " px)";
                return e;
            }

            int slack = Mathf.Max(2, Mathf.CeilToInt(0.03f * longest));
            const int Margin = 3;   // rows of the plateau next to each edge, and rows outside it, that the area sums cover
            var tops = new List<float>();
            var bottoms = new List<float>();
            var crossings = new List<float>();
            for (int x = 0; x < width; x++)
            {
                if (runLength[x] < longest - slack) continue;
                int y0 = runStart[x], y1 = runStart[x] + runLength[x] - 1;
                if (y0 < Margin + 1 || y1 > height - Margin - 2) continue;   // touching the frame edge: no outside to measure
                if (y1 - y0 + 1 < 2 * Margin + 2) continue;                   // too short to have a plateau
                float plateau = 0f;
                int n = 0;
                for (int y = y0 + Margin; y <= y1 - Margin; y++) { plateau += lum[y * width + x]; n++; }
                plateau /= n;
                if (plateau < 0.85f * max) continue;        // a side column of a stem: only partly covered

                // area method: bottom edge = (y0 + Margin) - light below that row / plateau; top edge likewise from above
                float below = 0f, above = 0f;
                for (int y = y0 - Margin; y < y0 + Margin; y++) below += lum[y * width + x];
                for (int y = y1 - Margin + 1; y <= y1 + Margin; y++) above += lum[y * width + x];
                float bottom = (y0 + Margin) - below / plateau;
                float top = (y1 - Margin + 1) + above / plateau;

                // the interpolated 50% crossings (reported for comparison only)
                float t = 0.5f * plateau;
                int yb = y0 - Margin;
                while (yb <= y1 && lum[yb * width + x] < t) yb++;
                int yt = y1 + Margin;
                while (yt >= y0 && lum[yt * width + x] < t) yt--;
                float crossing = float.NaN;
                if (yb > 0 && yb <= y1 && yt >= y0 && yt < height - 1)
                {
                    float lb0 = lum[(yb - 1) * width + x], lb1 = lum[yb * width + x];
                    float lt0 = lum[yt * width + x], lt1 = lum[(yt + 1) * width + x];
                    float cb = (yb - 1) + 0.5f + (lb1 > lb0 ? (t - lb0) / (lb1 - lb0) : 0.5f);
                    float ct = yt + 0.5f + (lt0 > lt1 ? (lt0 - t) / (lt0 - lt1) : 0.5f);
                    crossing = ct - cb;
                }

                tops.Add(top);
                bottoms.Add(bottom);
                crossings.Add(crossing);
            }
            if (tops.Count < 2)
            {
                e.Why = "fewer than 2 usable columns";
                return e;
            }

            float medianTop = Median(tops), medianBottom = Median(bottoms);
            float sum = 0f, sumTop = 0f, sumBottom = 0f, sumCross = 0f, minH = float.MaxValue, maxH = float.MinValue;
            int used = 0, usedCross = 0;
            for (int i = 0; i < tops.Count; i++)
            {
                if (Mathf.Abs(tops[i] - medianTop) > 0.5f || Mathf.Abs(bottoms[i] - medianBottom) > 0.5f) continue;
                float h = tops[i] - bottoms[i];
                sum += h;
                sumTop += tops[i];
                sumBottom += bottoms[i];
                if (!float.IsNaN(crossings[i])) { sumCross += crossings[i]; usedCross++; }
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
                used++;
            }
            if (used < 2)
            {
                e.Why = "columns disagree by more than half a pixel";
                return e;
            }
            e.Ok = true;
            e.Columns = used;
            e.HeightPx = sum / used;
            e.TopPx = sumTop / used;
            e.BottomPx = sumBottom / used;
            e.MinHeightPx = minH;
            e.MaxHeightPx = maxH;
            e.Crossing50Px = usedCross > 0 ? sumCross / usedCross : float.NaN;
            return e;
        }

        static float Median(List<float> values)
        {
            var copy = new List<float>(values);
            copy.Sort();
            int n = copy.Count;
            return (n & 1) == 1 ? copy[n / 2] : 0.5f * (copy[n / 2 - 1] + copy[n / 2]);
        }

        // The brightest channel of each pixel, in linear light. In Linear colour space the render texture holds sRGB values
        // (blended in linear light), so they are decoded; in Gamma colour space the blend happened on the stored values, so a
        // white glyph's stored value is already proportional to its coverage.
        static float[] Luminance(Color32[] pixels, bool linear)
        {
            var lum = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                float v = Mathf.Max(p.r, Mathf.Max(p.g, p.b)) / 255f;
                lum[i] = linear ? SrgbToLinear(v) : v;
            }
            return lum;
        }

        static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        // ------------------------------------------------------------------ rendering helpers

        static void RenderOnce(Camera eye, RenderTexture target)
        {
            eye.targetTexture = target;
            eye.Render();
            eye.targetTexture = null;
        }

        static Color32[] ReadBack(RenderTexture target, Texture2D image)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;   // an anti-aliased target is read as its resolved surface (RenderTexture-antiAliasing.html)
            image.ReadPixels(new Rect(0f, 0f, EyeWidth, EyeHeight), 0, 0, false);
            image.Apply(false);
            RenderTexture.active = previous;
            return image.GetPixels32();
        }

        static void PrewarmFont()
        {
            Font font = HudText.BuiltinFont;
            if (font == null) throw new InvalidOperationException("the built-in font did not load (see the hud_font log line)");
            for (int a = 0; a < AskedDegrees.Length; a++)
            {
                int size = HudText.FontSizeForCapDegrees(AskedDegrees[a], HudText.DefaultPixelsPerDegree);
                font.RequestCharactersInTexture(ReferenceGlyph + DigitGlyph, size, FontStyle.Bold);
            }
        }

        static Material CreateQuadMaterial()
        {
            for (int i = 0; i < QuadShaderNames.Length; i++)
            {
                Shader shader = Shader.Find(QuadShaderNames[i]);   // editor only, built-in shaders (Shader.Find.html)
                if (shader == null) continue;
                var material = new Material(shader);
                material.hideFlags = HideFlags.HideAndDontSave;
                material.color = Color.white;
                return material;
            }
            throw new InvalidOperationException("no unlit shader found for the control quad (tried " + string.Join(", ", QuadShaderNames) + ")");
        }

        // A quad in the XY plane, drawn from both sides (both windings), so the cull mode of whichever shader loaded does not matter.
        static Mesh BuildQuad(float width, float height)
        {
            float w = 0.5f * width, h = 0.5f * height;
            var mesh = new Mesh();
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.vertices = new[] { new Vector3(-w, -h, 0f), new Vector3(-w, h, 0f), new Vector3(w, h, 0f), new Vector3(w, -h, 0f) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ the report

        static string ToJson(List<Case> cases, float diagonal, float verticalFov, float focalPx, bool linear, int msaa, string failure, out string summary)
        {
            var sb = new StringBuilder(16384);
            sb.Append("{\n");
            Prop(sb, 1, "tool", "HudTextCalibration"); sb.Append(",\n");
            Prop(sb, 1, "unity", Application.unityVersion); sb.Append(",\n");
            Prop(sb, 1, "colorSpace", linear ? "Linear" : "Gamma"); sb.Append(",\n");
            Prop(sb, 1, "graphics", SystemInfo.graphicsDeviceType.ToString()); sb.Append(",\n");
            Prop(sb, 1, "batchMode", Application.isBatchMode); sb.Append(",\n");
            Prop(sb, 1, "verdict", failure == null ? "controls passed" : failure); sb.Append(",\n");
            sb.Append("  \"eye\": { ");
            sb.Append("\"width\": " + EyeWidth + ", \"height\": " + EyeHeight + ", \"diagonalFovDeg\": " + N(diagonal) + ", \"verticalFovDeg\": " + N(verticalFov)
                + ", \"focalPx\": " + N(focalPx) + ", \"pxPerDegAtCentre\": " + N(focalPx * Mathf.Deg2Rad) + ", \"msaa\": " + msaa + " },\n");
            sb.Append("  \"constants\": { \"hudTextCapUnitsPerFontSize\": " + N(HudText.CapUnitsPerFontSize)
                + ", \"neckLabelDigitUnitsPerFontSize\": " + N(NeckLabel.DigitUnitsPerFontSize)
                + ", \"legacyFallbackCapUnitsPerFontSize\": " + N(LegacyFallbackWorldPerFontPixel * LegacyCapHeightEm) + " },\n");

            sb.Append("  \"cases\": [\n");
            for (int i = 0; i < cases.Count; i++)
            {
                Case c = cases[i];
                sb.Append("    { ");
                sb.Append("\"kind\": " + Q(c.Kind) + ", \"glyph\": " + Q(c.Glyph) + ", \"askedDeg\": " + N(c.AskedDeg) + ", \"distanceM\": " + N(c.DistanceM));
                sb.Append(", \"askedCapMm\": " + N(c.AskedCapM * 1000f) + ", \"expectedRatio\": " + N(c.ExpectedRatio));
                sb.Append(", \"measuredCapPx\": " + N(c.Edge.HeightPx) + ", \"crossing50Px\": " + N(c.Edge.Ok ? c.Edge.Crossing50Px : 0f)
                    + ", \"measuredCapMm\": " + N(c.MeasuredCapM * 1000f));
                sb.Append(", \"measuredDeg\": " + N(c.MeasuredDeg) + ", \"ratio\": " + N(c.Ratio));
                sb.Append(", \"depthM\": " + N(c.DepthM) + ", \"columns\": " + c.Edge.Columns + ", \"heightPxMin\": " + N(c.Edge.Ok ? c.Edge.MinHeightPx : 0f)
                    + ", \"heightPxMax\": " + N(c.Edge.Ok ? c.Edge.MaxHeightPx : 0f));
                if (c.FontSize > 0)
                    sb.Append(", \"fontSize\": " + c.FontSize + ", \"worldScale\": " + N(c.WorldScale) + ", \"unitsPerFontSize\": " + N(c.UnitsPerFontSize));
                if (c.ExpectedPx > 0f) sb.Append(", \"projectedPx\": " + N(c.ExpectedPx));
                if (c.Kind == "hudtext-legacy")
                    sb.Append(", \"legacySizePath\": " + Q(c.SizePath) + ", \"legacyCapUnitsPerFontSize\": " + N(c.LegacyCapUnitsPerFontSize));
                if (!c.Edge.Ok) sb.Append(", \"error\": " + Q(c.Edge.Why));
                sb.Append(", \"png\": " + Q(Path.GetFileName(c.File)) + " }");
                sb.Append(i + 1 < cases.Count ? ",\n" : "\n");
            }
            sb.Append("  ],\n");

            Stats constant = StatsOf(cases, "hudtext-const", false);
            Stats legacy = StatsOf(cases, "hudtext-legacy", false);
            Stats neck = StatsOf(cases, "necklabel", false);
            Stats control = StatsOf(cases, "control", false);
            Stats planted = StatsOf(cases, "planted", false);
            Stats units = StatsOf(cases, "hudtext-const", true);
            Stats legacyUnits = StatsOf(cases, "hudtext-legacy", true);
            Stats neckUnits = StatsOf(cases, "necklabel", true);
            float recommended = units.Count > 0 ? units.Mean : 0f;

            sb.Append("  \"summary\": {\n");
            StatsJson(sb, "hudTextConstantRatio", constant); sb.Append(",\n");
            StatsJson(sb, "hudTextLegacyRatio", legacy); sb.Append(",\n");
            StatsJson(sb, "neckLabelRatio", neck); sb.Append(",\n");
            StatsJson(sb, "controlRatio", control); sb.Append(",\n");
            StatsJson(sb, "plantedRatio_expect0p80", planted); sb.Append(",\n");
            StatsJson(sb, "hudTextCapUnitsPerFontSize_measured", units); sb.Append(",\n");
            StatsJson(sb, "legacyRenderCapUnitsPerFontSize_measured", legacyUnits); sb.Append(",\n");
            StatsJson(sb, "neckLabelDigitUnitsPerFontSize_measured", neckUnits); sb.Append(",\n");
            sb.Append("    \"recommendedCapUnitsPerFontSize\": " + N(recommended) + ",\n");
            sb.Append("    \"constantPathRatiosIfRecommendedAdopted\": [");
            bool first = true;
            for (int i = 0; i < cases.Count; i++)
            {
                if (cases[i].Kind != "hudtext-const" || !cases[i].Edge.Ok || recommended <= 0f) continue;
                sb.Append((first ? "" : ", ") + N(cases[i].UnitsPerFontSize / recommended));
                first = false;
            }
            sb.Append("]\n  }\n}\n");

            summary = "asked vs measured (drawn/asked cap height, mean [min..max]): constant path " + StatsText(constant)
                + " | legacy path " + StatsText(legacy) + " | NeckLabel " + StatsText(neck) + " | control quad " + StatsText(control)
                + " | planted 0.80 quad " + StatsText(planted) + " | recommended HudText.CapUnitsPerFontSize = " + N(recommended)
                + " (now " + N(HudText.CapUnitsPerFontSize) + ")" + (failure == null ? "" : " | " + failure);
            return sb.ToString();
        }

        struct Stats
        {
            public int Count;
            public float Mean, Min, Max;
        }

        static Stats StatsOf(List<Case> cases, string kind, bool units)
        {
            var s = new Stats { Min = float.MaxValue, Max = float.MinValue };
            float sum = 0f;
            for (int i = 0; i < cases.Count; i++)
            {
                Case c = cases[i];
                if (c.Kind != kind || !c.Edge.Ok) continue;
                float v = units ? c.UnitsPerFontSize : c.Ratio;
                sum += v;
                s.Count++;
                if (v < s.Min) s.Min = v;
                if (v > s.Max) s.Max = v;
            }
            if (s.Count > 0) s.Mean = sum / s.Count;
            else { s.Min = 0f; s.Max = 0f; }
            return s;
        }

        static void StatsJson(StringBuilder sb, string name, Stats s)
        {
            sb.Append("    " + Q(name) + ": { \"n\": " + s.Count + ", \"mean\": " + N(s.Mean) + ", \"min\": " + N(s.Min) + ", \"max\": " + N(s.Max) + " }");
        }

        static string StatsText(Stats s)
        {
            return s.Count == 0 ? "n/a" : N3(s.Mean) + " [" + N3(s.Min) + ".." + N3(s.Max) + "]";
        }

        static void Prop(StringBuilder sb, int indent, string name, string value)
        {
            sb.Append(new string(' ', indent * 2) + Q(name) + ": " + Q(value));
        }

        static void Prop(StringBuilder sb, int indent, string name, bool value)
        {
            sb.Append(new string(' ', indent * 2) + Q(name) + ": " + (value ? "true" : "false"));
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string N(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        static string N3(float v)
        {
            return v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        static string F(float v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        static string F1(float v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
