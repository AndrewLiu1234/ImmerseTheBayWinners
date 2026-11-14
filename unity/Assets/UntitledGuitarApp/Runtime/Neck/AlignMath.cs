// AlignMath: the arithmetic between Unity's world (metres, UnityEngine types) and Person 2's Core/Geometry/NeckSolver
// (millimetres, System.Numerics types) for the 4-glance neck alignment (docs/MASTER_PLAN.md section 20). Pure: only
// Vector3 / Quaternion arithmetic and NeckPlacement.RotationFromAxes, no engine calls, so all of it runs in offline tests.
//
// UNITS AND AXES. Core gets Unity's world coordinates unchanged, times 1000 (metres -> mm), measured from a reference point
// (the head pivot when the alignment started) so the numbers stay small. No axis is swapped or negated: Core never assumes a
// handedness of the world, it only uses the standard cross product, and Unity's Vector3.Cross is the same formula. A unit
// quaternion (x, y, z, w) turns a vector the same way in UnityEngine (q * v) and in System.Numerics (Vector3.Transform(v, q)):
// NeckAlignerTests.QuaternionConventionsAgree pins that.
//
// THE FRAMES. Core's NeckPose (Core/Geometry/NeckPose.cs): origin = the nut on the centre line, X = AlongAxis (nut -> bridge),
// Y = AcrossAxis (+ toward string 6), Z = NormalAxis = X x Y. Core's four targets lie in its z = 0 plane and are ON THE
// STRINGS (where the player looks). The neck frame (FloatingNeck, poses, Blender): +x nut -> bridge, +y out of the fretboard,
// +z toward string 1, origin on the fretboard surface at the nut, strings at y = stringHeightMm. So:
//   x = AlongAxis.
//   right-handed neck (FloatingNeck scale z = +1): the rotation's z must be -AcrossAxis, so y = cross(z, x) = NormalAxis.
//   left-handed neck (FloatingNeck mirrors it, scale z = -1): the rotation's z must be +AcrossAxis, so y = -NormalAxis.
//   nut (neck-frame origin) = Core's origin - y * stringHeight.
// In both cases y is the playing-surface normal of the real guitar, which points to the side the player looks from whenever
// the player can see the strings. Choosing y "toward the head" agrees with this whenever the fit is physical; when the two
// disagree the fit itself is wrong (the two nut glances swapped, a wild fit), and taking y from the head would draw string 1
// on string 6 (35 mm off at the nut). So y comes from the handedness, and "the fretboard faces the viewer" is a validity check
// (ViewerElevationDeg), used by NeckAlignFlow. NeckAlignerTests.RoundTrip* check every target lands back within 1 mm.
using UnityEngine;
using UntitledGuitarApp.Core.Geometry;
using UntitledGuitarApp.Core.Theory;
using CoreNeckPose = UntitledGuitarApp.Core.Geometry.NeckPose;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>
    /// The aligned neck frame in the world: <see cref="nut"/> is the frame's origin (centre line, nut face, fretboard surface) and
    /// <see cref="rotation"/> carries its +x (nut to bridge) and +y (out of the fretboard). Exactly what
    /// <see cref="FloatingNeck.SetWorldPose"/> takes; the mirroring of a left-handed neck is the transform's scale, not part of it.
    /// </summary>
    public struct AlignedPose
    {
        public Vector3 nut;
        public Quaternion rotation;

        public AlignedPose(Vector3 nut, Quaternion rotation)
        {
            this.nut = nut;
            this.rotation = rotation;
        }

        /// <summary>The neck's +x in the world: nut toward the bridge.</summary>
        public Vector3 AlongAxis
        {
            get { return rotation * Vector3.right; }
        }

        /// <summary>The neck's +y in the world: out of the playing surface.</summary>
        public Vector3 UpAxis
        {
            get { return rotation * Vector3.up; }
        }

        public bool IsFinite
        {
            get
            {
                return Finite(nut.x) && Finite(nut.y) && Finite(nut.z) && Finite(rotation.x) && Finite(rotation.y) &&
                       Finite(rotation.z) && Finite(rotation.w) &&
                       rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > 0.5f;
            }
        }

        static bool Finite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }
    }

    public static class AlignMath
    {
        public const float MmPerMetre = 1000f;

        // ------------------------------------------------------------------ Unity <-> Core units

        /// <summary>A Unity world point as Core millimetres, measured from <paramref name="referenceM"/>.</summary>
        public static NVector3 ToCoreMm(Vector3 worldM, Vector3 referenceM)
        {
            Vector3 d = (worldM - referenceM) * MmPerMetre;
            return new NVector3(d.x, d.y, d.z);
        }

        /// <summary>A Core point (mm from <paramref name="referenceM"/>) as a Unity world point in metres.</summary>
        public static Vector3 ToWorldMetres(NVector3 mm, Vector3 referenceM)
        {
            return referenceM + new Vector3(mm.X, mm.Y, mm.Z) / MmPerMetre;
        }

        /// <summary>A Core direction as a Unity vector (same numbers; directions have no unit).</summary>
        public static Vector3 ToUnity(NVector3 v)
        {
            return new Vector3(v.X, v.Y, v.Z);
        }

        /// <summary>A gaze ray for NeckSolver: from the head pivot (world metres) along the head's forward, in Core mm.</summary>
        public static GazeRay ToCoreRay(Vector3 originM, Vector3 direction, Vector3 referenceM)
        {
            return new GazeRay(ToCoreMm(originM, referenceM), new NVector3(direction.x, direction.y, direction.z));
        }

        // ------------------------------------------------------------------ Core pose <-> neck frame

        /// <summary>
        /// Core's solved pose as the neck frame's world pose (see the file header for the axes). <paramref name="stringHeightMm"/>
        /// is the layout's string height: Core's origin is on the strings, the neck frame's on the fretboard below them.
        /// </summary>
        public static AlignedPose FromCore(CoreNeckPose pose, Vector3 referenceM, bool leftHanded, float stringHeightMm)
        {
            Vector3 along = ToUnity(pose.AlongAxis);
            Vector3 normal = ToUnity(pose.NormalAxis);
            Vector3 up = leftHanded ? -normal : normal;
            Quaternion rotation = NeckPlacement.RotationFromAxes(along, up);
            Vector3 onStrings = ToWorldMetres(pose.Position, referenceM);
            return new AlignedPose(onStrings - (rotation * Vector3.up) * (stringHeightMm / MmPerMetre), rotation);
        }

        /// <summary>The inverse of <see cref="FromCore"/>: a neck-frame world pose as Core's NeckPose (for NeckSolver.Reanchor).</summary>
        public static CoreNeckPose ToCore(AlignedPose pose, Vector3 referenceM, bool leftHanded, float stringHeightMm)
        {
            Vector3 along = pose.rotation * Vector3.right;
            Vector3 up = pose.rotation * Vector3.up;
            Vector3 zAxis = pose.rotation * Vector3.forward;
            Vector3 across = leftHanded ? zAxis : -zAxis;                       // Core's + toward string 6
            Quaternion q = NeckPlacement.RotationFromAxes(along, across);        // x = along, y = across, z = Core's normal
            Vector3 onStrings = pose.nut + up * (stringHeightMm / MmPerMetre);
            return new CoreNeckPose(ToCoreMm(onStrings, referenceM), new NQuaternion(q.x, q.y, q.z, q.w));
        }

        /// <summary>A Core neck point (a dwell target: on the string) in the neck frame, metres: (along, string height, -across).</summary>
        public static Vector3 NeckLocalMetres(NeckPoint point, float stringHeightMm)
        {
            return new Vector3((float)point.AlongMm, stringHeightMm, CoreNeckLayout.ZMm(point.AcrossMm)) / MmPerMetre;
        }

        /// <summary>
        /// Where a neck-local point (metres) lands in the world for a life-size neck at <paramref name="pose"/>: exactly what
        /// FloatingNeck's transform does after SetWorldPose (scale (1, 1, 1), or (1, 1, -1) for a left-handed neck).
        /// </summary>
        public static Vector3 NeckToWorld(AlignedPose pose, bool leftHanded, Vector3 localM)
        {
            if (leftHanded) localM.z = -localM.z;
            return pose.nut + pose.rotation * localM;
        }

        /// <summary>
        /// How high the viewer is above the fretboard plane, seen from the nut, in degrees: positive = on the playing side (the
        /// strings face the viewer), negative = behind the neck. A real fit of a guitar the player can see is positive or just
        /// below zero (a steep, edge-on view); a strongly negative value means the fit is mirrored (two nut glances swapped).
        /// </summary>
        public static float ViewerElevationDeg(AlignedPose pose, Vector3 viewerM)
        {
            Vector3 toViewer = viewerM - pose.nut;
            float length = toViewer.magnitude;
            if (length < 1e-6f) return 0f;
            float s = Vector3.Dot(pose.rotation * Vector3.up, toViewer) / length;
            return Mathf.Asin(Mathf.Clamp(s, -1f, 1f)) * Mathf.Rad2Deg;
        }

        // ------------------------------------------------------------------ small rotation helpers (no engine calls)

        /// <summary>The angle between two directions in degrees (atan2 form: accurate for tiny angles, unlike acos).</summary>
        public static float AngleDeg(Vector3 a, Vector3 b)
        {
            float cross = Vector3.Cross(a, b).magnitude;
            float dot = Vector3.Dot(a, b);
            if (cross == 0f && dot == 0f) return 0f;
            return Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;
        }

        /// <summary>The inverse of a unit quaternion.</summary>
        public static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        /// <summary>A rotation of <paramref name="degrees"/> about <paramref name="axis"/> (any length; zero gives identity).</summary>
        public static Quaternion AxisAngle(Vector3 axis, float degrees)
        {
            float length = axis.magnitude;
            if (!(length > 1e-9f)) return Quaternion.identity;
            float half = degrees * Mathf.Deg2Rad * 0.5f;
            float s = Mathf.Sin(half) / length;
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(half));
        }

        /// <summary>The quaternion scaled to unit length (identity when it is zero or not finite).</summary>
        public static Quaternion Normalized(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(length > 1e-9f) || float.IsInfinity(length)) return Quaternion.identity;
            return new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
        }

        /// <summary>The pose turned by <paramref name="delta"/> about <paramref name="pivot"/> (a recenter's yaw about the head).</summary>
        public static AlignedPose RotateAbout(AlignedPose pose, Vector3 pivot, Quaternion delta)
        {
            return new AlignedPose(pivot + delta * (pose.nut - pivot), Normalized(delta * pose.rotation));
        }
    }
}
