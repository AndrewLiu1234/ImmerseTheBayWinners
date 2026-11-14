// NeckMeshBuilder: turns a NeckLayout, the fingertip dots and the string marks into quads (NeckGeometry). Pure code: plain
// arithmetic on the data it is given, no scene access, no allocation. FloatingNeck uploads the result to two meshes:
//   lines  strings, frets, nut, fretboard edges                  (changes only when the layout or the mode changes)
//   dots   fingertip dots, leaders to the finger numbers, X / O  (changes when the chord changes or the eye moves)
// Lines lie on the fretboard surface or at string height, dots at string height. The X / O marks stand in a row beyond the
// nut and turn to face the eye when its position is known.
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

        /// <summary>True when <see cref="viewerMm"/> holds the eye's position; the X / O marks then turn to face it.</summary>
        public bool hasViewer;

        /// <summary>The eye in the neck frame, in mm (FloatingNeck takes it from the head).</summary>
        public Vector3 viewerMm;
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

            // X / O marks. Each is as tall as text must be, about 2.5 string spacings on the floating neck, so they cannot sit
            // on the string ends: they stand in a row beyond the nut, one slot per string in string order (the X / O row of a
            // chord diagram), and every marked string carries on past the nut as a dimmer line that fans out to its own mark.
            // When the eye's position is known the marks turn to face it, so they show round and at full size; otherwise they
            // lie flat on the neck.
            if (marks != null)
            {
                Vector3 row = new Vector3(sizes.markRowXMm, h, 0f);
                Vector3 view, along, across;
                MarkFrame(options, row, out view, out along, out across);
                float r = sizes.markRadiusMm;
                float halfStroke45 = sizes.markStrokeMm * 0.35355f;   // half the stroke, split over two axes for a 45 degree line
                Color32 lead = NeckStyle.Dim(NeckStyle.CoolWhite, NeckStyle.MarkLeadIntensity);
                int count = Mathf.Min(marks.Length, NeckLayout.StringCount);
                for (int i = 0; i < count; i++)
                {
                    if (marks[i] == StringMark.None) continue;
                    int stringNumber = i + 1;
                    Vector3 centre = row + across * (MarkSlotOffset(stringNumber) * sizes.markPitchMm);

                    // The string's own line, from its end at the nut to just short of its mark (for an X, short of the tips of
                    // its arms, so a line that arrives along a diagonal never reads as a longer arm). It follows the string's
                    // gain group, so it dims with a string that is not played.
                    Vector3 start = new Vector3(-sizes.nutMm, h, layout.stringZAtNutMm[i]);
                    Vector3 toMark = centre - start;
                    float length = toMark.magnitude;
                    float stop = MarkLeadStopMm(sizes, marks[i]);
                    Vector3 dir = length > 1e-4f ? toMark / length : along;
                    Vector3 side = Vector3.Cross(dir, view);
                    if (length > stop + sizes.markStrokeMm && side.sqrMagnitude > 1e-8f)
                    {
                        side = side.normalized * (0.5f * sizes.StringWidthMm(stringNumber));
                        g.Line(Mm(start), Mm(centre - dir * stop), Mm(side), lead, lead, false, true, NeckGeometry.StringGroup(stringNumber), 0f);
                    }

                    if (marks[i] == StringMark.Open)
                    {
                        float inner = Mathf.Clamp01(1f - sizes.markStrokeMm / Mathf.Max(r, 0.01f));
                        g.Disc(Mm(centre), Mm(along * r), Mm(across * r), NeckStyle.MarkOpen, inner, NeckGeometry.GroupFixed, 0f);
                    }
                    else
                    {
                        // Two strokes from corner to corner of the mark's square: the X is exactly as tall as the O.
                        Vector3 rising = (along + across) * (r - halfStroke45), falling = (along - across) * (r - halfStroke45);
                        g.Line(Mm(centre - rising), Mm(centre + rising), Mm((across - along) * halfStroke45), NeckStyle.MarkMuted, NeckStyle.MarkMuted,
                               true, true, NeckGeometry.GroupFixed, 0f);
                        g.Line(Mm(centre - falling), Mm(centre + falling), Mm((along + across) * halfStroke45), NeckStyle.MarkMuted, NeckStyle.MarkMuted,
                               true, true, NeckGeometry.GroupFixed, 0f);
                    }
                }
            }
        }

        /// <summary>
        /// How far from a mark's centre the line that leads to it stops, in mm: half a stroke outside the O's ring, or half a
        /// stroke beyond the tips of the X's arms (they reach (r - stroke / 2 / sqrt 2) * sqrt 2 along the diagonals).
        /// </summary>
        public static float MarkLeadStopMm(NeckSizes sizes, StringMark mark)
        {
            float r = sizes.markRadiusMm;
            float reach = mark == StringMark.Muted ? (r - sizes.markStrokeMm * 0.35355f) * 1.41421f : r;
            return reach + 0.5f * sizes.markStrokeMm;
        }

        /// <summary>
        /// Where a string's X / O slot sits in the row beyond the nut, in mark spacings from the row's centre: string 1 at
        /// +2.5 (toward string 1's side), string 6 at -2.5. The order the strings have on the neck, as in a chord diagram.
        /// </summary>
        public static float MarkSlotOffset(int stringNumber)
        {
            return 0.5f * (NeckLayout.StringCount + 1) - Mathf.Clamp(stringNumber, 1, NeckLayout.StringCount);
        }

        /// <summary>
        /// The directions the X / O marks are drawn in, all unit vectors in the neck frame: <paramref name="view"/> from the eye
        /// to the row, <paramref name="along"/> the neck as the eye sees it and <paramref name="across"/> toward string 1 as the
        /// eye sees it, both square to the view (so a mark drawn on them faces the eye). Without a known eye, or when the eye
        /// looks along the neck, the marks lie flat: along = +x, across = +z, view = straight down onto the fretboard.
        /// </summary>
        public static void MarkFrame(NeckBuildOptions options, Vector3 rowMm, out Vector3 view, out Vector3 along, out Vector3 across)
        {
            view = new Vector3(0f, -1f, 0f);
            along = new Vector3(1f, 0f, 0f);
            across = new Vector3(0f, 0f, 1f);
            if (!options.hasViewer) return;
            Vector3 v = rowMm - options.viewerMm;
            float distance = v.magnitude;
            if (!(distance > 1f)) return;
            v /= distance;
            Vector3 a = along - Vector3.Dot(along, v) * v;
            if (a.sqrMagnitude < 0.01f) return;
            a.Normalize();
            Vector3 c = across - Vector3.Dot(across, v) * v - Vector3.Dot(across, a) * a;
            if (c.sqrMagnitude < 0.01f) return;
            view = v;
            along = a;
            across = c.normalized;
        }

        // ------------------------------------------------------------------ helpers

        static Vector3 P(float xMm, float yMm, float zMm)
        {
            return NeckMath.LocalMetres(xMm, yMm, zMm);
        }

        // Millimetres to the neck transform's local metres, for a point or a vector.
        static Vector3 Mm(Vector3 mm)
        {
            return mm * NeckMath.MetresPerMm;
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
