// NeckMeshBuilder: turns a NeckLayout, the fingertip dots and the string marks into quads (NeckGeometry). Pure code: plain
// arithmetic on the data it is given, no scene access, no allocation. FloatingNeck uploads the result to two meshes:
//   lines  strings, frets, nut, fretboard edges                  (changes only when the layout or the mode changes)
//   dots   fingertip dots, leaders to the finger numbers, X / O  (changes when the chord changes)
// Everything lies flat on the neck: lines on the fretboard surface or at string height, dots and marks at string height.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>What the builder needs to know besides the layout and the sizes.</summary>
    public struct NeckBuildOptions
    {
        /// <summary>
        /// Draw every string and edge as two crossed ribbons, so it keeps its width when the fretboard is seen almost edge-on
        /// (the aligned neck, looked at over the top of the real guitar). The floating neck faces the player and uses one.
        /// </summary>
        public bool crossRibbons;

        /// <summary>Per-finger colours (flag ghost.fingerColors): 1 yellow, 2 magenta, 3 lime, 4 orange.</summary>
        public bool fingerColors;

        /// <summary>Flag debugOverlay: draw the neck frame's axes at the socket (x red, y green, z blue) and a cross at the strum zone.</summary>
        public bool debugOverlay;

        /// <summary>x of the strum zone in mm; 0 or less = no strum zone (the strings fade out just beyond the last fret).</summary>
        public float strumZoneXMm;

        /// <summary>
        /// Aligned neck: the strings stop just beyond the last fret and come back only under the strumming hand, because the
        /// real strings show in between. False (the floating neck): they run unbroken from the nut to just past the strum zone.
        /// </summary>
        public bool breakStrings;
    }

    public static class NeckMeshBuilder
    {
        /// <summary>The most beads one leader is drawn with.</summary>
        public const int MaxBeadsPerLeader = 16;

        static readonly Color32 Black = new Color32(0, 0, 0, 255);

        // ------------------------------------------------------------------ lines

        /// <summary>Strings, frets, nut and fretboard edges.</summary>
        public static void BuildLines(NeckLayout layout, NeckSizes sizes, NeckBuildOptions options, NeckGeometry g)
        {
            g.Clear();
            float lastX = layout.LastFretXMm;
            float tailEnd = lastX + sizes.tailMm;
            float h = layout.stringHeightMm;
            float ribbon = options.crossRibbons ? 0.7f : 1f;   // two crossed ribbons overlap in the middle: keep the sum in range

            // Frets: dimmer than the strings, so the strings read first. They lie on the fretboard surface, across the neck.
            Color32 fret = NeckStyle.Dim(NeckStyle.CoolWhite, NeckStyle.FretIntensity);
            for (int n = 1; n <= layout.LastFret; n++)
            {
                float x = layout.fretXMm[n];
                float half = NeckMath.FretboardHalfWidthMm(layout, x);
                g.Line(P(x, 0f, -half), P(x, 0f, half), P(sizes.fretMm * 0.5f, 0f, 0f), fret, fret, true, true, NeckGeometry.GroupFixed, 0f);
            }

            // Nut: a clear bright bar. Its face is x = 0 (the frame's origin); its body lies on the headstock side.
            Color32 nut = NeckStyle.Dim(NeckStyle.NutWhite, NeckStyle.NutIntensity);
            float nutHalf = NeckMath.FretboardHalfWidthMm(layout, 0f);
            g.Line(P(-sizes.nutMm * 0.5f, 0f, -nutHalf), P(-sizes.nutMm * 0.5f, 0f, nutHalf), P(sizes.nutMm * 0.5f, 0f, 0f), nut, nut, true, true,
                   NeckGeometry.GroupFixed, 0f);

            // Fretboard edges: the dimmest lines, fading out beyond the last fret.
            Color32 edge = NeckStyle.Dim(NeckStyle.CoolWhite, NeckStyle.EdgeIntensity * ribbon);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 a = P(-sizes.nutMm, 0f, side * NeckMath.FretboardHalfWidthMm(layout, -sizes.nutMm));
                Vector3 b = P(lastX, 0f, side * NeckMath.FretboardHalfWidthMm(layout, lastX));
                Vector3 c = P(tailEnd, 0f, side * NeckMath.FretboardHalfWidthMm(layout, tailEnd));
                Ribbon(g, a, b, sizes.edgeMm, edge, edge, true, false, NeckGeometry.GroupFixed, options.crossRibbons);
                Ribbon(g, b, c, sizes.edgeMm, edge, Black, false, false, NeckGeometry.GroupFixed, options.crossRibbons);
            }

            // Strings: thin, bright, cool white; the low strings thicker, as on the real guitar. Each one follows its own gain
            // group, so a string that is not played can be dimmed without rebuilding the mesh.
            // Floating neck: unbroken from the nut to just past the strum zone, then fading out, so the neck reads as one neck
            // and the strumming hand has strings under it. Aligned neck (breakStrings): they stop just beyond the last fret and
            // come back under the strumming hand; the real strings show in between, and virtual strings half a metre long would
            // drift visibly off the real ones with any alignment error.
            Color32 str = NeckStyle.Dim(NeckStyle.CoolWhite, NeckStyle.StringIntensity * ribbon);
            bool strumZone = options.strumZoneXMm > 0f;
            float solidEnd = lastX, fadeEnd = tailEnd;
            if (strumZone && !options.breakStrings)
            {
                solidEnd = Mathf.Max(lastX, Mathf.Min(options.strumZoneXMm + sizes.strumHalfMm, layout.scaleLengthMm));
                fadeEnd = Mathf.Min(solidEnd + sizes.strumFadeMm, layout.scaleLengthMm);   // strings end at the saddle
            }
            bool fades = fadeEnd > solidEnd;
            float patchFade = sizes.strumFadeMm;
            float patchFrom = options.strumZoneXMm - sizes.strumHalfMm;
            float patchTo = options.strumZoneXMm + sizes.strumHalfMm;
            float patchStart = Mathf.Max(patchFrom - patchFade, tailEnd);            // never run back into the neck part: leave the break
            float patchEnd = Mathf.Min(patchTo + patchFade, layout.scaleLengthMm);   // strings end at the saddle
            bool patch = strumZone && options.breakStrings && patchFrom > patchStart && patchEnd > patchTo;
            for (int n = 1; n <= NeckLayout.StringCount; n++)
            {
                float w = sizes.StringWidthMm(n);
                int group = NeckGeometry.StringGroup(n);
                Ribbon(g, S(layout, n, 0f, h), S(layout, n, solidEnd, h), w, str, str, true, !fades, group, options.crossRibbons);
                if (fades)
                    Ribbon(g, S(layout, n, solidEnd, h), S(layout, n, fadeEnd, h), w, str, Black, false, false, group, options.crossRibbons);
                if (!patch) continue;
                // The stretch under the strumming hand, beyond the break.
                Ribbon(g, S(layout, n, patchStart, h), S(layout, n, patchFrom, h), w, Black, str, false, false, group, options.crossRibbons);
                Ribbon(g, S(layout, n, patchFrom, h), S(layout, n, patchTo, h), w, str, str, false, false, group, options.crossRibbons);
                Ribbon(g, S(layout, n, patchTo, h), S(layout, n, patchEnd, h), w, str, Black, false, false, group, options.crossRibbons);
            }

            if (options.debugOverlay)
            {
                const float axis = 30f, width = 0.5f;
                g.Line(P(0f, 0f, 0f), P(axis, 0f, 0f), P(0f, 0f, width), NeckStyle.DebugX, NeckStyle.DebugX, true, true, NeckGeometry.GroupFixed, 0f);
                g.Line(P(0f, 0f, 0f), P(0f, axis, 0f), P(width, 0f, 0f), NeckStyle.DebugY, NeckStyle.DebugY, true, true, NeckGeometry.GroupFixed, 0f);
                g.Line(P(0f, 0f, 0f), P(0f, 0f, axis), P(width, 0f, 0f), NeckStyle.DebugZ, NeckStyle.DebugZ, true, true, NeckGeometry.GroupFixed, 0f);
                if (options.strumZoneXMm > 0f)
                {
                    float sx = options.strumZoneXMm;
                    g.Line(P(sx - 10f, h, 0f), P(sx + 10f, h, 0f), P(0f, 0f, width), NeckStyle.Finger1, NeckStyle.Finger1, true, true, NeckGeometry.GroupFixed, 0f);
                    g.Line(P(sx, h, -10f), P(sx, h, 10f), P(width, 0f, 0f), NeckStyle.Finger1, NeckStyle.Finger1, true, true, NeckGeometry.GroupFixed, 0f);
                }
            }
        }

        // ------------------------------------------------------------------ dots, leaders, marks

        /// <summary>
        /// Fingertip dots (glow, core, white-hot centre), the dotted leaders to the finger numbers in <paramref name="labels"/>
        /// and the X / O marks at the nut. <paramref name="marks"/> is indexed by string number - 1 and may be null.
        /// </summary>
        public static void BuildDots(NeckLayout layout, NeckSizes sizes, NeckBuildOptions options, FingertipDot[] dots, int dotCount,
                                     StringMark[] marks, LabelSlot[] labels, int labelCount, NeckGeometry g)
        {
            g.Clear();
            float h = layout.stringHeightMm;
            int n = dots == null ? 0 : Mathf.Min(dotCount, dots.Length);

            for (int i = 0; i < n; i++)
            {
                FingertipDot dot = dots[i];
                int group = NeckGeometry.FingerGroup(dot.finger);
                bool colored = options.fingerColors && dot.finger >= 1 && dot.finger <= 4;
                Color32 core = colored ? NeckStyle.FingerColor(dot.finger) : NeckStyle.DotWhite;
                Color32 halo = NeckStyle.Dim(colored ? core : NeckStyle.GhostBlue, NeckStyle.HaloIntensity);
                Vector3 centre = P(dot.xMm, h, dot.zMm);
                Flat(g, centre, sizes.haloRadiusMm, halo, NeckGeometry.Filled, group, 1f);       // glow: falls off over the whole radius
                Flat(g, centre, sizes.dotRadiusMm, core, NeckGeometry.Filled, group, 0.18f);     // the dot itself, crisp
                Flat(g, centre, sizes.hotRadiusMm, NeckStyle.DotWhite, NeckGeometry.Filled, group, 0.6f);   // white-hot centre
            }

            // Dotted leaders: a row of small beads from the dot's rim to its number (dotted, so it is never read as a fret).
            int labelN = labels == null ? 0 : Mathf.Min(labelCount, labels.Length);
            for (int k = 0; k < labelN; k++)
            {
                LabelSlot slot = labels[k];
                if (slot.dotIndex < 0 || slot.dotIndex >= n) continue;
                FingertipDot dot = dots[slot.dotIndex];
                bool colored = options.fingerColors && dot.finger >= 1 && dot.finger <= 4;
                Color32 bead = NeckStyle.Dim(colored ? NeckStyle.FingerColor(dot.finger) : NeckStyle.DotWhite, NeckStyle.BeadIntensity);
                int group = NeckGeometry.FingerGroup(dot.finger);

                float toX = slot.xMm;
                float toZ = slot.zMm - slot.side * (sizes.labelCapMm * 0.5f + sizes.labelGapMm * 0.4f);   // just short of the digit
                float dx = toX - dot.xMm, dz = toZ - dot.zMm;
                float length = Mathf.Sqrt(dx * dx + dz * dz);
                float start = sizes.dotRadiusMm * 1.45f;                                                  // just outside the dot
                if (length <= start + sizes.beadPitchMm * 0.5f) continue;
                float ux = dx / length, uz = dz / length;
                int beads = Mathf.Min(MaxBeadsPerLeader, Mathf.Max(1, Mathf.FloorToInt((length - start) / sizes.beadPitchMm)));
                float step = (length - start) / beads;
                for (int b = 0; b < beads; b++)
                {
                    float d = start + (b + 0.5f) * step;
                    Flat(g, P(dot.xMm + ux * d, h, dot.zMm + uz * d), sizes.beadRadiusMm, bead, NeckGeometry.Filled, group, 0.35f);
                }
            }

            // X / O at the nut end of each string, on the headstock side of the nut.
            if (marks != null)
            {
                float markX = -(sizes.nutMm + sizes.markGapMm + sizes.markRadiusMm);
                float r = sizes.markRadiusMm;
                int count = Mathf.Min(marks.Length, NeckLayout.StringCount);
                for (int i = 0; i < count; i++)
                {
                    if (marks[i] == StringMark.None) continue;
                    float z = layout.stringZAtNutMm[i];
                    if (marks[i] == StringMark.Open)
                    {
                        float inner = Mathf.Clamp01(1f - sizes.markStrokeMm / Mathf.Max(r, 0.01f));
                        Flat(g, P(markX, h, z), r, NeckStyle.MarkOpen, inner, NeckGeometry.GroupFixed, 0f);
                    }
                    else
                    {
                        float a = r * 0.78f;                               // the X fits inside the same circle as the O
                        float w = sizes.markStrokeMm * 0.5f * 0.7071f;     // half stroke, split over x and z for a 45 degree line
                        g.Line(P(markX - a, h, z - a), P(markX + a, h, z + a), P(-w, 0f, w), NeckStyle.MarkMuted, NeckStyle.MarkMuted, true, true,
                               NeckGeometry.GroupFixed, 0f);
                        g.Line(P(markX - a, h, z + a), P(markX + a, h, z - a), P(w, 0f, w), NeckStyle.MarkMuted, NeckStyle.MarkMuted, true, true,
                               NeckGeometry.GroupFixed, 0f);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        static Vector3 P(float xMm, float yMm, float zMm)
        {
            return NeckMath.LocalMetres(xMm, yMm, zMm);
        }

        // A point on a string, in local metres.
        static Vector3 S(NeckLayout layout, int stringNumber, float xMm, float heightMm)
        {
            return NeckMath.LocalMetres(xMm, heightMm, NeckMath.StringZMm(layout, stringNumber, xMm));
        }

        // A line along the neck: a ribbon lying in the fretboard plane, plus (crossed) one standing on it.
        static void Ribbon(NeckGeometry g, Vector3 from, Vector3 to, float widthMm, Color32 colorFrom, Color32 colorTo, bool capFrom, bool capTo,
                           int group, bool crossed)
        {
            float half = widthMm * 0.5f * NeckMath.MetresPerMm;
            g.Line(from, to, new Vector3(0f, 0f, half), colorFrom, colorTo, capFrom, capTo, group, 0f);
            if (crossed) g.Line(from, to, new Vector3(0f, half, 0f), colorFrom, colorTo, capFrom, capTo, group, 0f);
        }

        // A disc lying flat on the neck (in the x-z plane).
        static void Flat(NeckGeometry g, Vector3 centre, float radiusMm, Color32 color, float inner, int group, float softness)
        {
            float r = radiusMm * NeckMath.MetresPerMm;
            g.Disc(centre, new Vector3(r, 0f, 0f), new Vector3(0f, 0f, r), color, inner, group, softness);
        }
    }
}
