// CapsuleHandMesh: the vertex data of the capsule hand. Plain C# (arrays of Vector3 / Color32 / int only): no Mesh, no
// Unity object, so it runs and is tested outside the editor. CapsuleHand copies these arrays into Mesh objects.
//
// WHY A FINGER IS ONE SKIN AND NOT THREE CAPSULES
//   The ghost shader is additive and writes no depth, so every surface is visible through every other one. Three
//   overlapping capsules would show their buried round ends as bright arcs across each joint ("a stack of pills").
//   GhostTubeGeometry therefore builds exactly the OUTSIDE of the union of the capsules of one finger:
//     * straight (slightly tapered) tube sections along the bones,
//     * at every joint, on the outside of the bend, the wedge of the joint sphere that the bend opens up,
//     * on the inside of the bend, the crease where the two tubes run into each other (nothing is drawn past it),
//     * a hemisphere at the fingertip, and a start that fades to nothing inside the palm.
//   The topology never changes. Update() only rewrites the positions and normals, in place, with no allocation.
//
// VERTEX COLOURS follow the GhostHand shader's convention: red = fingertip glow, blue = fade out, alpha unused.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>A tube swept along a short polyline (one finger): fading start, rounded bends, hemispherical tip.</summary>
    public sealed class GhostTubeGeometry
    {
        /// <summary>The crease on the inside of a bend may use up at most this fraction of each neighbouring bone.</summary>
        public const float MaxCreaseFraction = 0.40f;
        const float MaxBend = 2.9f;                 // radians; beyond this the crease distance is clamped anyway

        public readonly int pointCount;             // centre-line points: start, one per joint, fingertip centre
        public readonly int around;                 // vertices around the tube
        public readonly int elbowSteps;             // rings spanning the outside of each bend, minus one
        public readonly int capRows;                // rings of the fingertip hemisphere (the pole is one extra vertex)
        public readonly int fadeRows;               // rings of the fading start
        public readonly float fadeFraction;         // the start fades in over this fraction of the first section
        public readonly float tipGlowStart;         // the fingertip glow starts this far along the last section (0..1)

        public readonly int rowCount;
        public readonly int vertexCount;
        public readonly int triangleCount;
        public readonly Vector3[] vertices;
        public readonly Vector3[] normals;
        public readonly Color32[] colors;           // static: (tip glow, 0, fade out, 255)
        public readonly int[] triangles;            // static

        readonly float[] m_cos;
        readonly float[] m_sin;

        public GhostTubeGeometry(int pointCount, int around, int elbowSteps, int capRows, int fadeRows, float fadeFraction, float tipGlowStart)
        {
            this.pointCount = Mathf.Max(pointCount, 3);
            this.around = Mathf.Max(around, 6);
            this.elbowSteps = Mathf.Max(elbowSteps, 1);
            this.capRows = Mathf.Max(capRows, 2);
            this.fadeRows = Mathf.Max(fadeRows, 2);
            this.fadeFraction = Mathf.Clamp(fadeFraction, 0.05f, 1f - MaxCreaseFraction - 0.05f);
            this.tipGlowStart = Mathf.Clamp(tipGlowStart, MaxCreaseFraction + 0.05f, 0.95f);

            int n = this.pointCount, N = this.around, K = this.elbowSteps, M = this.capRows, F = this.fadeRows;
            int tipStartRow = F + (n - 2) * (K + 1);
            rowCount = tipStartRow + 1 + M;
            vertexCount = rowCount * N + 1;
            triangleCount = (rowCount - 1) * N * 2 + N;

            vertices = new Vector3[vertexCount];
            normals = new Vector3[vertexCount];
            colors = new Color32[vertexCount];
            triangles = new int[triangleCount * 3];
            m_cos = new float[N];
            m_sin = new float[N];
            for (int i = 0; i < N; i++)
            {
                float phi = 2f * Mathf.PI * i / N;
                m_cos[i] = Mathf.Cos(phi);
                m_sin[i] = Mathf.Sin(phi);
            }

            // colours: the start fades in (blue = fade out), the tip glows (red)
            for (int row = 0; row < rowCount; row++)
            {
                byte fadeOut = 0, tip = 0;
                if (row < F)
                {
                    float x = (float)row / (F - 1);
                    float shown = x * x * (3f - 2f * x);
                    fadeOut = (byte)Mathf.RoundToInt((1f - shown) * 255f);
                }
                if (row > tipStartRow) tip = 255;
                Color32 c = new Color32(tip, 0, fadeOut, 255);
                for (int i = 0; i < N; i++) colors[row * N + i] = c;
            }
            colors[rowCount * N] = new Color32(255, 0, 0, 255);

            // triangles: Unity's front faces are clockwise; with ring direction e2 = cross(axis, e1) this order faces outward
            int t = 0;
            for (int row = 0; row < rowCount - 1; row++)
            {
                for (int i = 0; i < N; i++)
                {
                    int i1 = (i + 1) % N;
                    int a = row * N + i, b = row * N + i1, c = (row + 1) * N + i, d = (row + 1) * N + i1;
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                    triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
                }
            }
            int lastRow = (rowCount - 1) * N, pole = rowCount * N;
            for (int i = 0; i < N; i++)
            {
                triangles[t++] = lastRow + i; triangles[t++] = lastRow + (i + 1) % N; triangles[t++] = pole;
            }
        }

        /// <summary>
        /// Rewrite the vertices and normals for a centre line. points[0] is where the skin starts (faded), points[1 .. n-2]
        /// are the joints, points[n-1] is the fingertip centre; radii[i] is the thickness at points[i]. upHint fixes how the
        /// ring is turned about the first section (any direction not parallel to it). No allocation.
        /// </summary>
        public void Update(Vector3[] points, float[] radii, Vector3 upHint)
        {
            int n = pointCount, N = around, K = elbowSteps, M = capRows;

            Vector3 p0 = points[0];
            Vector3 seg = points[1] - p0;
            float len = seg.magnitude;
            Vector3 a = len > 1e-7f ? seg / len : Vector3.forward;

            Vector3 e1 = upHint - a * Vector3.Dot(upHint, a);
            float e1m = e1.magnitude;
            if (!(e1m > 1e-5f))
            {
                Vector3 alt = Mathf.Abs(a.y) < 0.9f ? Vector3.up : Vector3.right;
                e1 = alt - a * Vector3.Dot(alt, a);
                e1m = e1.magnitude;
            }
            e1 /= e1m;
            Vector3 e2 = Vector3.Cross(a, e1);

            int row = 0;
            for (int f = 0; f < fadeRows; f++)
            {
                float g = fadeFraction * f / (fadeRows - 1);
                WriteRing(row++, p0 + seg * g, Mathf.Lerp(radii[0], radii[1], g), e1, e2, a, 1f, 0f);
            }

            float prevLen = len;
            for (int j = 1; j <= n - 2; j++)
            {
                Vector3 joint = points[j];
                Vector3 segN = points[j + 1] - joint;
                float lenN = segN.magnitude;
                Vector3 b = lenN > 1e-7f ? segN / lenN : a;
                float r = radii[j];

                Vector3 w = Vector3.Cross(a, b);
                float sinT = w.magnitude;
                float cosT = Vector3.Dot(a, b);

                if (sinT < 1e-5f)
                {   // straight on (or folded exactly back, which no finger does): every ring of the bend is the same circle
                    for (int k = 0; k <= K; k++) WriteRing(row++, joint, r, e1, e2, a, 1f, 0f);
                    if (cosT > 0f) a = b;
                }
                else
                {
                    w /= sinT;
                    float theta = Mathf.Atan2(sinT, cosT);
                    Vector3 outside = (a * cosT - b) / sinT;                 // unit, perpendicular to a, away from the bend
                    float tanHalf = Mathf.Tan(Mathf.Min(theta, MaxBend) * 0.5f);
                    float creaseMax = MaxCreaseFraction * Mathf.Min(prevLen, lenN);
                    Vector3 e1End = Rotate(e1, w, sinT, cosT);
                    Vector3 e2End = Rotate(e2, w, sinT, cosT);

                    for (int k = 0; k <= K; k++)
                    {
                        float t = (float)k / K;
                        float ang = theta * t;
                        float sa = Mathf.Sin(ang), ca = Mathf.Cos(ang);
                        Vector3 e1t = Rotate(e1, w, sa, ca);
                        Vector3 e2t = Rotate(e2, w, sa, ca);
                        int o = row * N;
                        for (int i = 0; i < N; i++)
                        {
                            Vector3 u0 = e1 * m_cos[i] + e2 * m_sin[i];
                            float c = Vector3.Dot(u0, outside);
                            if (c >= 0f)
                            {   // outside of the bend: on the joint sphere
                                Vector3 ut = e1t * m_cos[i] + e2t * m_sin[i];
                                vertices[o + i] = joint + ut * r;
                                normals[o + i] = ut;
                            }
                            else
                            {   // inside of the bend: the crease, where this generator of the incoming tube meets the outgoing tube
                                float s = Mathf.Max(r * c * tanHalf, -creaseMax);
                                vertices[o + i] = joint + a * s + u0 * r;
                                if (t < 0.5f) normals[o + i] = u0;
                                else
                                {
                                    Vector3 u1 = e1End * m_cos[i] + e2End * m_sin[i];
                                    if (t > 0.5f) normals[o + i] = u1;
                                    else
                                    {
                                        Vector3 mid = u0 + u1;
                                        float mm = mid.magnitude;
                                        normals[o + i] = mm > 1e-6f ? mid / mm : u0;
                                    }
                                }
                            }
                        }
                        row++;
                    }
                    e1 = e1End; e2 = e2End; a = b;
                }
                prevLen = lenN;
            }

            // the last section and the fingertip
            Vector3 last = points[n - 2];
            Vector3 tipCentre = points[n - 1];
            float rt = radii[n - 1];
            WriteRing(row++, Vector3.Lerp(last, tipCentre, tipGlowStart), Mathf.Lerp(radii[n - 2], rt, tipGlowStart), e1, e2, a, 1f, 0f);
            for (int m = 0; m < M; m++)
            {
                float beta = 0.5f * Mathf.PI * m / M;
                float sb = Mathf.Sin(beta), cb = Mathf.Cos(beta);
                WriteRing(row++, tipCentre + a * (rt * sb), rt * cb, e1, e2, a, cb, sb);
            }
            vertices[rowCount * N] = tipCentre + a * rt;
            normals[rowCount * N] = a;
        }

        void WriteRing(int row, Vector3 centre, float radius, Vector3 e1, Vector3 e2, Vector3 axis, float normalRadial, float normalAxial)
        {
            int o = row * around;
            for (int i = 0; i < around; i++)
            {
                Vector3 u = e1 * m_cos[i] + e2 * m_sin[i];
                vertices[o + i] = centre + u * radius;
                normals[o + i] = u * normalRadial + axis * normalAxial;
            }
        }

        // Rodrigues: v turned about the unit axis w by the angle whose sine and cosine are given.
        static Vector3 Rotate(Vector3 v, Vector3 w, float sin, float cos)
        {
            return v * cos + Vector3.Cross(w, v) * sin + w * (Vector3.Dot(w, v) * (1f - cos));
        }
    }

    /// <summary>The palm: a rounded slab lofted through CapsuleHandDimensions.palm, faded out at both ends. Built once.</summary>
    public static class GhostPalmGeometry
    {
        public static void Build(CapsuleHandDimensions dims, int around,
            out Vector3[] vertices, out Vector3[] normals, out Color32[] colors, out int[] triangles)
        {
            int N = Mathf.Max(around, 8);
            CapsuleHandDimensions.PalmStation[] st = dims.palm;
            int rows = st.Length;
            vertices = new Vector3[rows * N];
            normals = new Vector3[rows * N];
            colors = new Color32[rows * N];
            triangles = new int[(rows - 1) * N * 6];

            float power = 2f / Mathf.Max(dims.palmExponent, 1f);
            for (int r = 0; r < rows; r++)
            {
                byte fadeOut = (byte)Mathf.RoundToInt((1f - Mathf.Clamp01(st[r].alpha)) * 255f);
                for (int i = 0; i < N; i++)
                {
                    float phi = 2f * Mathf.PI * i / N;
                    float c = Mathf.Cos(phi), s = Mathf.Sin(phi);
                    float x = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), power) * st[r].halfWidth;
                    float y = Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), power) * st[r].halfThickness;
                    vertices[r * N + i] = new Vector3(x, y, st[r].z);
                    colors[r * N + i] = new Color32(0, 0, fadeOut, 255);
                }
            }

            // Winding: pick the order whose face normal points away from the palm's axis (the same for both hands).
            Vector3 p00 = vertices[0], p01 = vertices[1], p10 = vertices[N];
            Vector3 probe = Vector3.Cross(p01 - p00, p10 - p00);
            bool keep = Vector3.Dot(probe, new Vector3(p00.x, p00.y, 0f)) > 0f;

            int t = 0;
            for (int r = 0; r < rows - 1; r++)
            {
                for (int i = 0; i < N; i++)
                {
                    int i1 = (i + 1) % N;
                    int a = r * N + i, b = r * N + i1, c = (r + 1) * N + i, d = (r + 1) * N + i1;
                    if (keep)
                    {
                        triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                        triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
                    }
                    else
                    {
                        triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                        triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                    }
                }
            }

            // smooth normals: area-weighted sum of the face normals around each vertex
            for (int k = 0; k < triangles.Length; k += 3)
            {
                int a = triangles[k], b = triangles[k + 1], c = triangles[k + 2];
                Vector3 fn = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                normals[a] += fn; normals[b] += fn; normals[c] += fn;
            }
            for (int v = 0; v < normals.Length; v++)
            {
                float m = normals[v].magnitude;
                normals[v] = m > 1e-12f ? normals[v] / m : Vector3.up;
            }
        }
    }
}
