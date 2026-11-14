// NeckGeometry: a fixed-size buffer of quads in the vertex format the NeckOverlay shader reads. It is filled by
// NeckMeshBuilder (pure code) and uploaded to a Mesh by FloatingNeck. Allocated once; filling it never allocates.
//
// Vertex format (see Resources/Shaders/NeckOverlay.shader):
//   POSITION   neck-local metres
//   COLOR      the colour, already multiplied by its brightness (black = invisible on the additive display)
//   TEXCOORD0  xy = position inside the quad, -1..1;  z = 0 soft-edged rectangle (lines), 1 soft-edged disc or ring;
//              w = inner radius of a ring as a fraction of the outer radius (negative = filled disc)
//   TEXCOORD1  x = gain group (0 = fixed brightness; 1-12 pick a component of the material's _GainA/_GainB/_GainC);
//              y = edge softness, 0..1 of the half width or radius (0 = one-pixel anti-aliasing only)
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck
{
    public sealed class NeckGeometry
    {
        public const float ShapeRect = 0f;
        public const float ShapeDisc = 1f;

        /// <summary>Inner radius that means "no hole": a filled disc.</summary>
        public const float Filled = -1f;

        // Gain groups: which brightness uniform a quad follows (see FloatingNeck.SetActiveFinger and SetStringMarks).
        public const int GroupFixed = 0;
        public const int GroupCount = 13;

        /// <summary>Group of everything belonging to a finger (0 = a dot without a finger number, 1-4).</summary>
        public static int FingerGroup(int finger)
        {
            return 1 + Mathf.Clamp(finger, 0, 4);
        }

        /// <summary>Group of one string's line (strings 1-6).</summary>
        public static int StringGroup(int stringNumber)
        {
            return 5 + Mathf.Clamp(stringNumber, 1, NeckLayout.StringCount);
        }

        public readonly Vector3[] positions;
        public readonly Color32[] colors;
        public readonly Vector4[] shape;     // TEXCOORD0
        public readonly Vector4[] extra;     // TEXCOORD1
        public readonly int[] triangles;     // two triangles per quad, written once: quad q uses vertices 4q..4q+3

        public NeckGeometry(int quadCapacity)
        {
            QuadCapacity = quadCapacity;
            positions = new Vector3[quadCapacity * 4];
            colors = new Color32[quadCapacity * 4];
            shape = new Vector4[quadCapacity * 4];
            extra = new Vector4[quadCapacity * 4];
            triangles = new int[quadCapacity * 6];
            for (int q = 0; q < quadCapacity; q++)
            {
                int v = q * 4, t = q * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }
        }

        public int QuadCapacity { get; private set; }
        public int QuadCount { get; private set; }

        /// <summary>Quads that did not fit since the last Clear (0 in normal use; the caller logs it).</summary>
        public int Dropped { get; private set; }

        public int VertexCount { get { return QuadCount * 4; } }
        public int IndexCount { get { return QuadCount * 6; } }

        public void Clear()
        {
            QuadCount = 0;
            Dropped = 0;
        }

        /// <summary>
        /// A straight line as a flat ribbon from <paramref name="from"/> to <paramref name="to"/> (neck-local metres).
        /// <paramref name="halfWidth"/> is the vector from the centre line to one edge. The colour may differ at the two ends
        /// (fading to black fades the line out). A capped end gets an anti-aliased edge; leave ends that touch another segment
        /// uncapped so the joint shows no seam.
        /// </summary>
        public void Line(Vector3 from, Vector3 to, Vector3 halfWidth, Color32 colorFrom, Color32 colorTo, bool capFrom, bool capTo, int group, float softness)
        {
            if (QuadCount >= QuadCapacity) { Dropped++; return; }
            int v = QuadCount * 4;
            float uFrom = capFrom ? -1f : 0f;
            float uTo = capTo ? 1f : 0f;
            positions[v] = from - halfWidth;
            positions[v + 1] = to - halfWidth;
            positions[v + 2] = to + halfWidth;
            positions[v + 3] = from + halfWidth;
            colors[v] = colorFrom;
            colors[v + 1] = colorTo;
            colors[v + 2] = colorTo;
            colors[v + 3] = colorFrom;
            shape[v] = new Vector4(uFrom, -1f, ShapeRect, Filled);
            shape[v + 1] = new Vector4(uTo, -1f, ShapeRect, Filled);
            shape[v + 2] = new Vector4(uTo, 1f, ShapeRect, Filled);
            shape[v + 3] = new Vector4(uFrom, 1f, ShapeRect, Filled);
            Vector4 e = new Vector4(group, softness, 0f, 0f);
            extra[v] = e;
            extra[v + 1] = e;
            extra[v + 2] = e;
            extra[v + 3] = e;
            QuadCount++;
        }

        /// <summary>
        /// A soft-edged disc (or a ring, when <paramref name="innerRadius01"/> is above 0) centred on <paramref name="centre"/>.
        /// <paramref name="radiusU"/> and <paramref name="radiusV"/> are the two radius vectors that span its plane.
        /// </summary>
        public void Disc(Vector3 centre, Vector3 radiusU, Vector3 radiusV, Color32 color, float innerRadius01, int group, float softness)
        {
            if (QuadCount >= QuadCapacity) { Dropped++; return; }
            int v = QuadCount * 4;
            positions[v] = centre - radiusU - radiusV;
            positions[v + 1] = centre + radiusU - radiusV;
            positions[v + 2] = centre + radiusU + radiusV;
            positions[v + 3] = centre - radiusU + radiusV;
            colors[v] = color;
            colors[v + 1] = color;
            colors[v + 2] = color;
            colors[v + 3] = color;
            shape[v] = new Vector4(-1f, -1f, ShapeDisc, innerRadius01);
            shape[v + 1] = new Vector4(1f, -1f, ShapeDisc, innerRadius01);
            shape[v + 2] = new Vector4(1f, 1f, ShapeDisc, innerRadius01);
            shape[v + 3] = new Vector4(-1f, 1f, ShapeDisc, innerRadius01);
            Vector4 e = new Vector4(group, softness, 0f, 0f);
            extra[v] = e;
            extra[v + 1] = e;
            extra[v + 2] = e;
            extra[v + 3] = e;
            QuadCount++;
        }
    }
}
