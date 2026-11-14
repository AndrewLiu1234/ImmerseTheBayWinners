// CapsuleHandMesh: the vertex data of the capsule hand. Plain C# (arrays of Vector3 / Vector4 / Color32 / int only): no
// Mesh, no Unity object, so it runs and is tested outside the editor. CapsuleHand copies these arrays into one Mesh.
//
// ONE MESH FOR THE WHOLE HAND (GhostHandGeometry)
//   The ghost shader first writes the depth of the whole mesh and then adds colour only where a surface is the front-most
//   one. That only makes "one hand" out of the parts if they are all in the same mesh, so the palm and the five fingers
//   share one vertex buffer. The topology never changes: a pose change rewrites positions and normals in place, a change
//   of brightness rewrites one small per-vertex control value. Nothing is allocated after construction.
//
// A FINGER IS ONE SKIN, NOT THREE CAPSULES (GhostTubeGeometry)
//   It is exactly the OUTSIDE of the union of the finger's capsules:
//     * straight (slightly tapered) tube sections along the bones,
//     * at every joint, on the outside of the bend, the wedge of the joint sphere that the bend opens up,
//     * on the inside of the bend, the crease where the two tubes run into each other (nothing is drawn past it),
//     * a hemisphere at the fingertip. The start is open; it sits hidden inside the palm.
//   So the normals are continuous along the whole finger and the rim light never breaks at a joint.
//
// PER-VERTEX DATA (the GhostHand shader's conventions)
//   colour   red = fingertip glow, blue = fade out (the forearm end), green and alpha unused
//   control  (TEXCOORD0, only read by the shader when _VertexControl is 1):
//            x = dim     0 = full brightness .. 1 = off      (finger-by-finger emphasis)
//            y = hollow  0 = normal fill .. 1 = rim only      (the palm and wrist are drawn lighter than the fingers)
//            z = tipOff  0 = the fingertip glows .. 1 = no glow   (only fingers that press a string glow)
//            w = slot    the fingertip colour: 0 = the shader's _TipColor, 1-4 = _TipColor1-4 (fingers 1-4)
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>A tube swept along a short polyline (one finger): open start, rounded bends, hemispherical tip.</summary>
    public sealed class GhostTubeGeometry
    {
        /// <summary>The crease on the inside of a bend may use up at most this fraction of each neighbouring bone.</summary>
        public const float MaxCreaseFraction = 0.40f;
        const float MaxBend = 2.9f;                 // radians; beyond this the crease distance is clamped anyway

        public readonly int pointCount;             // centre-line points: start, one per joint, fingertip centre
        public readonly int around;                 // vertices around the tube
        public readonly int elbowSteps;             // rings spanning the outside of each bend, minus one
        public readonly int capRows;                // rings of the fingertip hemisphere (the pole is one extra vertex)
        public readonly int startRows;              // rings along the first part of the first section
        public readonly float startFraction;        // those rings cover this fraction of the first section
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

        /// <param name="fadeStart">true = the start rings fade in from nothing (blue vertex colour); false = solid from the start.</param>
        public GhostTubeGeometry(int pointCount, int around, int elbowSteps, int capRows, int startRows, float startFraction, float tipGlowStart, bool fadeStart)
        {
            this.pointCount = Mathf.Max(pointCount, 3);
            this.around = Mathf.Max(around, 6);
            this.elbowSteps = Mathf.Max(elbowSteps, 1);
            this.capRows = Mathf.Max(capRows, 2);
            this.startRows = Mathf.Max(startRows, 2);
            this.startFraction = Mathf.Clamp(startFraction, 0.05f, 1f - MaxCreaseFraction - 0.05f);
            this.tipGlowStart = Mathf.Clamp(tipGlowStart, MaxCreaseFraction + 0.05f, 0.95f);

            int n = this.pointCount, N = this.around, K = this.elbowSteps, M = this.capRows, F = this.startRows;
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

            // colours: the tip glows (red); optionally the start fades in (blue = fade out)
            for (int row = 0; row < rowCount; row++)
            {
                byte fadeOut = 0, tip = 0;
                if (fadeStart && row < F)
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
        /// Rewrite the vertices and normals for a centre line. points[0] is where the skin starts, points[1 .. n-2] are the
        /// joints, points[n-1] is the fingertip centre; radii[i] is the thickness at points[i]. upHint fixes how the ring is
        /// turned about the first section (any direction not parallel to it). No allocation.
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
            for (int f = 0; f < startRows; f++)
            {
                float g = startFraction * f / (startRows - 1);
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

    /// <summary>The palm: a rounded slab lofted through CapsuleHandDimensions.palm. The forearm end fades out and is open;
    /// the far end is closed and follows the arc of the knuckles, so the fingers grow out of it. Built once.</summary>
    public static class GhostPalmGeometry
    {
        // how the closed end is rounded off: distance past the knuckle arc, and the thickness left there
        static readonly float[] CapAhead = { 0.0045f, 0.0075f, 0.0090f };
        static readonly float[] CapThickness = { 0.80f, 0.48f, 0.0f };

        /// <param name="hollow">per vertex, 0..1: how much of the fill to leave out (the palm and wrist are drawn lighter).</param>
        public static void Build(CapsuleHandDimensions dims, int around,
            out Vector3[] vertices, out Vector3[] normals, out Color32[] colors, out float[] hollow, out int[] triangles)
        {
            int N = Mathf.Max(around, 8);
            CapsuleHandDimensions.PalmStation[] st = dims.palm;
            int stations = st.Length;
            int rows = stations + CapAhead.Length;
            vertices = new Vector3[rows * N];
            normals = new Vector3[rows * N];
            colors = new Color32[rows * N];
            hollow = new float[rows * N];
            triangles = new int[(rows - 1) * N * 6];

            float power = 2f / Mathf.Max(dims.palmExponent, 1f);
            float zEnd = st[stations - 1].z;
            float zBend = dims.palmArcStart;
            for (int r = 0; r < rows; r++)
            {
                int s = Mathf.Min(r, stations - 1);
                float thickness = r < stations ? 1f : CapThickness[r - stations];
                float ahead = r < stations ? 0f : CapAhead[r - stations];
                byte fadeOut = (byte)Mathf.RoundToInt((1f - Mathf.Clamp01(st[s].alpha)) * 255f);
                for (int i = 0; i < N; i++)
                {
                    // half a step off the sides, so no vertex lies on the plane where the closed end folds flat
                    // (a vertex there on every row would make flat slivers in that plane)
                    float phi = 2f * Mathf.PI * (i + 0.5f) / N;
                    float c = Mathf.Cos(phi), sn = Mathf.Sin(phi);
                    float x = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), power) * st[s].halfWidth;
                    float y = Mathf.Sign(sn) * Mathf.Pow(Mathf.Abs(sn), power) * st[s].halfThickness * thickness;
                    // past the middle of the palm the rings lean so that the last one lies along the arc of the knuckles
                    float z = st[s].z;
                    if (z > zBend) z = zBend + (z - zBend) * (dims.KnuckleArc(x) - zBend) / (zEnd - zBend);
                    vertices[r * N + i] = new Vector3(x, y, z + ahead);
                    colors[r * N + i] = new Color32(0, 0, fadeOut, 255);
                    hollow[r * N + i] = st[s].hollow;
                }
            }

            // Winding: pick the order whose face normal points away from the palm's axis.
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

    /// <summary>The whole hand as one vertex buffer: the palm, then fingers 1-4, then the thumb. Palm space, scale 1.</summary>
    public sealed class GhostHandGeometry
    {
        public const int PartCount = 6;         // 0 = palm, 1-4 = fingers 1-4 (index..little), 5 = thumb  (part = finger index + 1)
        public const int ElbowSteps = 4;
        public const int CapRows = 4;
        public const int StartRows = 2;

        public readonly Vector3[] vertices;
        public readonly Vector3[] normals;
        public readonly Color32[] colors;       // static
        public readonly Vector4[] control;      // (dim, hollow, tipOff, colour slot): see the top of this file
        public readonly int[] triangles;        // static
        public readonly int[] partStart = new int[PartCount];
        public readonly int[] partCount = new int[PartCount];

        readonly CapsuleHandDimensions m_dims;
        readonly GhostTubeGeometry[] m_tubes = new GhostTubeGeometry[CapsuleHandDimensions.FingerCount];
        readonly Vector3[] m_points = new Vector3[5];
        readonly float[] m_radii = new float[5];

        public int VertexCount { get { return vertices.Length; } }
        public int TriangleCount { get { return triangles.Length / 3; } }

        public GhostHandGeometry(CapsuleHandDimensions dims, int fingerAround, int palmAround)
        {
            m_dims = dims;
            Vector3[] pv; Vector3[] pn; Color32[] pc; float[] ph; int[] pt;
            GhostPalmGeometry.Build(dims, palmAround, out pv, out pn, out pc, out ph, out pt);

            int vertexTotal = pv.Length, indexTotal = pt.Length;
            for (int f = 0; f < CapsuleHandDimensions.FingerCount; f++)
            {
                bool thumb = f == CapsuleHandDimensions.ThumbIndex;
                m_tubes[f] = new GhostTubeGeometry(thumb ? 4 : 5, fingerAround, ElbowSteps, CapRows, StartRows, 0.5f, 0.5f, false);
                vertexTotal += m_tubes[f].vertexCount;
                indexTotal += m_tubes[f].triangles.Length;
            }

            vertices = new Vector3[vertexTotal];
            normals = new Vector3[vertexTotal];
            colors = new Color32[vertexTotal];
            control = new Vector4[vertexTotal];
            triangles = new int[indexTotal];

            System.Array.Copy(pv, vertices, pv.Length);
            System.Array.Copy(pn, normals, pn.Length);
            System.Array.Copy(pc, colors, pc.Length);
            System.Array.Copy(pt, triangles, pt.Length);
            for (int i = 0; i < pv.Length; i++) control[i] = new Vector4(0f, ph[i], 1f, 0f);    // the palm never glows like a fingertip
            partStart[0] = 0; partCount[0] = pv.Length;

            int v = pv.Length, t = pt.Length;
            for (int f = 0; f < CapsuleHandDimensions.FingerCount; f++)
            {
                GhostTubeGeometry tube = m_tubes[f];
                partStart[f + 1] = v; partCount[f + 1] = tube.vertexCount;
                System.Array.Copy(tube.colors, 0, colors, v, tube.vertexCount);
                float slot = f == CapsuleHandDimensions.ThumbIndex ? 0f : f + 1;    // fingers 1-4 may get their own tip colour
                for (int i = 0; i < tube.vertexCount; i++) control[v + i] = new Vector4(0f, 0f, 0f, slot);
                for (int k = 0; k < tube.triangles.Length; k++) triangles[t + k] = tube.triangles[k] + v;
                v += tube.vertexCount; t += tube.triangles.Length;
            }

            // a first shape, so the buffers never hold a collapsed finger: every finger in its relaxed curl
            FingerIkResult r;
            for (int f = 0; f < CapsuleHandDimensions.FingerCount; f++)
            {
                FingerIk.Pose(dims.chains[f], 0f, dims.relaxedMcp[f], dims.relaxedPip[f], out r);
                SetFinger(f, r.knuckle, r.pip, r.dip, r.tip);
            }
        }

        /// <summary>Redraw one finger (0 = index .. 3 = little, 4 = thumb) through its four joint positions (palm space).</summary>
        public void SetFinger(int finger, Vector3 knuckle, Vector3 pip, Vector3 dip, Vector3 tip)
        {
            CapsuleHandDimensions dims = m_dims;
            Vector3 upHint;
            if (finger == CapsuleHandDimensions.ThumbIndex)
            {
                m_points[0] = knuckle; m_points[1] = pip; m_points[2] = dip; m_points[3] = tip;
                m_radii[0] = dims.Radius(finger, 0); m_radii[1] = dims.Radius(finger, 1); m_radii[2] = dims.Radius(finger, 2); m_radii[3] = dims.Radius(finger, 3);
                upHint = -dims.chains[finger].curl;
            }
            else
            {
                m_points[0] = dims.skinStart[finger]; m_points[1] = knuckle; m_points[2] = pip; m_points[3] = dip; m_points[4] = tip;
                m_radii[0] = dims.skinStartRadius[finger]; m_radii[1] = dims.Radius(finger, 0); m_radii[2] = dims.Radius(finger, 1);
                m_radii[3] = dims.Radius(finger, 2); m_radii[4] = dims.Radius(finger, 3);
                upHint = Vector3.up;
            }
            GhostTubeGeometry tube = m_tubes[finger];
            tube.Update(m_points, m_radii, upHint);
            int start = partStart[finger + 1];
            System.Array.Copy(tube.vertices, 0, vertices, start, tube.vertexCount);
            System.Array.Copy(tube.normals, 0, normals, start, tube.vertexCount);
        }

        /// <summary>Brightness of one part (0 = palm, 1-4 = fingers, 5 = thumb): dim 0 = full .. 1 = off; tipGlow 1 = the
        /// fingertip glows .. 0 = it does not. Writes the control values of that part's vertices.</summary>
        public void SetPartLook(int part, float dim, float tipGlow)
        {
            float d = Mathf.Clamp01(dim), off = 1f - Mathf.Clamp01(tipGlow);
            int start = partStart[part], end = start + partCount[part];
            for (int i = start; i < end; i++)
            {
                Vector4 c = control[i];
                c.x = d;
                c.z = off;
                control[i] = c;
            }
        }
    }

    /// <summary>The look rules of the capsule hand as plain functions (so they are tested without Unity).
    /// Parts: 0 = palm, 1-4 = fingers 1-4 (index .. little), 5 = thumb.</summary>
    public static class GhostHandLook
    {
        /// <summary>Brightness of a part: 1 when the whole hand is lit (activeFinger outside 1-4) or the part is the active
        /// finger; every other part (the palm and the thumb too) at <paramref name="dimmed"/>.</summary>
        public static float PartEmphasis(int part, int activeFinger, float dimmed)
        {
            bool wholeHand = activeFinger < 1 || activeFinger > 4;
            return wholeHand || part == activeFinger ? 1f : Mathf.Clamp01(dimmed);
        }

        /// <summary>Does the fingertip of a part glow? Only fingers 1-4 of a fretting hand, and of those only the ones that
        /// press a string (used[finger]) or the active finger. The thumb, the palm and a free (strumming) hand never glow:
        /// where to press is the brightest thing in view, and nothing else is.</summary>
        public static bool PartTipGlows(int part, bool fretting, bool[] used, int activeFinger)
        {
            if (!fretting || part < 1 || part > 4) return false;
            if (part == activeFinger) return true;
            int finger = part - 1;
            return used != null && finger < used.Length && used[finger];
        }
    }
}
