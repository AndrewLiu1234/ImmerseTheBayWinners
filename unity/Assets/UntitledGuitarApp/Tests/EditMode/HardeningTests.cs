// HardeningTests.cs: EditMode tests for the demo-hardening fixes (Sat 2026-11-14).
//   MonitorLayoutTests   the maths behind "UGA/Glasses Game View" on macOS (NSScreen lines -> editor screen space -> the glasses)
// No Play Mode, no device, no network, no sleeping.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UntitledGuitarApp.Runtime.Render;

namespace UntitledGuitarApp.Tests.EditMode
{
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
