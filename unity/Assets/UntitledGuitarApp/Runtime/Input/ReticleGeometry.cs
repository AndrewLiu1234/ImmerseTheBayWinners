// ReticleGeometry: the reticle's mesh as plain arrays, with no scene or GameObject. Reticle (the MonoBehaviour) owns a
// Mesh and copies these arrays into it. Keeping the index arithmetic here means it runs in EditMode tests.
//
// Layout, all in the local XY plane (z = 0), in metres, for a reticle that sits `distanceM` in front of the head:
//   ring : a thin annulus, 48 segments  (always on, dim until a target is focused)
//   arc  : an annular sector, 64 segments, from 12 o'clock clockwise (as seen by the wearer) up to `progress` of a turn.
//          Vertices beyond the sweep collapse onto its end, so the index buffer never changes and the unused part has
//          zero area (draws nothing).
//   dot  : a tiny disc, 12 segments, marks the exact gaze ray. The ring's middle stays open so the target stays visible.
// Angular sizes are converted with radius = distance * tan(angle), so the reticle keeps one angular size at any distance.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Gaze
{
    public sealed class ReticleGeometry
    {
        public const int RingSegments = 48;
        public const int ArcSegments = 64;
        public const int DotSegments = 12;

        public const int RingFirstVertex = 0;
        public const int RingVertexCount = RingSegments * 2;                       // inner, outer per step; the ring wraps
        public const int ArcFirstVertex = RingFirstVertex + RingVertexCount;
        public const int ArcVertexCount = (ArcSegments + 1) * 2;                   // inner, outer per step; the arc does not wrap
        public const int DotFirstVertex = ArcFirstVertex + ArcVertexCount;
        public const int DotVertexCount = DotSegments + 1;                         // centre + rim
        public const int VertexCount = DotFirstVertex + DotVertexCount;
        public const int TriangleCount = RingSegments * 2 + ArcSegments * 2 + DotSegments;

        const float TwoPi = 2f * Mathf.PI;
        const float MaxAngleDeg = 80f;   // keeps tan() finite and positive

        /// <summary>Vertex positions (local space, z = 0). Reticle copies them into the Mesh.</summary>
        public readonly Vector3[] Vertices = new Vector3[VertexCount];

        /// <summary>Per-vertex colour; alpha is brightness for the additive shader.</summary>
        public readonly Color32[] Colors = new Color32[VertexCount];

        /// <summary>Triangle list, clockwise as seen by the wearer. Never changes after construction.</summary>
        public readonly int[] Triangles = new int[TriangleCount * 3];

        float arcInnerRadius;
        float arcOuterRadius;
        float arcProgress;

        public ReticleGeometry()
        {
            BuildTriangles();
            for (int i = 0; i < Colors.Length; i++) Colors[i] = new Color32(255, 255, 255, 255);
            SetShape(1f, 0.75f, 0.12f, 0.30f, 0.08f);
        }

        /// <summary>Radius in metres of the largest part (ring, arc or dot); handy for mesh bounds.</summary>
        public float OuterRadius { get; private set; }

        /// <summary>Radius in metres of the ring's inner edge; the open area inside it is never covered except by the dot.</summary>
        public float RingInnerRadius { get; private set; }

        /// <summary>Radius in metres of the centre dot.</summary>
        public float DotRadius { get; private set; }

        /// <summary>The sweep currently built into the arc, 0..1.</summary>
        public float Progress { get { return arcProgress; } }

        static float Radius(float distanceM, float angleDeg)
        {
            if (!(angleDeg > 0f)) return 0f;
            return GazeMath.AngularRadiusToWorld(distanceM, angleDeg > MaxAngleDeg ? MaxAngleDeg : angleDeg);
        }

        /// <summary>
        /// Positions the ring, dot and the arc's band. The ring is centred on <paramref name="ringRadiusDeg"/> and is
        /// <paramref name="ringWidthDeg"/> thick; the arc covers the same radius but is <paramref name="arcWidthDeg"/> thick, so
        /// it shows as a bolder band over the ring while a dwell fills it. All angles are as seen from the head.
        /// </summary>
        public void SetShape(float distanceM, float ringRadiusDeg, float ringWidthDeg, float arcWidthDeg, float dotRadiusDeg)
        {
            float ringHalf = (ringWidthDeg > 0f ? ringWidthDeg : 0f) * 0.5f;
            float arcHalf = (arcWidthDeg > 0f ? arcWidthDeg : 0f) * 0.5f;
            float ringInner = Radius(distanceM, ringRadiusDeg - ringHalf);
            float ringOuter = Radius(distanceM, ringRadiusDeg + ringHalf);
            float dot = Radius(distanceM, dotRadiusDeg);
            arcInnerRadius = Radius(distanceM, ringRadiusDeg - arcHalf);
            arcOuterRadius = Radius(distanceM, ringRadiusDeg + arcHalf);

            float ringStep = TwoPi / RingSegments;
            for (int k = 0; k < RingSegments; k++)
            {
                float s = Mathf.Sin(k * ringStep), c = Mathf.Cos(k * ringStep);
                Vertices[RingFirstVertex + 2 * k] = new Vector3(s * ringInner, c * ringInner, 0f);
                Vertices[RingFirstVertex + 2 * k + 1] = new Vector3(s * ringOuter, c * ringOuter, 0f);
            }

            Vertices[DotFirstVertex] = Vector3.zero;
            float dotStep = TwoPi / DotSegments;
            for (int k = 0; k < DotSegments; k++)
            {
                Vertices[DotFirstVertex + 1 + k] = new Vector3(Mathf.Sin(k * dotStep) * dot, Mathf.Cos(k * dotStep) * dot, 0f);
            }

            RingInnerRadius = ringInner;
            DotRadius = dot;
            OuterRadius = Mathf.Max(ringOuter, Mathf.Max(arcOuterRadius, dot));
            RefreshArc();
        }

        /// <summary>
        /// Sets how much of the arc is filled (0..1; negative and NaN count as 0, above 1 counts as 1). Returns true if the
        /// vertices changed, so the caller only re-uploads the mesh when it has to.
        /// </summary>
        public bool SetProgress(float progress01)
        {
            float p = progress01 > 0f ? (progress01 < 1f ? progress01 : 1f) : 0f;
            if (p == arcProgress) return false;
            arcProgress = p;
            RefreshArc();
            return true;
        }

        /// <summary>Colours the ring and the centre dot.</summary>
        public void SetRingColor(Color32 color)
        {
            for (int i = RingFirstVertex; i < RingFirstVertex + RingVertexCount; i++) Colors[i] = color;
            for (int i = DotFirstVertex; i < DotFirstVertex + DotVertexCount; i++) Colors[i] = color;
        }

        /// <summary>Colours the progress arc.</summary>
        public void SetArcColor(Color32 color)
        {
            for (int i = ArcFirstVertex; i < ArcFirstVertex + ArcVertexCount; i++) Colors[i] = color;
        }

        void RefreshArc()
        {
            float sweep = arcProgress * TwoPi;
            float step = TwoPi / ArcSegments;
            for (int k = 0; k <= ArcSegments; k++)
            {
                float a = k * step;
                if (a > sweep) a = sweep;   // vertices past the sweep collapse onto its end: zero area, nothing drawn
                float s = Mathf.Sin(a), c = Mathf.Cos(a);
                Vertices[ArcFirstVertex + 2 * k] = new Vector3(s * arcInnerRadius, c * arcInnerRadius, 0f);
                Vertices[ArcFirstVertex + 2 * k + 1] = new Vector3(s * arcOuterRadius, c * arcOuterRadius, 0f);
            }
        }

        void BuildTriangles()
        {
            int n = 0;
            for (int k = 0; k < RingSegments; k++)
            {
                int kNext = (k + 1) % RingSegments;
                int inner0 = RingFirstVertex + 2 * k, outer0 = inner0 + 1;
                int inner1 = RingFirstVertex + 2 * kNext, outer1 = inner1 + 1;
                Triangles[n++] = inner0; Triangles[n++] = outer0; Triangles[n++] = inner1;
                Triangles[n++] = outer0; Triangles[n++] = outer1; Triangles[n++] = inner1;
            }
            for (int k = 0; k < ArcSegments; k++)
            {
                int inner0 = ArcFirstVertex + 2 * k, outer0 = inner0 + 1;
                int inner1 = inner0 + 2, outer1 = inner0 + 3;
                Triangles[n++] = inner0; Triangles[n++] = outer0; Triangles[n++] = inner1;
                Triangles[n++] = outer0; Triangles[n++] = outer1; Triangles[n++] = inner1;
            }
            int centre = DotFirstVertex;
            for (int k = 0; k < DotSegments; k++)
            {
                Triangles[n++] = centre;
                Triangles[n++] = centre + 1 + k;
                Triangles[n++] = centre + 1 + (k + 1) % DotSegments;
            }
        }
    }
}
