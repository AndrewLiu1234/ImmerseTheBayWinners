using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// The drawings of the Learn HUD, as geometry. Marks and icons fit a 1 x 1 box centred on the origin (x right, y up);
    /// the tuner dial parts are in dial units (the track has radius 1, the pivot is the origin). Strokes are heavy on
    /// purpose: 0.10 to 0.20 of the box, so a 1.5 degree mark has strokes about 10 glasses pixels wide.
    /// Shapes differ from each other in outline, not only in colour. Pure (fills a <see cref="StrokeGeometry"/>); unit-tested.
    /// </summary>
    public static class LearnGlyphs
    {
        /// <summary>Number of values in <see cref="LearnGlyph"/>.</summary>
        public const int Count = 21;

        /// <summary>Stroke width of the per-string marks (fraction of the box).</summary>
        public const float MarkStroke = 0.17f;

        /// <summary>Stroke width of the tip icons (fraction of the box).</summary>
        public const float IconStroke = 0.13f;

        // The tuner pointer covers this part of the dial radius, so the note name has the middle of the dial to itself.
        public const float NeedleInner = 0.64f;
        public const float NeedleOuter = 1.07f;

        const int Round = 12;

        static readonly Vector2[] s_Points = new Vector2[4];

        /// <summary>True when the glyph is drawn in the 1 x 1 box (false for the tuner dial parts, which use dial units).</summary>
        public static bool FitsUnitBox(LearnGlyph glyph)
        {
            return glyph != LearnGlyph.DialTrack && glyph != LearnGlyph.DialZone && glyph != LearnGlyph.DialNeedle;
        }

        /// <summary>Clears <paramref name="g"/> and fills it with the drawing of <paramref name="glyph"/>.</summary>
        public static void Build(LearnGlyph glyph, StrokeGeometry g)
        {
            g.Clear();
            Vector2 o = Vector2.zero;
            switch (glyph)
            {
                case LearnGlyph.Check:
                    s_Points[0] = new Vector2(-0.36f, -0.02f);
                    s_Points[1] = new Vector2(-0.12f, -0.28f);
                    s_Points[2] = new Vector2(0.38f, 0.30f);
                    g.AddPolyline(s_Points, 3, MarkStroke, Round);
                    break;

                case LearnGlyph.Cross:
                    g.AddCapsule(new Vector2(-0.30f, -0.30f), new Vector2(0.30f, 0.30f), MarkStroke, Round);
                    g.AddCapsule(new Vector2(-0.30f, 0.30f), new Vector2(0.30f, -0.30f), MarkStroke, Round);
                    break;

                case LearnGlyph.Bar:      // "flat line": the string did not ring
                    g.AddCapsule(new Vector2(-0.36f, 0f), new Vector2(0.36f, 0f), 0.20f, Round);
                    break;

                case LearnGlyph.Question:
                {
                    var c = new Vector2(0f, 0.17f);
                    g.AddArc(c, 0.20f, -90f, 135f, 0.15f, 20, true);
                    g.AddCapsule(StrokeGeometry.OnCircle(c, 0.20f, 135f), new Vector2(0f, -0.12f), 0.15f, Round);
                    g.AddDisc(new Vector2(0f, -0.36f), 0.095f, 16);
                    break;
                }

                case LearnGlyph.Waves:    // a sounding dot with waves leaving it: let it ring
                {
                    var c = new Vector2(-0.25f, 0f);
                    g.AddDisc(c, 0.10f, 16);
                    g.AddArc(c, 0.28f, 50f, 130f, 0.10f, 10, true);
                    g.AddArc(c, 0.48f, 52f, 128f, 0.10f, 12, true);
                    g.AddArc(c, 0.68f, 58f, 122f, 0.10f, 14, true);
                    break;
                }

                case LearnGlyph.Arch:     // a finger arched over a string
                {
                    var c = new Vector2(0f, 0.04f);
                    g.AddArc(c, 0.29f, -90f, 90f, IconStroke, 18, true);
                    g.AddCapsule(new Vector2(-0.29f, 0.04f), new Vector2(-0.29f, -0.18f), IconStroke, Round);
                    g.AddCapsule(new Vector2(0.29f, 0.04f), new Vector2(0.29f, -0.18f), IconStroke, Round);
                    g.AddCapsule(new Vector2(-0.44f, -0.37f), new Vector2(0.44f, -0.37f), 0.08f, Round);
                    break;
                }

                case LearnGlyph.Press:    // an arrow pressing down onto a line
                    g.AddCapsule(new Vector2(0f, 0.40f), new Vector2(0f, -0.10f), IconStroke, Round);
                    g.AddArrowHead(new Vector2(0f, -0.14f), 180f, 0.24f, IconStroke);
                    g.AddCapsule(new Vector2(-0.36f, -0.38f), new Vector2(0.36f, -0.38f), 0.10f, Round);
                    break;

                case LearnGlyph.Move:     // a two-headed arrow
                    g.AddCapsule(new Vector2(-0.36f, 0f), new Vector2(0.36f, 0f), IconStroke, Round);
                    g.AddArrowHead(new Vector2(0.40f, 0f), 90f, 0.22f, IconStroke);
                    g.AddArrowHead(new Vector2(-0.40f, 0f), 270f, 0.22f, IconStroke);
                    break;

                case LearnGlyph.Skip:     // a circle with a slash
                    g.AddRing(o, 0.36f, 0.12f, 36);
                    g.AddCapsule(StrokeGeometry.OnCircle(o, 0.36f, 315f), StrokeGeometry.OnCircle(o, 0.36f, 135f), 0.12f, Round);
                    break;

                case LearnGlyph.Again:    // a circular arrow
                    g.AddArc(o, 0.32f, 40f, 320f, 0.12f, 30, true);
                    g.AddArrowHead(StrokeGeometry.OnCircle(o, 0.32f, 326f), 50f, 0.20f, 0.12f);
                    break;

                case LearnGlyph.Mismatch: // a not-equal sign
                    g.AddCapsule(new Vector2(-0.30f, 0.13f), new Vector2(0.30f, 0.13f), 0.12f, Round);
                    g.AddCapsule(new Vector2(-0.30f, -0.13f), new Vector2(0.30f, -0.13f), 0.12f, Round);
                    g.AddCapsule(new Vector2(-0.14f, -0.36f), new Vector2(0.14f, 0.36f), 0.11f, Round);
                    break;

                case LearnGlyph.Timing:   // a clock
                    g.AddRing(o, 0.36f, 0.11f, 36);
                    g.AddCapsule(o, new Vector2(0f, 0.22f), 0.10f, Round);
                    g.AddCapsule(o, new Vector2(0.17f, -0.06f), 0.10f, Round);
                    break;

                case LearnGlyph.ArrowUp:
                    g.AddCapsule(new Vector2(0f, -0.38f), new Vector2(0f, 0.34f), 0.15f, Round);
                    g.AddArrowHead(new Vector2(0f, 0.40f), 0f, 0.28f, 0.15f);
                    break;

                case LearnGlyph.ArrowDown:
                    g.AddCapsule(new Vector2(0f, 0.38f), new Vector2(0f, -0.34f), 0.15f, Round);
                    g.AddArrowHead(new Vector2(0f, -0.40f), 180f, 0.28f, 0.15f);
                    break;

                case LearnGlyph.Disc:     // a filled dot: a step that is done
                    g.AddDisc(o, 0.5f, 32);
                    break;

                case LearnGlyph.Ring:     // an empty circle: a step still to do
                    g.AddRing(o, 0.43f, 0.14f, 36);
                    break;

                case LearnGlyph.Target:   // a ring around a dot: the step being done now
                    g.AddRing(o, 0.44f, 0.11f, 36);
                    g.AddDisc(o, 0.23f, 24);
                    break;

                case LearnGlyph.DialTrack:
                {
                    float max = TunerMath.MaxNeedleDegrees;
                    g.AddArc(o, 1f, -max, max, 0.03f, 40, true);
                    for (int i = -2; i <= 2; i++)
                    {
                        float a = max * i / 2f;                    // -50, -25, 0, +25, +50 cents
                        float outer = i % 2 == 0 ? 1.13f : 1.09f;  // the centre and the two ends are longer
                        g.AddCapsule(StrokeGeometry.OnCircle(o, 1.0f, a), StrokeGeometry.OnCircle(o, outer, a), 0.03f, 8);
                    }
                    break;
                }

                case LearnGlyph.DialZone:  // the in-tune zone: a heavier piece of the dial, +-5 cents wide
                    g.AddArc(o, 1f, -TunerMath.ZoneHalfDegrees, TunerMath.ZoneHalfDegrees, 0.10f, 8, false);
                    break;

                case LearnGlyph.DialNeedle:
                    g.AddCapsule(new Vector2(0f, NeedleInner), new Vector2(0f, NeedleOuter), 0.05f, Round);
                    break;

                case LearnGlyph.Line:      // a unit square, stretched into guide lines by the preview
                    g.AddQuad(new Vector2(-0.5f, -0.5f), new Vector2(-0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, -0.5f));
                    break;
            }
        }
    }
}
