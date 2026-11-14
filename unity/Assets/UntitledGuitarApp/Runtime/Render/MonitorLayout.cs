using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UntitledGuitarApp.Runtime.Render
{
    /// <summary>One monitor as macOS reports it: position and size in points (not pixels).</summary>
    public struct MonitorFrame
    {
        /// <summary>Index in NSScreen.screens. 0 = the primary screen (it has the menu bar and its origin is (0, 0)).</summary>
        public int Index;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        /// <summary>NSScreen.backingScaleFactor (2 on a Retina panel, 1 on the glasses); 0 when unknown.</summary>
        public double Scale;
        /// <summary>NSScreen.localizedName, "" when unknown.</summary>
        public string Name;

        public MonitorFrame(int index, double x, double y, double width, double height, double scale, string name)
        {
            Index = index; X = x; Y = y; Width = width; Height = height; Scale = scale; Name = name ?? "";
        }

        public bool IsPrimary { get { return Index == 0; } }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0} \"{1}\" x={2} y={3} {4} x {5} pt (scale {6}){7}",
                Index, Name, X, Y, Width, Height, Scale, IsPrimary ? " [primary]" : "");
        }
    }

    /// <summary>
    /// The maths behind the editor menu "UGA/Glasses Game View" (Editor/GlassesGameView.cs): plain C#, no Unity calls, so the
    /// EditMode tests cover it. Unity's own monitor query (InternalEditorUtility.GetBoundsOfDesktopAtPoint) throws
    /// "Not implemented on OSX", and Display.displays has length 1 in the Editor, so on macOS the editor asks AppKit instead:
    /// it runs <see cref="NsScreenScript"/> with <c>/usr/bin/osascript -l JavaScript</c> (script on standard input) and parses the
    /// lines with <see cref="ParseNsScreenLines"/>. Sources: docs/apis/unity/editorwindow-popup.md section 5.
    ///
    /// Coordinates: NSScreen.frame is in Cocoa screen space (points, origin at the bottom-left corner of the primary screen,
    /// y grows UP). EditorWindow.position is in the editor's screen space, where y grows DOWN (csref PopupLocationHelper puts
    /// "below" at a larger y). <see cref="ToEditorSpace"/> flips y against the primary screen's height, assuming the editor's
    /// origin is the primary screen's top-left corner (the usual macOS convention; not documented by Unity, see the doc).
    /// </summary>
    public static class MonitorLayout
    {
        /// <summary>First field of every line <see cref="NsScreenScript"/> prints.</summary>
        public const string LinePrefix = "UGA_SCREEN";

        /// <summary>
        /// JavaScript for Automation, run as <c>/usr/bin/osascript -l JavaScript</c> with this text on standard input. Prints one
        /// tab-separated line per NSScreen: UGA_SCREEN, index, frame x, y, width, height (points), backingScaleFactor, name.
        /// Checked on this Mac (macOS 26.5.1): "UGA_SCREEN\t0\t0\t0\t1512\t982\t2\tBuilt-in Retina Display" in 0.08 s.
        /// </summary>
        public const string NsScreenScript =
            "ObjC.import(\"AppKit\");\n" +
            "var s = $.NSScreen.screens;\n" +
            "var out = [];\n" +
            "for (var i = 0; i < s.count; i++) {\n" +
            "  var sc = s.objectAtIndex(i);\n" +
            "  var f = sc.frame;\n" +
            "  var name = \"\";\n" +
            "  try { name = ObjC.unwrap(sc.localizedName); } catch (e) { name = \"\"; }\n" +
            "  out.push([\"UGA_SCREEN\", i, f.origin.x, f.origin.y, f.size.width, f.size.height, sc.backingScaleFactor, name].join(\"\\t\"));\n" +
            "}\n" +
            "out.join(\"\\n\");\n";

        /// <summary>Width tolerance (points) when matching the glasses' 3840-wide side-by-side mode.</summary>
        public const double WidthTolerance = 2.0;
        /// <summary>Height range accepted for the glasses (1080 plus or minus a menu bar's worth).</summary>
        public const double MinHeight = 1000.0, MaxHeight = 1202.0;

        /// <summary>
        /// Parses the output of <see cref="NsScreenScript"/>. Lines that do not start with <see cref="LinePrefix"/> or do not
        /// carry six numbers are skipped (osascript may add other text). Numbers use the invariant culture ("1512.5"). Never throws.
        /// </summary>
        public static List<MonitorFrame> ParseNsScreenLines(string output)
        {
            List<MonitorFrame> result = new List<MonitorFrame>();
            if (string.IsNullOrEmpty(output)) return result;
            string[] lines = output.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (!line.StartsWith(LinePrefix + "\t", StringComparison.Ordinal)) continue;
                string[] f = line.Split('\t');
                if (f.Length < 7) continue;
                int index;
                double x, y, w, h, scale;
                if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) continue;
                if (!Num(f[2], out x) || !Num(f[3], out y) || !Num(f[4], out w) || !Num(f[5], out h)) continue;
                if (!Num(f[6], out scale)) scale = 0;
                if (!(w > 0) || !(h > 0)) continue;
                string name = f.Length >= 8 ? string.Join("\t", f, 7, f.Length - 7) : "";
                result.Add(new MonitorFrame(index, x, y, w, h, scale, name));
            }
            return result;
        }

        /// <summary>
        /// Converts Cocoa frames (y up from the primary screen's bottom edge) to editor screen space (y down from the primary
        /// screen's top edge): y' = primaryHeight - (y + height); x is unchanged. The primary screen is the one with Index 0;
        /// without it the list cannot be converted and an empty list is returned.
        /// </summary>
        public static List<MonitorFrame> ToEditorSpace(IList<MonitorFrame> cocoa)
        {
            List<MonitorFrame> result = new List<MonitorFrame>();
            if (cocoa == null) return result;
            double primaryHeight = -1;
            for (int i = 0; i < cocoa.Count; i++)
                if (cocoa[i].IsPrimary) { primaryHeight = cocoa[i].Height; break; }
            if (!(primaryHeight > 0)) return result;
            for (int i = 0; i < cocoa.Count; i++)
            {
                MonitorFrame m = cocoa[i];
                m.Y = primaryHeight - (m.Y + m.Height);
                result.Add(m);
            }
            return result;
        }

        /// <summary>The glasses in side-by-side 3D mode: 3840 wide (within 2 points) and about 1080 high.</summary>
        public static bool LooksLikeGlasses(MonitorFrame m, double width)
        {
            return Math.Abs(m.Width - width) <= WidthTolerance && m.Height >= MinHeight && m.Height <= MaxHeight;
        }

        /// <summary>
        /// Index into <paramref name="monitors"/> of the glasses' monitor, or -1. Among the monitors that look like the glasses,
        /// a non-primary one wins over the primary one (the Mac's own screen is never the glasses), then the one nearest to
        /// <paramref name="width"/> x <paramref name="height"/>, then the lowest index.
        /// </summary>
        public static int ChooseGlasses(IList<MonitorFrame> monitors, double width, double height)
        {
            if (monitors == null) return -1;
            int best = -1;
            for (int i = 0; i < monitors.Count; i++)
            {
                MonitorFrame m = monitors[i];
                if (!LooksLikeGlasses(m, width)) continue;
                if (best < 0) { best = i; continue; }
                MonitorFrame b = monitors[best];
                if (b.IsPrimary != m.IsPrimary) { if (b.IsPrimary) best = i; continue; }
                double dm = Math.Abs(m.Height - height) + Math.Abs(m.Width - width);
                double db = Math.Abs(b.Height - height) + Math.Abs(b.Width - width);
                if (dm < db) best = i;
            }
            return best;
        }

        /// <summary>
        /// The window rectangle for the glasses' Game view on <paramref name="monitor"/> (editor space): <paramref name="width"/>
        /// x <paramref name="height"/> at its top-left corner, or bottom-aligned when the monitor is reported shorter.
        /// </summary>
        public static void WindowRect(MonitorFrame monitor, double width, double height, out double x, out double y)
        {
            x = monitor.X;
            y = monitor.Height >= height ? monitor.Y : monitor.Y + monitor.Height - height;
        }

        /// <summary>One line per monitor, for the log.</summary>
        public static string Describe(IList<MonitorFrame> monitors)
        {
            StringBuilder sb = new StringBuilder();
            int n = monitors == null ? 0 : monitors.Count;
            sb.Append(n).Append(" monitor(s)");
            for (int i = 0; i < n; i++) sb.Append("\n    ").Append(monitors[i].ToString());
            return sb.ToString();
        }

        static readonly Regex NumberToken = new Regex(@"[-+]?(?:\d+\.?\d*|\.\d+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads a rectangle typed by hand: exactly four numbers x, y, width, height separated by anything ("1512 0 3840 1080",
        /// "1512, 0, 3840, 1080", or Unity's own "(x:1512.00, y:0.00, width:3840.00, height:1080.00)"). Width and height must be
        /// positive. Never throws.
        /// </summary>
        public static bool TryParseRect(string text, out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 200) return false;
            MatchCollection m = NumberToken.Matches(text);
            if (m.Count != 4) return false;
            double[] v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!Num(m[i].Value, out v[i])) return false;
            if (!(v[2] > 0) || !(v[3] > 0)) return false;
            x = v[0]; y = v[1]; width = v[2]; height = v[3];
            return true;
        }

        static bool Num(string s, out double v)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v))
                return true;
            v = 0;
            return false;
        }
    }
}
