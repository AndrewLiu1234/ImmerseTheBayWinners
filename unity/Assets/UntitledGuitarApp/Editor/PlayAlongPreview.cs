#if UNITY_EDITOR
// PlayAlongPreview: plays riptide-loop and happy-birthday through the TEMPORARY dev clock (Runtime/Practice/Dev) into the play
// HUD (PlayAlongPresenter), with the floating neck and the posed fretting ghost hand, and renders the glasses' LEFT eye at
// eight moments of each song, to PNG files, with no glasses and no Play Mode.
//
// Batch mode (never -nographics: rendering needs the graphics device; refused while the editor has the project open):
//   Unity -batchmode -quit -projectPath <repo>/unity -executeMethod UntitledGuitarApp.Editor.PlayAlongPreview.RenderPreviews
//         -playPreviewOut <folder> -logFile -
// In the editor: menu "Untitled Guitar App/Play Along/Render Previews" (no dialogs; nothing in the open scene is touched:
// every object lives in a temporary preview scene that is closed afterwards).
// Without -playPreviewOut the files go to <repo>/build/previews/play-along/.
//
// Output: <song>-NN-<moment>.png (exactly the left-eye frame the glasses get; black = not shown), <song>-contact.png (the
// eight frames at a quarter size), planted-overlap.png and play-along-report.json: per frame the PracticeFrame values, the
// screen rectangle of every visible HUD part, of the neck's fingertip dots and of the hand, and a RELATIONAL overlap report
// (each HUD part against every other HUD group, against each fingertip dot and against the hand). The overlap checker is
// shown to fire on every run: one extra frame plants a real HUD disc on the first fingertip dot, and the run fails unless
// the checker reports it.
//
// SOURCES (offline documentation of Unity 2021.3.3f1, Unity.app/Contents/Documentation/en/ScriptReference), the same set
// HudLearnPreview.cs quotes: SceneManagement.EditorSceneManager.NewPreviewScene ("Creates a new preview Scene. Any object
// added to a preview Scene will only be rendered in that Scene.") and ClosePreviewScene; Camera-scene ("If not null, the
// camera will only render the contents of the specified Scene"); Texture2D.ReadPixels ("(0,0) is lower left");
// Camera-worldToCameraMatrix ("camera's forward is the negative Z axis") and Camera-projectionMatrix (the CPU-side
// projection, used here to map points to pixels without Camera.pixelRect, which is not the eye size in batch mode);
// Renderer-bounds ("The bounding box of the renderer in world space"); Manual/EditorCommandLineArguments (-executeMethod:
// "throw an exception which causes Unity to exit with return code 1").
//
// In Edit Mode Update and LateUpdate do not run, so this script advances everything itself (clock, presenter, card,
// count-in, hand) in 1/60 s steps, and renders each frame twice (the first render fills the font texture).
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
using UntitledGuitarApp.Runtime.Hud.Play;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;
using UntitledGuitarApp.Runtime.Practice;
using UntitledGuitarApp.Runtime.Practice.Dev;

namespace UntitledGuitarApp.Editor
{
    /// <summary>Renders the play-along previews. Entry point for -executeMethod: <see cref="RenderPreviews"/>.</summary>
    public static class PlayAlongPreview
    {
        public const string OutArgument = "-playPreviewOut";
        public const string MenuPath = "Untitled Guitar App/Play Along/Render Previews";
        public const string SubFolder = "play-along";
        public const string ReportFileName = "play-along-report.json";
        public static readonly string[] Songs = { "riptide-loop", "happy-birthday" };

        const int EyeWidth = 1920;
        const int EyeHeight = 1080;
        const float DiagonalFovDeg = 52f;
        const float Step = 1f / 60f;
        const float MaxSongSeconds = 240f;
        const float WantedPreset = 0.75f;
        const float MinOverlapPx = 4f;      // pixel area below which two rectangles only touch
        const int ThumbScale = 4;

        sealed class Moment
        {
            public string Name;
            public Func<DevPlayAlongClock, bool> When;
            public bool Done;
        }

        struct Element
        {
            public string Group;
            public string Name;
            public Rect Rect;
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>For -executeMethod. Throws on failure (batch exit code 1).</summary>
        public static void RenderPreviews()
        {
            string folder = ResolveOutFolder(Environment.GetCommandLineArgs());
            string problem = Render(folder);
            if (problem != null) throw new InvalidOperationException("[PlayAlongPreview] " + problem);
        }

        [MenuItem(MenuPath, false, 230)]
        static void RenderFromMenu()
        {
            string problem = Render(ResolveOutFolder(null));
            if (problem != null) Debug.LogError("[PlayAlongPreview] " + problem);
        }

        static string ProjectRoot { get { return Path.GetDirectoryName(Application.dataPath); } }

        static string DefaultFolder
        {
            get { return Path.Combine(Path.GetDirectoryName(ProjectRoot) ?? ProjectRoot, "build", "previews", SubFolder); }
        }

        static string ResolveOutFolder(string[] args)
        {
            if (args != null)
            {
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (!string.Equals(args[i], OutArgument, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = args[i + 1];
                    if (!string.IsNullOrEmpty(v) && !v.StartsWith("-", StringComparison.Ordinal))
                        return Path.IsPathRooted(v) ? v : Path.Combine(ProjectRoot, v);
                }
            }
            return DefaultFolder;
        }

        // ------------------------------------------------------------------ the run

        /// <summary>Renders everything into <paramref name="folder"/>. Returns null on success, else what went wrong.</summary>
        static string Render(string folder)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return "needs the graphics device: run Unity without -nographics";
            Directory.CreateDirectory(folder);

            var json = new StringBuilder(64 * 1024);
            json.Append("{\n  \"unity\": ").Append(Q(Application.unityVersion))
                .Append(",\n  \"graphics\": ").Append(Q(SystemInfo.graphicsDeviceType.ToString()))
                .Append(",\n  \"eye\": \"left, ").Append(EyeWidth).Append('x').Append(EyeHeight).Append(", ")
                .Append(F(DiagonalFovDeg)).Append(" deg diagonal\"")
                .Append(",\n  \"largeText\": ").Append(FlagStore.Current.largeText ? "true" : "false")
                .Append(",\n  \"frames\": [\n");

            var written = new List<string>();
            string problem = null;
            bool firstFrame = true;
            bool asyncBefore = ShaderUtil.allowAsyncCompilation;
            var texture = new Texture2D(EyeWidth, EyeHeight, TextureFormat.RGBA32, false);
            RenderTexture target = RenderTexture.GetTemporary(EyeWidth, EyeHeight, 24, RenderTextureFormat.ARGB32);
            int plantedOverlaps = -1;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                for (int s = 0; s < Songs.Length && problem == null; s++)
                {
                    bool plant = s == 0;
                    problem = RenderSong(Songs[s], plant, folder, target, texture, json, written, ref firstFrame, ref plantedOverlaps);
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncBefore;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(texture);
            }

            if (problem == null && plantedOverlaps <= 0)
                problem = "the overlap checker did not fire on the planted overlap (it reported " + plantedOverlaps + ")";
            json.Append("\n  ],\n  \"plantedOverlapFired\": ").Append(plantedOverlaps > 0 ? "true" : "false")
                .Append(",\n  \"plantedOverlaps\": ").Append(plantedOverlaps)
                .Append(",\n  \"result\": ").Append(Q(problem ?? "ok")).Append("\n}\n");
            string reportPath = Path.Combine(folder, ReportFileName);
            File.WriteAllText(reportPath, json.ToString());
            written.Add(reportPath);
            Debug.Log("[PlayAlongPreview] wrote " + written.Count + " file(s) to " + folder + (problem != null ? " (FAILED: " + problem + ")" : ""));
            return problem;
        }

        static string RenderSong(string songId, bool plant, string folder, RenderTexture target, Texture2D texture,
            StringBuilder json, List<string> written, ref bool firstFrame, ref int plantedOverlaps)
        {
            DevSong song;
            string error;
            if (!DevSongReader.LoadFile(DevSongReader.PathFor(DevSongReader.SongsFolder, songId), out song, out error))
                return "song " + songId + " did not load: " + error;

            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            var thumbs = new List<Color32[]>();
            try
            {
                root = new GameObject("PlayAlongPreview " + songId);
                SceneManager.MoveGameObjectToScene(root, scene);

                // The seated player's head at the origin, looking along +z; the neck hangs head-locked in front of it.
                var head = new GameObject("Head");
                head.transform.SetParent(root.transform, false);
                FloatingNeck neck = FloatingNeck.Create(root.transform);
                if (!neck.Build(DevNeckLayout.Create())) return "the dev neck layout did not build";
                neck.PlaceFloating(head.transform);

                PlayAlongPresenter presenter = PlayAlongPresenter.Create(root.transform, head.transform, neck);

                var library = new GhostPoseLibrary();
                library.Reload();
                PosedGhostHand hand = PosedGhostHand.Create(neck.Socket, GhostPose.HandFretting, library);
                bool handOk = hand != null && hand.IsAvailable;

                FlagSet flags = FlagStore.Current;
                float ipdM = (flags.render != null ? flags.render.ipdMm : 63f) * 0.001f;
                Camera eye = MakeEye(head.transform, scene, ipdM);

                var clock = new DevPlayAlongClock();
                float preset = DevTimeline.NearestPreset(WantedPreset, flags.tempoPresets);
                if (!clock.Start(song, preset, true)) return "the clock refused " + songId;
                presenter.SetSong(song);
                if (handOk) hand.SetPose(song.Runs[0].Chord, true);

                List<Moment> moments = MomentsFor(song);
                float t = 0f;
                int index = 0;
                bool resultsSettled = false;
                while (index < moments.Count)
                {
                    if (t > MaxSongSeconds) return songId + ": moment " + NextPending(moments) + " never came";
                    clock.Tick(Step);
                    presenter.PresentClock(clock, Step);
                    presenter.Card.Advance(Step);
                    presenter.Card.CountIn.Advance(Step);
                    if (handOk)
                    {
                        string shape = clock.Frame.Chord ?? clock.Frame.NextChord;
                        if (shape != null) hand.SetPose(shape, false);
                        hand.Tick(Step);
                    }
                    t += Step;
                    if (clock.State == DevPlayState.Ended && !resultsSettled)
                    {
                        resultsSettled = true;
                        continue;   // one more step so the results card is up
                    }
                    for (int m = 0; m < moments.Count; m++)
                    {
                        Moment moment = moments[m];
                        if (moment.Done || !moment.When(clock)) continue;
                        moment.Done = true;
                        index++;
                        string name = songId + "-" + moment.Name;
                        Color32[] pixels = Capture(eye, target, texture);
                        string png = Path.Combine(folder, name + ".png");
                        WritePng(png, pixels, texture);
                        written.Add(png);
                        thumbs.Add(Thumbnail(pixels));
                        List<Element> hud = CollectHud(presenter, eye);
                        List<Element> others = CollectNeckAndHand(neck, presenter.ShownShape, handOk ? hand : null, eye);
                        List<string> overlaps = Overlaps(hud, others);
                        AppendFrame(json, ref firstFrame, name, clock, presenter, handOk, hud, others, overlaps, Lit(pixels));
                    }
                }

                if (plant)
                {
                    // A real HUD part (a disc under the card) put exactly on the first fingertip dot: the checker must flag it.
                    FingertipDot[] dots = new FingertipDot[FloatingNeck.MaxDots];
                    StringMark[] marks = new StringMark[NeckLayout.StringCount];
                    int n = DevNeckLayout.ChordShape(neck.Layout, presenter.ShownShape ?? "G", dots, marks);
                    if (n > 0)
                    {
                        HudShape planted = HudShape.Create(presenter.Card.transform, "PlantedOverlap", HudShape.Disc);
                        planted.SetColor(Color.white, 1f);
                        planted.SetSize(0.012f);
                        planted.Transform.position = neck.Socket.TransformPoint(new Vector3(dots[0].xMm * 0.001f, 0.004f, dots[0].zMm * 0.001f));
                        presenter.ResultsCard.Hide();
                        Color32[] pixels = Capture(eye, target, texture);
                        string png = Path.Combine(folder, "planted-overlap.png");
                        WritePng(png, pixels, texture);
                        written.Add(png);
                        List<Element> hud = CollectHud(presenter, eye);
                        hud.AddRange(Gather("planted", planted.Transform, eye));
                        List<Element> others = CollectNeckAndHand(neck, presenter.ShownShape ?? "G", null, eye);
                        List<string> overlaps = Overlaps(hud, others);
                        plantedOverlaps = 0;
                        for (int i = 0; i < overlaps.Count; i++)
                        {
                            if (overlaps[i].IndexOf("planted", StringComparison.Ordinal) >= 0 && overlaps[i].IndexOf("dot", StringComparison.Ordinal) >= 0) plantedOverlaps++;
                        }
                        AppendFrame(json, ref firstFrame, "planted-overlap", clock, presenter, false, hud, others, overlaps, Lit(pixels));
                    }
                }

                string sheet = Path.Combine(folder, songId + "-contact.png");
                WriteContactSheet(sheet, thumbs);
                written.Add(sheet);
                return null;
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        static string NextPending(List<Moment> moments)
        {
            for (int i = 0; i < moments.Count; i++)
            {
                if (!moments[i].Done) return moments[i].Name;
            }
            return "?";
        }

        /// <summary>The eight moments of a song (any order in time; each is captured the first frame it is true).</summary>
        static List<Moment> MomentsFor(DevSong song)
        {
            DevChordRun[] runs = song.Runs;
            int lyricLine = Math.Min(2, song.Lines.Length - 1);
            double lineStart = song.Lines[lyricLine].StartBeat;
            return new List<Moment>
            {
                new Moment { Name = "01-count-in", When = c => c.State == DevPlayState.CountIn && DevTimeline.CountInBeatsLeft(c.Beat, song.StartBeat) == 2 },
                new Moment { Name = "02-wait-first-chord", When = c => c.Waiting && c.RunIndex == 0 && c.WaitSeconds >= 0.3f },
                new Moment { Name = "03-mid-chord", When = c => !c.Waiting && c.RunIndex == 1 && c.Beat >= runs[1].StartBeat + 0.5 * runs[1].Beats },
                new Moment { Name = "04-one-beat-before-change", When = c => !c.Waiting && c.RunIndex == 2 && c.Frame.BeatsToChange <= 1.0 },
                new Moment { Name = "05-waiting", When = c => c.Waiting && c.RunIndex == 3 && c.WaitSeconds >= 0.3f },
                new Moment { Name = "06-confirmed", When = c => !c.Waiting && c.RunIndex == 3 && c.Frame.ChordConfirmed && c.Beat >= runs[3].StartBeat + 0.25 },
                new Moment { Name = "07-lyric-line-3", When = c => !c.Waiting && c.State == DevPlayState.Playing && c.Beat >= lineStart + 1.0 },
                new Moment { Name = "08-results", When = c => c.State == DevPlayState.Ended },
            };
        }

        // ------------------------------------------------------------------ camera and pixels

        static Camera MakeEye(Transform head, Scene scene, float ipdM)
        {
            var go = new GameObject("LeftEye");
            go.transform.SetParent(head, false);
            go.transform.localPosition = new Vector3(-0.5f * ipdM, 0f, 0f);
            Camera eye = go.AddComponent<Camera>();
            eye.enabled = false;
            eye.cameraType = CameraType.Preview;
            eye.scene = scene;
            eye.clearFlags = CameraClearFlags.SolidColor;
            eye.backgroundColor = new Color(0f, 0f, 0f, 1f);
            eye.nearClipPlane = 0.05f;
            eye.farClipPlane = 20f;
            eye.allowHDR = false;
            eye.allowMSAA = false;
            float h, v;
            NeckMath.FovFromDiagonal(DiagonalFovDeg, EyeWidth / (float)EyeHeight, out h, out v);
            eye.fieldOfView = v;
            eye.aspect = EyeWidth / (float)EyeHeight;
            return eye;
        }

        static Color32[] Capture(Camera eye, RenderTexture target, Texture2D texture)
        {
            RenderOnce(eye, target);   // fills the font texture, builds the text meshes
            RenderOnce(eye, target);   // the frame that is kept
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, EyeWidth, EyeHeight), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
            return pixels;
        }

        static void RenderOnce(Camera eye, RenderTexture target)
        {
            eye.targetTexture = target;
            eye.Render();
            eye.targetTexture = null;
        }

        static void WritePng(string path, Color32[] pixels, Texture2D texture)
        {
            texture.SetPixels32(pixels);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }

        static int Lit(Color32[] pixels)
        {
            int n = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.r > 24 || p.g > 24 || p.b > 24) n++;
            }
            return n;
        }

        static Color32[] Thumbnail(Color32[] pixels)
        {
            int w = EyeWidth / ThumbScale, h = EyeHeight / ThumbScale;
            var thumb = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < ThumbScale; dy++)
                    {
                        int row = (y * ThumbScale + dy) * EyeWidth + x * ThumbScale;
                        for (int dx = 0; dx < ThumbScale; dx++)
                        {
                            Color32 p = pixels[row + dx];
                            r += p.r; g += p.g; b += p.b;
                        }
                    }
                    int k = ThumbScale * ThumbScale;
                    thumb[y * w + x] = new Color32((byte)(r / k), (byte)(g / k), (byte)(b / k), 255);
                }
            }
            return thumb;
        }

        static void WriteContactSheet(string path, List<Color32[]> thumbs)
        {
            int w = EyeWidth / ThumbScale, h = EyeHeight / ThumbScale, columns = 4;
            int rows = Math.Max(1, (thumbs.Count + columns - 1) / columns);
            int sheetW = w * columns + (columns - 1) * 4, sheetH = h * rows + (rows - 1) * 4;
            var sheet = new Color32[sheetW * sheetH];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(60, 60, 60, 255);   // grey gutters between frames
            for (int i = 0; i < thumbs.Count; i++)
            {
                int cx = (i % columns) * (w + 4);
                int cy = (rows - 1 - i / columns) * (h + 4);   // first frame top left (rows are bottom-up in a texture)
                for (int y = 0; y < h; y++) Array.Copy(thumbs[i], y * w, sheet, (cy + y) * sheetW + cx, w);
            }
            var tex = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(sheet);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ------------------------------------------------------------------ what is where on the screen

        static List<Element> CollectHud(PlayAlongPresenter presenter, Camera eye)
        {
            var list = new List<Element>();
            NeckHudCard card = presenter.Card;
            Transform t = card.transform;
            list.AddRange(Gather("chord", t.Find("ChordPivot"), eye));
            list.AddRange(Gather("next", t.Find("NextWord"), eye));
            list.AddRange(Gather("next", t.Find("NextChord"), eye));
            list.AddRange(Gather("wait", t.Find("WaitCue"), eye));
            list.AddRange(Gather("beats", card.Beats.transform, eye));
            list.AddRange(Gather("pattern", card.Pattern.transform, eye));
            list.AddRange(Gather("lyrics", card.Lyrics.transform, eye));
            list.AddRange(Gather("countin", card.CountIn.transform, eye));
            list.AddRange(Gather("rings", presenter.Rings.transform, eye));
            list.AddRange(Gather("results", presenter.ResultsCard.transform, eye));
            return list;
        }

        static List<Element> CollectNeckAndHand(FloatingNeck neck, string chord, PosedGhostHand hand, Camera eye)
        {
            var list = new List<Element>();
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int n = chord != null ? DevNeckLayout.ChordShape(neck.Layout, chord, dots, marks) : 0;
            float radiusM = neck.Sizes.dotRadiusMm > 0f ? neck.Sizes.dotRadiusMm * 0.001f : 0.004f;
            Transform socket = neck.Socket;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = new Vector3(dots[i].xMm * 0.001f, 0f, dots[i].zMm * 0.001f);
                Rect r;
                if (ProjectBox(eye, socket, c, new Vector3(radiusM, 0.001f, radiusM), out r))
                    list.Add(new Element { Group = "dot", Name = "dot s" + dots[i].stringNumber + " f" + dots[i].finger, Rect = r });
            }
            if (hand != null) list.AddRange(Gather("hand", hand.transform, eye));
            return list;
        }

        /// <summary>Every visible renderer under <paramref name="root"/> as a screen rectangle of the group.</summary>
        static List<Element> Gather(string group, Transform root, Camera eye)
        {
            var list = new List<Element>();
            if (root == null || !root.gameObject.activeInHierarchy) return list;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                Bounds b = r.bounds;
                if (b.size.sqrMagnitude < 1e-12f) continue;
                Rect rect;
                if (!ProjectWorldBounds(eye, b, out rect)) continue;
                list.Add(new Element { Group = group, Name = group + "/" + r.gameObject.name, Rect = rect });
            }
            return list;
        }

        static bool ProjectWorldBounds(Camera eye, Bounds b, out Rect rect)
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector2 p;
                if (!ToPixels(eye, corner, out p))
                {
                    rect = default(Rect);
                    return false;
                }
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            rect = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        static bool ProjectBox(Camera eye, Transform frame, Vector3 centre, Vector3 half, out Rect rect)
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 local = centre + Vector3.Scale(half, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector2 p;
                if (!ToPixels(eye, frame.TransformPoint(local), out p))
                {
                    rect = default(Rect);
                    return false;
                }
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            rect = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        /// <summary>World point to eye pixels (0,0 = lower left), from the CPU-side matrices; false behind the eye.</summary>
        static bool ToPixels(Camera eye, Vector3 world, out Vector2 pixels)
        {
            Vector4 view = eye.worldToCameraMatrix * new Vector4(world.x, world.y, world.z, 1f);
            Vector4 clip = eye.projectionMatrix * view;
            if (clip.w <= 1e-6f)
            {
                pixels = default(Vector2);
                return false;
            }
            pixels = new Vector2((clip.x / clip.w * 0.5f + 0.5f) * EyeWidth, (clip.y / clip.w * 0.5f + 0.5f) * EyeHeight);
            return true;
        }

        /// <summary>
        /// The relational check: every HUD part against every part of another HUD group, against every fingertip dot and
        /// against the hand. Rings are meant to sit on the dots (that is what they are for), so rings-vs-dot is not reported.
        /// </summary>
        static List<string> Overlaps(List<Element> hud, List<Element> others)
        {
            var found = new List<string>();
            for (int i = 0; i < hud.Count; i++)
            {
                for (int j = i + 1; j < hud.Count; j++)
                {
                    if (hud[i].Group == hud[j].Group) continue;
                    float a = Area(hud[i].Rect, hud[j].Rect);
                    if (a >= MinOverlapPx) found.Add("hud-hud " + hud[i].Name + " x " + hud[j].Name + " " + F(a) + " px2");
                }
                for (int k = 0; k < others.Count; k++)
                {
                    if (hud[i].Group == "rings" && others[k].Group == "dot") continue;
                    float a = Area(hud[i].Rect, others[k].Rect);
                    if (a < MinOverlapPx) continue;
                    string kind = others[k].Group == "dot" ? "hud-dot " : "hud-hand ";
                    found.Add(kind + hud[i].Name + " x " + others[k].Name + " " + F(a) + " px2");
                }
            }
            return found;
        }

        static float Area(Rect a, Rect b)
        {
            float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            float h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        // ------------------------------------------------------------------ report

        static void AppendFrame(StringBuilder json, ref bool first, string name, DevPlayAlongClock clock, PlayAlongPresenter presenter,
            bool handOk, List<Element> hud, List<Element> others, List<string> overlaps, int lit)
        {
            if (!first) json.Append(",\n");
            first = false;
            PracticeFrame f = clock.Frame;
            DevPlayResults r = clock.Results;
            json.Append("    {\"name\": ").Append(Q(name))
                .Append(", \"state\": ").Append(Q(clock.State.ToString()))
                .Append(", \"active\": ").Append(f.Active ? "true" : "false")
                .Append(", \"beat\": ").Append(F((float)clock.Beat))
                .Append(", \"songSeconds\": ").Append(F((float)clock.SongSeconds))
                .Append(", \"tempoPreset\": ").Append(F(clock.TempoPreset))
                .Append(", \"beatsPerSecond\": ").Append(F(f.BeatsPerSecond))
                .Append(", \"chord\": ").Append(Q(f.Chord))
                .Append(", \"nextChord\": ").Append(Q(f.NextChord))
                .Append(", \"beatsToChange\": ").Append(double.IsInfinity(f.BeatsToChange) ? "null" : F((float)f.BeatsToChange))
                .Append(", \"waiting\": ").Append(f.Waiting ? "true" : "false")
                .Append(", \"chordConfirmed\": ").Append(f.ChordConfirmed ? "true" : "false")
                .Append(", \"slotsPerBeat\": ").Append(f.SlotsPerBeat)
                .Append(", \"cardChord\": ").Append(Q(presenter.Card.CurrentChord))
                .Append(", \"cardNext\": ").Append(Q(presenter.Card.NextChord))
                .Append(", \"countIn\": ").Append(presenter.Card.CountIn.BeatsLeft)
                .Append(", \"neckShape\": ").Append(Q(presenter.ShownShape))
                .Append(", \"neckDots\": ").Append(presenter.Neck.DotCount)
                .Append(", \"rings\": ").Append(presenter.Rings.ActiveChanges)
                .Append(", \"results\": ").Append(presenter.ShowingResults
                    ? "{\"hit\": " + r.ChordsHit + ", \"total\": " + r.ChordsTotal + ", \"meanTimingMs\": " + (float.IsNaN(r.MeanTimingMs) ? "null" : F(r.MeanTimingMs)) + ", \"simulated\": " + (r.Simulated ? "true" : "false") + "}"
                    : "null")
                .Append(", \"posedHand\": ").Append(handOk ? "true" : "false")
                .Append(", \"litPixels\": ").Append(lit)
                .Append(",\n     \"elements\": [");
            for (int i = 0; i < hud.Count; i++) AppendElement(json, hud[i], i == 0);
            json.Append("],\n     \"neckAndHand\": [");
            for (int i = 0; i < others.Count; i++) AppendElement(json, others[i], i == 0);
            json.Append("],\n     \"overlaps\": [");
            for (int i = 0; i < overlaps.Count; i++) json.Append(i == 0 ? "" : ", ").Append(Q(overlaps[i]));
            json.Append("]}");
        }

        static void AppendElement(StringBuilder json, Element e, bool first)
        {
            json.Append(first ? "" : ", ").Append("{\"name\": ").Append(Q(e.Name))
                .Append(", \"px\": [").Append(F(e.Rect.xMin)).Append(", ").Append(F(e.Rect.yMin)).Append(", ")
                .Append(F(e.Rect.xMax)).Append(", ").Append(F(e.Rect.yMax)).Append("]}");
        }

        static string F(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
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
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
#endif
