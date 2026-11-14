using System.Collections.Generic;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// Builds flat line-art out of triangles: thick strokes with round ends, arcs, discs. The Learn HUD draws its marks and
    /// icons this way because the built-in font has no check or cross glyphs, and because on an additive display contrast
    /// comes from stroke weight: every stroke here is a real, wide shape, never a hairline.
    ///
    /// Coordinates are 2D in the shape's own plane: x to the viewer's right, y up, z = 0 (the same way HudText faces).
    /// Angles are in degrees, clockwise from straight up as the viewer sees them (so +90 is to the right), which is the
    /// tuner needle's convention too. Plain managed code (lists of Vector3 / int), no engine objects, so it is unit-tested.
    /// </summary>
    public sealed class StrokeGeometry
    {
        public readonly List<Vector3> Vertices = new List<Vector3>(256);
        public readonly List<int> Triangles = new List<int>(768);

        const float Tiny = 1e-6f;

        public void Clear()
        {
            Vertices.Clear();
            Triangles.Clear();
        }

        /// <summary>The point <paramref name="radius"/> away from <paramref name="center"/> at <paramref name="degrees"/> clockwise from straight up.</summary>
        public static Vector2 OnCircle(Vector2 center, float radius, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            return new Vector2(center.x + radius * Mathf.Sin(a), center.y + radius * Mathf.Cos(a));
        }

        int Add(Vector2 p)
        {
            Vertices.Add(new Vector3(p.x, p.y, 0f));
            return Vertices.Count - 1;
        }

        public void AddTriangle(Vector2 a, Vector2 b, Vector2 c)
        {
            int i = Add(a);
            Add(b);
            Add(c);
            Triangles.Add(i);
            Triangles.Add(i + 1);
            Triangles.Add(i + 2);
        }

        /// <summary>A four-sided shape; the corners go around its edge in order.</summary>
        public void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            int i = Add(a);
            Add(b);
            Add(c);
            Add(d);
            Triangles.Add(i);
            Triangles.Add(i + 1);
            Triangles.Add(i + 2);
            Triangles.Add(i);
            Triangles.Add(i + 2);
            Triangles.Add(i + 3);
        }

        /// <summary>A straight stroke with square ends. Returns false (and adds nothing) when the two points coincide.</summary>
        public bool AddSegment(Vector2 a, Vector2 b, float width)
        {
            Vector2 d = b - a;
            float length = d.magnitude;
            if (!(length > Tiny) || !(width > 0f)) return false;
            Vector2 n = new Vector2(-d.y, d.x) * (0.5f * width / length);
            AddQuad(a + n, b + n, b - n, a - n);
            return true;
        }

        /// <summary>A filled circle.</summary>
        public void AddDisc(Vector2 center, float radius, int segments)
        {
            if (!(radius > 0f)) return;
            if (segments < 6) segments = 6;
            int c = Add(center);
            for (int i = 0; i < segments; i++) Add(OnCircle(center, radius, 360f * i / segments));
            for (int i = 0; i < segments; i++)
            {
                Triangles.Add(c);
                Triangles.Add(c + 1 + i);
                Triangles.Add(c + 1 + (i + 1) % segments);
            }
        }

        /// <summary>A straight stroke with round ends (a single dot when the two points coincide).</summary>
        public void AddCapsule(Vector2 a, Vector2 b, float width, int capSegments)
        {
            AddSegment(a, b, width);
            AddDisc(a, 0.5f * width, capSegments);
            if ((b - a).sqrMagnitude > Tiny * Tiny) AddDisc(b, 0.5f * width, capSegments);
        }

        /// <summary>
        /// A stroke through <paramref name="count"/> points with round joints and round ends. Joints overlap, which is
        /// invisible with the Learn HUD's "lighten" blending.
        /// </summary>
        public void AddPolyline(Vector2[] points, int count, float width, int capSegments)
        {
            if (points == null) return;
            if (count > points.Length) count = points.Length;
            for (int i = 0; i < count; i++)
            {
                AddDisc(points[i], 0.5f * width, capSegments);
                if (i + 1 < count) AddSegment(points[i], points[i + 1], width);
            }
        }

        /// <summary>
        /// A curved stroke: the part of a circle outline from <paramref name="fromDegrees"/> to <paramref name="toDegrees"/>
        /// (clockwise from straight up), <paramref name="width"/> wide, with no gaps or overlaps along the curve.
        /// </summary>
        public void AddArc(Vector2 center, float radius, float fromDegrees, float toDegrees, float width, int segments, bool roundEnds)
        {
            if (!(radius > 0f) || !(width > 0f)) return;
            if (segments < 1) segments = 1;
            float inner = Mathf.Max(0f, radius - 0.5f * width);
            float outer = radius + 0.5f * width;
            int first = Vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Lerp(fromDegrees, toDegrees, i / (float)segments);
                Add(OnCircle(center, inner, a));
                Add(OnCircle(center, outer, a));
            }
            for (int i = 0; i < segments; i++)
            {
                int k = first + 2 * i;
                Triangles.Add(k);
                Triangles.Add(k + 1);
                Triangles.Add(k + 3);
                Triangles.Add(k);
                Triangles.Add(k + 3);
                Triangles.Add(k + 2);
            }
            if (roundEnds)
            {
                AddDisc(OnCircle(center, radius, fromDegrees), 0.5f * width, 10);
                AddDisc(OnCircle(center, radius, toDegrees), 0.5f * width, 10);
            }
        }

        /// <summary>A circle outline.</summary>
        public void AddRing(Vector2 center, float radius, float width, int segments)
        {
            AddArc(center, radius, 0f, 360f, width, segments, false);
        }

        /// <summary>An arrow head: two short strokes meeting at <paramref name="tip"/>, opening away from <paramref name="pointingDegrees"/>.</summary>
        public void AddArrowHead(Vector2 tip, float pointingDegrees, float size, float width)
        {
            Vector2 left = OnCircle(tip, size, pointingDegrees + 180f - 42f);
            Vector2 right = OnCircle(tip, size, pointingDegrees + 180f + 42f);
            AddCapsule(left, tip, width, 10);
            AddCapsule(right, tip, width, 10);
        }

        // ------------------------------------------------------------------ measurements (for tests and layout)

        /// <summary>The box around everything added so far. False when empty.</summary>
        public bool TryGetBounds(out Vector2 min, out Vector2 max)
        {
            min = Vector2.zero;
            max = Vector2.zero;
            if (Vertices.Count == 0) return false;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < Vertices.Count; i++)
            {
                Vector3 v = Vertices[i];
                if (v.x < minX) minX = v.x;
                if (v.y < minY) minY = v.y;
                if (v.x > maxX) maxX = v.x;
                if (v.y > maxY) maxY = v.y;
            }
            min = new Vector2(minX, minY);
            max = new Vector2(maxX, maxY);
            return true;
        }

        /// <summary>Sum of the areas of all triangles (overlaps counted twice).</summary>
        public float TriangleArea()
        {
            float area = 0f;
            for (int i = 0; i + 2 < Triangles.Count; i += 3)
            {
                Vector3 a = Vertices[Triangles[i]];
                Vector3 b = Vertices[Triangles[i + 1]];
                Vector3 c = Vertices[Triangles[i + 2]];
                area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            }
            return area;
        }

        /// <summary>True when every triangle index points at an existing vertex and every coordinate is a finite number.</summary>
        public bool IsWellFormed()
        {
            if (Triangles.Count % 3 != 0) return false;
            for (int i = 0; i < Triangles.Count; i++)
            {
                int index = Triangles[i];
                if (index < 0 || index >= Vertices.Count) return false;
            }
            for (int i = 0; i < Vertices.Count; i++)
            {
                Vector3 v = Vertices[i];
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || v.z != 0f) return false;
            }
            return true;
        }
    }
}
