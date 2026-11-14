// NeckPlacement: where the floating neck hangs in front of the player, and how it is mirrored for a left-handed player.
// Pure static maths (only Vector3 / Quaternion arithmetic), unit-tested in NeckTests.cs.
//
// Head space is Unity's: +x right, +y up, +z forward (the way the player looks).
//
// HANDEDNESS. For a right-handed player the pinned neck frame (+x nut to bridge, +y out of the fretboard, +z toward
// string 1) is an ordinary Unity frame: seen from above with the nut on the left, x runs right, y comes toward the eye and
// z runs up the picture, and Unity's cross(x, y) is exactly that z. A left-handed guitar is the mirror image: x and y are
// still the physical nut-to-bridge direction and fretboard normal, but string 1 is on the other side. So a pose is a proper
// rotation (which carries x and y) plus a scale whose z is negative for a left-handed player. Neck-local data never changes.
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>Where the floating neck hangs, relative to the head. MASTER_PLAN section 17 pins the ranges.</summary>
    [Serializable]
    public struct FloatingPlacement
    {
        /// <summary>Eye to nut, in metres. 0.8 to 1.0.</summary>
        public float distanceM;

        /// <summary>Direction of the nut, degrees to the right of straight ahead (negative = left). -20 to -5: lower-left of view.</summary>
        public float azimuthDeg;

        /// <summary>Direction of the nut, degrees above straight ahead (negative = below). -12 to -1.</summary>
        public float elevationDeg;

        /// <summary>
        /// How far the fretboard is tipped back from facing the player squarely, in degrees: 0 = face-on like a chord diagram,
        /// larger = seen more from above, with the far (high E) edge further away. 0 to 60.
        /// </summary>
        public float tiltDeg;

        /// <summary>Size relative to the real neck. 1.0 to 1.2.</summary>
        public float scale;

        /// <summary>
        /// 0.9 m ahead; the nut 14 degrees left and 4 degrees down, so frets 0 to 5 span about -14 to -1.5 degrees and the ghost
        /// hand's wrist still fits above the bottom edge of the 27 degree tall view; tipped back 25 degrees; 1.2 times life size.
        /// </summary>
        public static FloatingPlacement Default
        {
            get
            {
                return new FloatingPlacement { distanceM = 0.9f, azimuthDeg = -14f, elevationDeg = -4f, tiltDeg = 25f, scale = 1.2f };
            }
        }

        /// <summary>The same placement with every value forced into its pinned range (NaN becomes the default).</summary>
        public FloatingPlacement Clamped()
        {
            FloatingPlacement d = Default;
            return new FloatingPlacement
            {
                distanceM = ClampOr(distanceM, 0.8f, 1.0f, d.distanceM),
                azimuthDeg = ClampOr(azimuthDeg, -20f, -5f, d.azimuthDeg),
                elevationDeg = ClampOr(elevationDeg, -12f, -1f, d.elevationDeg),
                tiltDeg = ClampOr(tiltDeg, 0f, 60f, d.tiltDeg),
                scale = ClampOr(scale, 1.0f, 1.2f, d.scale),
            };
        }

        static float ClampOr(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return Mathf.Clamp(value, min, max);
        }
    }

    /// <summary>
    /// A neck pose in some parent space (the head's, or the world's): where the nut is, which way the neck points, and its
    /// scale. <see cref="scale"/>.z is negative for a left-handed player (see the file header).
    /// </summary>
    public struct NeckPose
    {
        /// <summary>The nut: the neck frame's origin.</summary>
        public Vector3 position;

        /// <summary>Carries the neck's +x (nut to bridge) and +y (out of the fretboard).</summary>
        public Quaternion rotation;

        /// <summary>(s, s, s) for a right-handed player, (s, s, -s) for a left-handed one.</summary>
        public Vector3 scale;

        public NeckPose(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
        }

        /// <summary>True when this pose mirrors the neck (left-handed).</summary>
        public bool IsMirrored
        {
            get { return scale.z < 0f; }
        }

        /// <summary>A point given in neck-local metres, in the pose's parent space.</summary>
        public Vector3 TransformPoint(Vector3 neckLocalMetres)
        {
            return position + rotation * Vector3.Scale(scale, neckLocalMetres);
        }

        /// <summary>A direction given in the neck frame (not scaled, but mirrored when the pose is), in the pose's parent space.</summary>
        public Vector3 TransformDirection(Vector3 neckLocalDirection)
        {
            if (IsMirrored) neckLocalDirection.z = -neckLocalDirection.z;
            return rotation * neckLocalDirection;
        }

        /// <summary>A point of the parent space, in neck-local metres.</summary>
        public Vector3 InverseTransformPoint(Vector3 parentSpacePoint)
        {
            Quaternion inverse = new Quaternion(-rotation.x, -rotation.y, -rotation.z, rotation.w);   // conjugate = inverse of a unit quaternion
            Vector3 p = inverse * (parentSpacePoint - position);
            return new Vector3(SafeDivide(p.x, scale.x), SafeDivide(p.y, scale.y), SafeDivide(p.z, scale.z));
        }

        static float SafeDivide(float a, float b)
        {
            return Mathf.Abs(b) > 1e-9f ? a / b : 0f;
        }
    }

    public static class NeckPlacement
    {
        /// <summary>
        /// The floating neck's pose in head space: <see cref="FloatingPlacement.distanceM"/> ahead, lower-left of view, oriented
        /// like the player's own neck seen from above (nut on the left, low E nearest the player). For a left-handed player it
        /// is the mirror image in the head's left-right plane: nut on the right, low E still nearest.
        /// </summary>
        public static NeckPose Floating(FloatingPlacement placement, bool leftHanded)
        {
            FloatingPlacement p = placement.Clamped();
            float azimuth = p.azimuthDeg * Mathf.Deg2Rad;
            float elevation = p.elevationDeg * Mathf.Deg2Rad;
            float tilt = p.tiltDeg * Mathf.Deg2Rad;

            float cosElevation = Mathf.Cos(elevation);
            Vector3 nut = new Vector3(Mathf.Sin(azimuth) * cosElevation, Mathf.Sin(elevation), Mathf.Cos(azimuth) * cosElevation) * p.distanceM;

            // Nut to bridge runs to the player's right. Face-on, the fretboard normal would point straight back at the player
            // (0, 0, -1); tipping it back by the tilt raises the normal, which lowers the near (low E) edge and pushes the far
            // (high E) edge up and away: the neck seen from above.
            Vector3 alongNeck = new Vector3(1f, 0f, 0f);
            Vector3 outOfFretboard = new Vector3(0f, Mathf.Sin(tilt), -Mathf.Cos(tilt));

            NeckPose pose = new NeckPose(nut, RotationFromAxes(alongNeck, outOfFretboard), new Vector3(p.scale, p.scale, p.scale));
            return leftHanded ? MirrorLeftRight(pose) : pose;
        }

        /// <summary>
        /// The mirror image of a pose in the parent's x = 0 plane (the head's left-right mirror). Every point of the neck lands
        /// at (-x, y, z) of where it was. The result keeps the convention of the file header: its rotation carries the mirrored
        /// neck's real +x and +y, and the reflection itself is the sign of scale.z. Mirroring twice gives the original pose.
        /// </summary>
        public static NeckPose MirrorLeftRight(NeckPose pose)
        {
            Vector3 x = pose.rotation * new Vector3(1f, 0f, 0f);
            Vector3 y = pose.rotation * new Vector3(0f, 1f, 0f);
            x.x = -x.x;
            y.x = -y.x;
            return new NeckPose(
                new Vector3(-pose.position.x, pose.position.y, pose.position.z),
                RotationFromAxes(x, y),
                new Vector3(pose.scale.x, pose.scale.y, -pose.scale.z));
        }

        /// <summary>The neck transform's local scale: (s, s, s), or (s, s, -s) when mirrored for a left-handed player.</summary>
        public static Vector3 ScaleFor(float scale, bool leftHanded)
        {
            return new Vector3(scale, scale, leftHanded ? -scale : scale);
        }

        /// <summary>A pose given in head space, as a world position and rotation (the scale is unchanged).</summary>
        public static void ToWorld(Vector3 headPosition, Quaternion headRotation, NeckPose inHeadSpace, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            worldPosition = headPosition + headRotation * inHeadSpace.position;
            worldRotation = headRotation * inHeadSpace.rotation;
        }

        /// <summary>
        /// The rotation that turns +x onto <paramref name="xAxis"/> and +y as near as possible onto <paramref name="yAxis"/>
        /// (+z then lands on Unity's cross(x, y)). Plain arithmetic, so it is exact, deterministic and testable without the engine.
        /// </summary>
        public static Quaternion RotationFromAxes(Vector3 xAxis, Vector3 yAxis)
        {
            Vector3 x = xAxis.normalized;
            Vector3 z = Vector3.Cross(x, yAxis).normalized;
            if (x.sqrMagnitude < 0.5f || z.sqrMagnitude < 0.5f) return Quaternion.identity;   // degenerate input: zero or parallel axes
            Vector3 y = Vector3.Cross(z, x);

            // Rotation matrix with columns x, y, z -> quaternion (Shepperd's method: divide by the largest component).
            float m00 = x.x, m01 = y.x, m02 = z.x;
            float m10 = x.y, m11 = y.y, m12 = z.y;
            float m20 = x.z, m21 = y.z, m22 = z.z;
            float trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length > 1e-6f ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }

        /// <summary>
        /// The local rotation of a finger-number label so that it reads along the neck and faces the viewer: it hinges about
        /// the neck's x axis only. Both points are in neck-local metres. The label's own +x stays on the neck's +x (a mirrored
        /// neck flips the label's x scale instead), its +y points toward the string 1 side, and its +z points away from the
        /// viewer, which is the side a Unity TextMesh is read from.
        /// </summary>
        public static Quaternion LabelFacing(Vector3 viewerLocal, Vector3 aboutLocal)
        {
            float ny = viewerLocal.y - aboutLocal.y;
            float nz = viewerLocal.z - aboutLocal.z;
            float length = Mathf.Sqrt(ny * ny + nz * nz);
            if (length < 1e-6f)
            {
                ny = 1f;   // viewer on the hinge line: lie flat on the fretboard, facing +y
                nz = 0f;
            }
            else
            {
                ny /= length;
                nz /= length;
            }
            return RotationFromAxes(new Vector3(1f, 0f, 0f), new Vector3(0f, -nz, ny));
        }
    }
}
