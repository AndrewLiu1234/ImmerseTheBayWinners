// PlayHudOverlap: the RELATIONAL layout check behind the play HUD preview (Editor/HudPlayPreview.cs), kept here as pure
// managed code so it can be unit-tested offline with a planted overlap (Tests/EditMode/HudPlayTests.cs).
//
// Each part of the picture (one HUD text, one HUD shape, the neck's dots, a finger number...) is rendered ALONE and turned
// into a PixelMask of the pixels it lights. Two parts overlap when some pixel is lit by both: element against element, on
// the real ink, so a disc is judged by its round outline and a ring by its stroke (not by its bounding box), and text by
// its glyphs. Classify then says whether an overlap is a problem or happens by design (a glow behind its own glyph, an
// approach ring landing on its own target, the cursor crossing the beat ticks of its own lane).
//
// No engine calls: Color32 is a plain struct. Nothing here runs in the player.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>What an overlap between two parts of the picture means.</summary>
    public enum OverlapKind
    {
        /// <summary>Two parts that must not touch do: fix the layout.</summary>
        Problem = 0,
        /// <summary>The parts are meant to sit on each other (a glow behind its glyph, a ring on its own target...).</summary>
        ByDesign = 1,
        /// <summary>Allowed but worth a look (a soft glow over a neighbour, two shrinking rings crossing).</summary>
        Note = 2,
    }

    /// <summary>The pixels one part of a rendered frame lights, as a bit set with its bounding box. Pure; no engine calls.</summary>
    public sealed class PixelMask
    {
        readonly ulong[] _bits;
        readonly int _wordsPerRow;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Number of lit pixels.</summary>
        public int Count { get; private set; }

        /// <summary>Bounding box of the lit pixels (inclusive); MinX > MaxX when nothing is lit.</summary>
        public int MinX { get; private set; }
        public int MinY { get; private set; }
        public int MaxX { get; private set; }
        public int MaxY { get; private set; }

        public bool IsEmpty { get { return Count == 0; } }

        public PixelMask(int width, int height)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            _wordsPerRow = (Width + 63) / 64;
            _bits = new ulong[_wordsPerRow * Height];
            MinX = int.MaxValue;
            MinY = int.MaxValue;
            MaxX = -1;
            MaxY = -1;
        }

        /// <summary>Marks pixel (x, y) as lit (outside the frame is ignored).</summary>
        public void Set(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int word = y * _wordsPerRow + (x >> 6);
            ulong bit = 1UL << (x & 63);
            if ((_bits[word] & bit) != 0) return;
            _bits[word] |= bit;
            Count++;
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            return (_bits[y * _wordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        /// <summary>Fills a rectangle (inclusive corners); for tests and for planted cases.</summary>
        public void FillRect(int x0, int y0, int x1, int y1)
        {
            for (int y = Mathf.Max(0, y0); y <= Mathf.Min(Height - 1, y1); y++)
                for (int x = Mathf.Max(0, x0); x <= Mathf.Min(Width - 1, x1); x++)
                    Set(x, y);
        }

        /// <summary>
        /// The pixels of <paramref name="pixels"/> (row-major, row 0 at the bottom as Texture2D gives them) whose brightest
        /// channel is above <paramref name="threshold"/>. Black is what the additive glasses do not show, so "lit" is the
        /// test, not alpha.
        /// </summary>
        public static PixelMask FromPixels(Color32[] pixels, int width, int height, byte threshold)
        {
            var mask = new PixelMask(width, height);
            if (pixels == null) return mask;
            int rows = Mathf.Min(mask.Height, pixels.Length / mask.Width);
            for (int y = 0; y < rows; y++)
            {
                int row = y * mask.Width;
                for (int x = 0; x < mask.Width; x++)
                {
                    Color32 p = pixels[row + x];
                    if (p.r > threshold || p.g > threshold || p.b > threshold) mask.Set(x, y);
                }
            }
            return mask;
        }

        /// <summary>Pixels lit in both masks (0 when the sizes differ or the boxes do not meet). No allocation.</summary>
        public static int OverlapPixels(PixelMask a, PixelMask b)
        {
            if (a == null || b == null || a.IsEmpty || b.IsEmpty) return 0;
            if (a.Width != b.Width || a.Height != b.Height) return 0;
            int x0 = Mathf.Max(a.MinX, b.MinX), x1 = Mathf.Min(a.MaxX, b.MaxX);
            int y0 = Mathf.Max(a.MinY, b.MinY), y1 = Mathf.Min(a.MaxY, b.MaxY);
            if (x0 > x1 || y0 > y1) return 0;
            int w0 = x0 >> 6, w1 = x1 >> 6;
            int total = 0;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * a._wordsPerRow;
                for (int w = w0; w <= w1; w++)
                {
                    ulong both = a._bits[row + w] & b._bits[row + w];
                    if (both != 0) total += PopCount(both);
                }
            }
            return total;
        }

        /// <summary>Bounding box of the pixels lit in both masks; false when there are none.</summary>
        public static bool OverlapBox(PixelMask a, PixelMask b, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = int.MaxValue; minY = int.MaxValue; maxX = -1; maxY = -1;
            if (OverlapPixels(a, b) == 0) return false;
            int x0 = Mathf.Max(a.MinX, b.MinX), x1 = Mathf.Min(a.MaxX, b.MaxX);
            int y0 = Mathf.Max(a.MinY, b.MinY), y1 = Mathf.Min(a.MaxY, b.MaxY);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!a.Get(x, y) || !b.Get(x, y)) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX >= 0;
        }

        /// <summary>
        /// Smallest gap in pixels between the two bounding boxes (0 when they touch or overlap). A cheap "too close" measure:
        /// boxes are conservative, so a small gap means "look", never "fine".
        /// </summary>
        public static int BoxGap(PixelMask a, PixelMask b)
        {
            if (a == null || b == null || a.IsEmpty || b.IsEmpty) return int.MaxValue;
            int dx = Mathf.Max(0, Mathf.Max(a.MinX - b.MaxX, b.MinX - a.MaxX) - 1);
            int dy = Mathf.Max(0, Mathf.Max(a.MinY - b.MaxY, b.MinY - a.MaxY) - 1);
            return Mathf.Max(dx, dy);
        }

        /// <summary>True when the part reaches the edge of the frame (it is probably cut off there).</summary>
        public bool TouchesEdge
        {
            get { return !IsEmpty && (MinX <= 0 || MinY <= 0 || MaxX >= Width - 1 || MaxY >= Height - 1); }
        }

        static int PopCount(ulong v)
        {
            int n = 0;
            while (v != 0)
            {
                v &= v - 1;
                n++;
            }
            return n;
        }
    }

    /// <summary>Rules for which overlaps between parts of the play HUD are problems. Pure.</summary>
    public static class PlayHudOverlap
    {
        /// <summary>Fewer shared pixels than this are anti-aliasing, not an overlap.</summary>
        public const int MinOverlapPixels = 4;

        public const string CardGroup = "card";
        public const string RingsGroup = "rings";
        public const string ResultsGroup = "results";
        public const string NeckGroup = "neck";

        /// <summary>An id for a part: "group:path", the path being the GameObject names below the group's root ("PatternStrip/Slot3/Halo").</summary>
        public static string Id(string group, string path)
        {
            return (group ?? string.Empty) + ":" + (path ?? string.Empty);
        }

        /// <summary>
        /// Whether two parts are compared at all: every HUD part against every other HUD part and against the neck; the neck
        /// against itself is not this check's business.
        /// </summary>
        public static bool ShouldCompare(string idA, string idB)
        {
            return !(Group(idA) == NeckGroup && Group(idB) == NeckGroup);
        }

        /// <summary>What an overlap between parts <paramref name="idA"/> and <paramref name="idB"/> means, and why.</summary>
        public static OverlapKind Classify(string idA, string idB, out string why)
        {
            string ga = Group(idA), gb = Group(idB);
            string pa = PathOf(idA), pb = PathOf(idB);
            string la = Leaf(pa), lb = Leaf(pb);

            // Approach rings live on the fretboard: they frame fingertips and cross strings by design.
            if ((ga == RingsGroup && gb == NeckGroup) || (gb == RingsGroup && ga == NeckGroup))
            {
                why = "approach rings lie on the fretboard around the fingertips";
                return OverlapKind.ByDesign;
            }
            if (ga == RingsGroup && gb == RingsGroup)
            {
                if (RingSuffix(la) != null && RingSuffix(la) == RingSuffix(lb))
                {
                    why = "a ring shrinks onto its own target";
                    return OverlapKind.ByDesign;
                }
                why = "rings of neighbouring fingertips cross while they shrink";
                return OverlapKind.Note;
            }

            if (ga == CardGroup && gb == CardGroup)
            {
                string ca = PatternCell(pa), cb = PatternCell(pb);
                if (ca != null && ca == cb && (la == "Halo" || lb == "Halo"))
                {
                    why = "the glow sits behind its own cell";
                    return OverlapKind.ByDesign;
                }
                if (IsPatternCursor(pa) && (IsPatternCursor(pb) || IsPatternTick(pb)) ||
                    IsPatternCursor(pb) && IsPatternTick(pa))
                {
                    why = "the cursor runs along its lane over the beat ticks";
                    return OverlapKind.ByDesign;
                }
                if (ca != null && cb != null && (la == "Halo" || lb == "Halo"))
                {
                    why = "a soft glow reaches over a neighbouring cell";
                    return OverlapKind.Note;
                }
                if (IsPair(pa, pb, "WaitCue/PauseLeft", "WaitCue/PauseRight"))
                {
                    why = "the two bars of the pause mark";
                    return OverlapKind.Note;
                }
            }

            why = "these parts must not touch";
            return OverlapKind.Problem;
        }

        public static string Group(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            int colon = id.IndexOf(':');
            return colon < 0 ? string.Empty : id.Substring(0, colon);
        }

        public static string PathOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            int colon = id.IndexOf(':');
            return colon < 0 ? id : id.Substring(colon + 1);
        }

        static string Leaf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        // "PatternStrip/Slot3" for anything inside cell 3 of the pattern strip, else null.
        static string PatternCell(string path)
        {
            const string prefix = "PatternStrip/Slot";
            if (!path.StartsWith(prefix, System.StringComparison.Ordinal)) return null;
            int slash = path.IndexOf('/', prefix.Length);
            return slash < 0 ? path : path.Substring(0, slash);
        }

        static bool IsPatternCursor(string path)
        {
            return path == "PatternStrip/Cursor" || path == "PatternStrip/CursorWrap";
        }

        static bool IsPatternTick(string path)
        {
            return path.StartsWith("PatternStrip/Beat", System.StringComparison.Ordinal);
        }

        // "0_3" for "Ring0_3" or "Target0_3" (change 0, fingertip 3), else null.
        static string RingSuffix(string leaf)
        {
            if (leaf.StartsWith("Ring", System.StringComparison.Ordinal)) return leaf.Substring(4);
            if (leaf.StartsWith("Target", System.StringComparison.Ordinal)) return leaf.Substring(6);
            return null;
        }

        static bool IsPair(string a, string b, string first, string second)
        {
            return (a == first && b == second) || (a == second && b == first);
        }
    }
}
