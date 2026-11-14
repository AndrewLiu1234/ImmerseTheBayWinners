// GazeMath: pure functions for gaze-dwell. No physics colliders and no scene access: everything is an angle test between
// the head's forward ray and a target position. "Gaze" here means the head's forward direction, because the glasses
// only report head rotation (docs/MASTER_PLAN.md section 20).
//
// Namespace note: UntitledGuitarApp.Runtime.Gaze, not ...Runtime.Input (see DwellTimer.cs for why).
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Gaze
{
    public static class GazeMath
    {
        /// <summary>Squared length below which a direction is treated as "no direction" (1e-6 m).</summary>
        const float MinSqrMagnitude = 1e-12f;

        /// <summary>
        /// One thing the gaze can select, as plain data. GazeDwell fills a preallocated array of these every frame, so
        /// selecting a target allocates nothing and the maths stays testable without a scene.
        /// </summary>
        public struct Candidate
        {
            public Vector3 Position;   // world position of the target
            public float EnterDeg;     // the gaze must be within this angle to select the target
            public float ExitDeg;      // a "sticky" target (one being dwelled on) stays selected within this angle
            public bool Armed;         // false = cannot be selected right now (cooldown, fired)
            public bool Sticky;        // true = already being dwelled on, so the wider ExitDeg applies

            public Candidate(Vector3 position, float enterDeg, float exitDeg, bool armed, bool sticky)
            {
                Position = position;
                EnterDeg = enterDeg;
                ExitDeg = exitDeg;
                Armed = armed;
                Sticky = sticky;
            }
        }

        /// <summary>
        /// Angle in degrees (0..180) between two directions. The vectors need not be normalised. Returns
        /// float.PositiveInfinity if either is zero-length or NaN, so a degenerate input is never "looking at" anything.
        /// Uses atan2(|a x b|, a . b), which stays accurate for very small angles where acos loses precision.
        /// </summary>
        public static float AngleBetweenDeg(Vector3 a, Vector3 b)
        {
            if (!(a.sqrMagnitude > MinSqrMagnitude) || !(b.sqrMagnitude > MinSqrMagnitude)) return float.PositiveInfinity;
            float sin = Vector3.Cross(a, b).magnitude;   // |a||b| sin(theta)
            float cos = Vector3.Dot(a, b);               // |a||b| cos(theta)
            return Mathf.Atan2(sin, cos) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Angle in degrees between the gaze ray (origin, forward) and the direction from the origin to the target.
        /// float.PositiveInfinity if the target sits on the origin or the forward vector is degenerate.
        /// </summary>
        public static float AngleDeg(Vector3 origin, Vector3 forward, Vector3 targetPosition)
        {
            return AngleBetweenDeg(forward, targetPosition - origin);
        }

        /// <summary>
        /// Index of the armed candidate nearest to the gaze ray, or -1 if none is selectable. A candidate is selectable
        /// when the angle is within its EnterDeg, or within its (wider) ExitDeg if it is Sticky. "Nearest" is the smallest
        /// angle; ties go to the lower index. Only the first <paramref name="count"/> entries are read.
        /// <paramref name="bestAngleDeg"/> is the winner's angle (float.PositiveInfinity when the result is -1).
        /// </summary>
        public static int SelectNearest(Vector3 origin, Vector3 forward, Candidate[] candidates, int count, out float bestAngleDeg)
        {
            bestAngleDeg = float.PositiveInfinity;
            if (candidates == null) return -1;
            if (count > candidates.Length) count = candidates.Length;

            int best = -1;
            for (int i = 0; i < count; i++)
            {
                if (!candidates[i].Armed) continue;

                float limit = candidates[i].EnterDeg;
                if (candidates[i].Sticky && candidates[i].ExitDeg > limit) limit = candidates[i].ExitDeg;

                float angle = AngleDeg(origin, forward, candidates[i].Position);
                if (angle <= limit && angle < bestAngleDeg)
                {
                    bestAngleDeg = angle;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>World-space radius that subtends <paramref name="angleDeg"/> at <paramref name="distance"/> (metres): d * tan(angle).</summary>
        public static float AngularRadiusToWorld(float distance, float angleDeg)
        {
            return distance * Mathf.Tan(angleDeg * Mathf.Deg2Rad);
        }

        /// <summary>The angle in degrees that a disc of <paramref name="worldRadius"/> subtends at <paramref name="distance"/> metres.</summary>
        public static float WorldRadiusToAngularDeg(float distance, float worldRadius)
        {
            return Mathf.Atan2(worldRadius, distance) * Mathf.Rad2Deg;
        }
    }
}
