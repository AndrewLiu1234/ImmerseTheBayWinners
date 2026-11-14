// HardeningTests.cs: EditMode tests for the demo-hardening fixes (Sat 2026-11-14).
//   MonitorLayoutTests   the maths behind "UGA/Glasses Game View" on macOS (NSScreen lines -> editor screen space -> the glasses)
//   ReplayFixtureTests   the shipped WAV-replay file (header, length, level) and every wavReplayFile the flag files name exists
// No Play Mode, no device, no network, no sleeping.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UntitledGuitarApp.Runtime.Audio;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Tests.EditMode
{
    internal static class HardeningPaths
    {
        /// <summary>
        /// unity/Assets. Found from this source file's own path first (absolute when compiled offline; "Assets/..." relative to
        /// the project folder, Unity's working directory, when Unity compiles it), else from Application.dataPath (Editor only).
        /// </summary>
        public static string AssetsDir([CallerFilePath] string thisFile = "")
        {
            try
            {
                if (!string.IsNullOrEmpty(thisFile))
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(thisFile));          // .../Assets/UntitledGuitarApp/Tests/EditMode
                    string assets = Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
                    if (Directory.Exists(Path.Combine(assets, "StreamingAssets"))) return assets;
                }
            }
            catch (Exception) { }
            return EngineAssetsDir();
        }

        // Separate and not inlined, so the offline runner's JIT never meets this engine call unless the path above failed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string EngineAssetsDir() { return UnityEngine.Application.dataPath; }

        public static string StreamingAssets { get { return Path.Combine(AssetsDir(), "StreamingAssets"); } }

        /// <summary>The repository root (unity/Assets is two folders down).</summary>
        public static string RepoRoot { get { return Path.GetFullPath(Path.Combine(AssetsDir(), "..", "..")); } }
    }

    [TestFixture]
    public class ReplayFixtureTests
    {
        const string ShippedFile = "fixtures-demo/learn-g.wav";

        static readonly Regex WavReplayFileField = new Regex("\"wavReplayFile\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);

        /// <summary>The flag files that ship or can be switched to: StreamingAssets/flags.default.json and tools/flags/*.json.</summary>
        static List<string> FlagFiles()
        {
            List<string> files = new List<string>();
            files.Add(Path.Combine(HardeningPaths.StreamingAssets, "flags.default.json"));
            string profiles = Path.Combine(Path.Combine(HardeningPaths.RepoRoot, "tools"), "flags");
            Assert.IsTrue(Directory.Exists(profiles), "expected the flag profiles in " + profiles);
            foreach (string f in Directory.GetFiles(profiles, "*.json"))
            {
                if (Path.GetFileName(f).StartsWith("secrets", StringComparison.OrdinalIgnoreCase)) continue;   // never read secrets
                files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>
        /// Every problem with the replay files the given flag files name (read as TEXT, independent of the flag loader): a name
        /// that is missing under <paramref name="streamingAssets"/> or does not decode. <paramref name="named"/> counts the names.
        /// </summary>
        static List<string> ReplayProblems(IEnumerable<string> flagFiles, string streamingAssets, out int named)
        {
            List<string> problems = new List<string>();
            named = 0;
            foreach (string file in flagFiles)
            {
                if (!File.Exists(file)) { problems.Add(file + ": flag file missing"); continue; }
                string json = File.ReadAllText(file);
                foreach (Match m in WavReplayFileField.Matches(json))
                {
                    named++;
                    string rel = m.Groups[1].Value;
                    if (rel.Length == 0) { problems.Add(file + ": wavReplayFile is empty"); continue; }
                    string path = Path.Combine(streamingAssets, rel);
                    if (!File.Exists(path)) { problems.Add(file + ": names \"" + rel + "\", which is not in " + streamingAssets); continue; }
                    WavData d; string err;
                    if (!WavReader.TryReadFile(path, out d, out err)) problems.Add(file + ": \"" + rel + "\" does not decode: " + err);
                }
            }
            return problems;
        }

        [Test]
        public void EveryWavReplayFileNamedByTheFlagFiles_ExistsInStreamingAssets_AndDecodes()
        {
            List<string> files = FlagFiles();
            Assert.GreaterOrEqual(files.Count, 5, "flags.default.json + the four profiles");
            int named;
            List<string> problems = ReplayProblems(files, HardeningPaths.StreamingAssets, out named);
            Assert.GreaterOrEqual(named, 5, "every flag file names a replay file (otherwise this check proves nothing)");
            Assert.IsEmpty(problems, string.Join("\n", problems.ToArray()));
        }

        // The checker must be seen to fire: a flag file naming a file that does not exist, and one that is not a WAV.
        [Test]
        public void TheReplayFileCheck_ReportsAMissingAndABrokenFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uga-replay-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "fixtures-demo"));
                File.WriteAllText(Path.Combine(Path.Combine(dir, "fixtures-demo"), "broken.wav"), "not a wav");
                string flags = Path.Combine(dir, "flags.json");
                File.WriteAllText(flags, "{ \"audioSource\": \"wav\", \"wavReplayFile\": \"fixtures-demo/missing.wav\" }\n"
                    + "{ \"wavReplayFile\" : \"fixtures-demo/broken.wav\" }");
                int named;
                List<string> problems = ReplayProblems(new[] { flags }, dir, out named);
                Assert.AreEqual(2, named);
                Assert.AreEqual(2, problems.Count, string.Join("\n", problems.ToArray()));
                StringAssert.Contains("missing.wav", problems[0]);
                StringAssert.Contains("does not decode", problems[1]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public void TheShippedReplayFile_Is48kMono16BitPcm_TenSeconds_AtAGentleLevel()
        {
            string path = Path.Combine(HardeningPaths.StreamingAssets, ShippedFile);
            Assert.IsTrue(File.Exists(path), path + " must ship (art/audio/make_learn_g.py writes it)");
            byte[] b = File.ReadAllBytes(path);
            // Header as art/audio/make_learn_g.py writes it (Python's wave module: RIFF, fmt (16 bytes), data), read by hand.
            Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
            Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(b, 8, 4));
            Assert.AreEqual("fmt ", System.Text.Encoding.ASCII.GetString(b, 12, 4));
            Assert.AreEqual(16, BitConverter.ToInt32(b, 16), "plain PCM fmt chunk");
            Assert.AreEqual(1, BitConverter.ToInt16(b, 20), "format 1 = integer PCM");
            Assert.AreEqual(1, BitConverter.ToInt16(b, 22), "mono");
            Assert.AreEqual(48000, BitConverter.ToInt32(b, 24), "48 kHz");
            Assert.AreEqual(96000, BitConverter.ToInt32(b, 28), "byte rate");
            Assert.AreEqual(2, BitConverter.ToInt16(b, 32), "block align");
            Assert.AreEqual(16, BitConverter.ToInt16(b, 34), "16-bit");
            Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(b, 36, 4));
            Assert.AreEqual(b.Length - 44, BitConverter.ToInt32(b, 40), "data size = the rest of the file");

            WavData d; string err;
            Assert.IsTrue(WavReader.TryReadFile(path, out d, out err), err);
            Assert.AreEqual(48000, d.SampleRate);
            Assert.AreEqual(1, d.SourceChannels);
            Assert.AreEqual(16, d.SourceBitsPerSample);
            Assert.AreEqual(10.0, d.DurationSec, 0.5, "about 10 s");

            double sum = 0, peak = 0;
            foreach (float v in d.Samples) { sum += v * (double)v; if (Math.Abs(v) > peak) peak = Math.Abs(v); }
            double rmsDb = 10.0 * Math.Log10(sum / d.Samples.Length);
            Assert.That(peak, Is.InRange(0.1, 0.5), "gentle: peak between -20 and -6 dBFS, never clipped");
            Assert.That(rmsDb, Is.InRange(-40.0, -20.0), "RMS over the whole file");

            // A strum every ~2 s: no 2-second window is (nearly) silent after the first strum at 0.3 s.
            int win = 2 * d.SampleRate;
            for (int start = (int)(0.3 * d.SampleRate); start + win <= d.Samples.Length; start += win)
            {
                double s = 0;
                for (int i = start; i < start + win; i++) s += d.Samples[i] * (double)d.Samples[i];
                double db = 10.0 * Math.Log10(s / win + 1e-20);
                Assert.Greater(db, -45.0, "window starting at " + (start / (double)d.SampleRate) + " s is silent");
            }
        }
    }
    [TestFixture]
    public class MonitorLayoutTests
    {
        // Real output of MonitorLayout.NsScreenScript on the Mac this was written on (glasses unplugged), copied from the terminal.
        const string RealMacLine = "UGA_SCREEN\t0\t0\t0\t1512\t982\t2\tBuilt-in Retina Display";

        static string Lines(params string[] lines) { return string.Join("\n", lines) + "\n"; }

        [Test]
        public void Parse_TheRealMacOutput()
        {
            List<MonitorFrame> m = MonitorLayout.ParseNsScreenLines(RealMacLine + "\n");
            Assert.AreEqual(1, m.Count);
            Assert.AreEqual(0, m[0].Index);
            Assert.IsTrue(m[0].IsPrimary);
            Assert.AreEqual(0.0, m[0].X);
            Assert.AreEqual(0.0, m[0].Y);
            Assert.AreEqual(1512.0, m[0].Width);
            Assert.AreEqual(982.0, m[0].Height);
            Assert.AreEqual(2.0, m[0].Scale);
            Assert.AreEqual("Built-in Retina Display", m[0].Name);
        }

        [Test]
        public void Parse_SkipsNoiseAndBrokenLines_AndNeverThrows()
        {
            string text = Lines("some warning from osascript", RealMacLine, "UGA_SCREEN\t1\tx\t0\t3840\t1080\t1\tbad x",
                "UGA_SCREEN\t2\t0\t0\t0\t1080\t1\tzero width", "UGA_SCREEN\t3\t1", "UGA_SCREENX\t4\t0\t0\t10\t10\t1\tprefix glued",
                "UGA_SCREEN\t5\t1512.5\t-98\t3840\t1080\t1\tXREAL Air 2 Ultra\r");
            List<MonitorFrame> m = MonitorLayout.ParseNsScreenLines(text);
            Assert.AreEqual(2, m.Count);
            Assert.AreEqual(5, m[1].Index);
            Assert.AreEqual(1512.5, m[1].X, "invariant-culture decimals");
            Assert.AreEqual("XREAL Air 2 Ultra", m[1].Name, "a trailing CR is not part of the name");
            Assert.AreEqual(0, MonitorLayout.ParseNsScreenLines(null).Count);
            Assert.AreEqual(0, MonitorLayout.ParseNsScreenLines("").Count);
        }

        [Test]
        public void ToEditorSpace_FlipsYAgainstThePrimaryScreen()
        {
            // Cocoa: origin at the primary screen's bottom-left, y up. Glasses to the right, tops aligned: y = 982 - 1080 = -98.
            List<MonitorFrame> cocoa = MonitorLayout.ParseNsScreenLines(Lines(RealMacLine,
                "UGA_SCREEN\t1\t1512\t-98\t3840\t1080\t1\tright, tops aligned",
                "UGA_SCREEN\t2\t0\t982\t3840\t1080\t1\tabove",
                "UGA_SCREEN\t3\t-3840\t0\t3840\t1080\t1\tleft, bottoms aligned"));
            List<MonitorFrame> e = MonitorLayout.ToEditorSpace(cocoa);
            Assert.AreEqual(4, e.Count);
            Assert.AreEqual(0.0, e[0].Y, "the primary screen's top is y = 0");
            Assert.AreEqual(1512.0, e[1].X);
            Assert.AreEqual(0.0, e[1].Y, "tops aligned with the primary screen: same top");
            Assert.AreEqual(-1080.0, e[2].Y, "above the primary screen: negative y (y grows down)");
            Assert.AreEqual(-98.0, e[3].Y, "bottoms aligned: its top is 98 points above the primary screen's top");
            Assert.AreEqual(-3840.0, e[3].X, "x is unchanged");
            Assert.AreEqual(1080.0, e[3].Height);
        }

        [Test]
        public void ToEditorSpace_WithoutAPrimaryScreen_GivesNothing()
        {
            List<MonitorFrame> cocoa = MonitorLayout.ParseNsScreenLines(Lines("UGA_SCREEN\t1\t1512\t-98\t3840\t1080\t1\tno primary"));
            Assert.AreEqual(1, cocoa.Count);
            Assert.AreEqual(0, MonitorLayout.ToEditorSpace(cocoa).Count);
            Assert.AreEqual(0, MonitorLayout.ToEditorSpace(null).Count);
        }

        [Test]
        public void ChooseGlasses_PrefersTheNonPrimary3840x1080Monitor()
        {
            var list = new List<MonitorFrame>
            {
                new MonitorFrame(0, 0, 0, 3840, 1080, 1, "a 3840 x 1080 primary screen"),
                new MonitorFrame(1, 0, -1080, 2560, 1440, 1, "a desk monitor"),
                new MonitorFrame(2, 3840, 0, 3840, 1080, 1, "XREAL"),
            };
            Assert.AreEqual(2, MonitorLayout.ChooseGlasses(list, 3840, 1080));
        }

        [Test]
        public void ChooseGlasses_TheGlassesIn2DModeOrUnplugged_IsNotFound()
        {
            var twoD = new List<MonitorFrame>
            {
                new MonitorFrame(0, 0, 0, 1512, 982, 2, "Built-in Retina Display"),
                new MonitorFrame(1, 1512, 0, 1920, 1080, 1, "XREAL in 2D mode"),
            };
            Assert.AreEqual(-1, MonitorLayout.ChooseGlasses(twoD, 3840, 1080));
            Assert.AreEqual(-1, MonitorLayout.ChooseGlasses(new List<MonitorFrame> { twoD[0] }, 3840, 1080));
            Assert.AreEqual(-1, MonitorLayout.ChooseGlasses(null, 3840, 1080));
        }

        [Test]
        public void ChooseGlasses_ToleratesAMenuBarsWorthOfHeight_AndTwoPointsOfWidth()
        {
            var list = new List<MonitorFrame>
            {
                new MonitorFrame(0, 0, 0, 1512, 982, 2, "Mac"),
                new MonitorFrame(1, 1512, 25, 3841, 1055, 1, "work area"),
            };
            Assert.AreEqual(1, MonitorLayout.ChooseGlasses(list, 3840, 1080));
            list[1] = new MonitorFrame(1, 1512, 0, 3843, 1080, 1, "3 points too wide");
            Assert.AreEqual(-1, MonitorLayout.ChooseGlasses(list, 3840, 1080));
            list[1] = new MonitorFrame(1, 1512, 0, 3840, 999, 1, "too short");
            Assert.AreEqual(-1, MonitorLayout.ChooseGlasses(list, 3840, 1080));
        }

        [Test]
        public void ChooseGlasses_TwoCandidates_TheNearestSizeWins()
        {
            var list = new List<MonitorFrame>
            {
                new MonitorFrame(0, 0, 0, 1512, 982, 2, "Mac"),
                new MonitorFrame(1, 1512, 0, 3840, 1150, 1, "taller"),
                new MonitorFrame(2, 5352, 0, 3840, 1080, 1, "exact"),
            };
            Assert.AreEqual(2, MonitorLayout.ChooseGlasses(list, 3840, 1080));
        }

        [Test]
        public void WindowRect_TopLeft_OrBottomAlignedWhenTheMonitorIsShort()
        {
            double x, y;
            MonitorLayout.WindowRect(new MonitorFrame(1, 1512, 0, 3840, 1080, 1, ""), 3840, 1080, out x, out y);
            Assert.AreEqual(1512.0, x);
            Assert.AreEqual(0.0, y);
            MonitorLayout.WindowRect(new MonitorFrame(1, 1512, 25, 3840, 1055, 1, ""), 3840, 1080, out x, out y);
            Assert.AreEqual(0.0, y, "a work area 25 points short: the window's bottom meets the monitor's bottom");
        }

        [Test]
        public void TryParseRect_ReadsWhatTheLogPrints_AndWhatAPersonTypes()
        {
            double x, y, w, h;
            Assert.IsTrue(MonitorLayout.TryParseRect("1512 0 3840 1080", out x, out y, out w, out h));
            Assert.AreEqual(new[] { 1512.0, 0.0, 3840.0, 1080.0 }, new[] { x, y, w, h });
            Assert.IsTrue(MonitorLayout.TryParseRect("(x:1512.00, y:-98.00, width:3840.00, height:1080.00)", out x, out y, out w, out h),
                "Unity's Rect.ToString() text");
            Assert.AreEqual(new[] { 1512.0, -98.0, 3840.0, 1080.0 }, new[] { x, y, w, h });
            Assert.IsTrue(MonitorLayout.TryParseRect(" -3840,  -98 , 3840 ,1080\n", out x, out y, out w, out h));
            Assert.AreEqual(-3840.0, x);
            Assert.IsFalse(MonitorLayout.TryParseRect("1512 0 3840", out x, out y, out w, out h), "three numbers");
            Assert.IsFalse(MonitorLayout.TryParseRect("1 2 3 4 5", out x, out y, out w, out h), "five numbers");
            Assert.IsFalse(MonitorLayout.TryParseRect("1512 0 0 1080", out x, out y, out w, out h), "zero width");
            Assert.IsFalse(MonitorLayout.TryParseRect("1512 0 3840 -1080", out x, out y, out w, out h), "negative height");
            Assert.IsFalse(MonitorLayout.TryParseRect(null, out x, out y, out w, out h));
            Assert.IsFalse(MonitorLayout.TryParseRect("hello", out x, out y, out w, out h));
        }

        [Test]
        public void Describe_ListsEveryMonitor()
        {
            var list = MonitorLayout.ToEditorSpace(MonitorLayout.ParseNsScreenLines(Lines(RealMacLine, "UGA_SCREEN\t1\t1512\t-98\t3840\t1080\t1\tXREAL")));
            string d = MonitorLayout.Describe(list);
            StringAssert.Contains("2 monitor(s)", d);
            StringAssert.Contains("Built-in Retina Display", d);
            StringAssert.Contains("x=1512 y=0 3840 x 1080", d);
        }

        // The editor runs exactly this script; run it once on the real OS (Observation: a binding that compiles can still throw
        // "Not implemented on OSX" at its first call). Ignored off macOS; a 5 s cap, no sleeping.
        [Test]
        public void NsScreenScript_RunsOnThisMac_AndReportsAPrimaryScreen()
        {
            if (!File.Exists("/usr/bin/osascript")) Assert.Ignore("macOS only: /usr/bin/osascript is not here");
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript", "-l JavaScript")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardInput.Write(MonitorLayout.NsScreenScript);
                p.StandardInput.Close();
                bool exited = p.WaitForExit(5000);
                if (!exited) { try { p.Kill(); } catch (Exception) { } }
                Assert.IsTrue(exited, "osascript answered within 5 s");
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                Assert.AreEqual(0, p.ExitCode, "osascript failed: " + stderr);
                List<MonitorFrame> editor = MonitorLayout.ToEditorSpace(MonitorLayout.ParseNsScreenLines(stdout));
                Assert.GreaterOrEqual(editor.Count, 1, "no screen in: " + stdout + stderr);
                Assert.IsTrue(editor[0].IsPrimary);
                Assert.AreEqual(0.0, editor[0].X, "the primary screen's origin is (0, 0)");
                Assert.AreEqual(0.0, editor[0].Y);
                Assert.Greater(editor[0].Width, 0.0);
                Assert.Greater(editor[0].Height, 0.0);
            }
        }
    }
}
