using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud
{
    /// <summary>
    /// Places head-locked HUD widgets by view angle instead of by metres, so a bootstrap script can say "5 degrees left of
    /// centre, 12 degrees down, 1 metre away" for a widget parented to the head or eye rig (identity rotation, +Z forward).
    /// Pure maths, no allocation.
    /// </summary>
    public static class HudLayout
    {
        /// <summary>
        /// Local position <paramref name="distanceM"/> metres away in the direction given by <paramref name="azimuthDeg"/>
        /// (positive = right) and <paramref name="elevationDeg"/> (positive = up). (0, 0, d) is straight ahead.
        /// </summary>
        public static Vector3 PointAtAngles(float azimuthDeg, float elevationDeg, float distanceM)
        {
            float az = azimuthDeg * Mathf.Deg2Rad;
            float el = elevationDeg * Mathf.Deg2Rad;
            float cosEl = Mathf.Cos(el);
            return new Vector3(
                distanceM * Mathf.Sin(az) * cosEl,
                distanceM * Mathf.Sin(el),
                distanceM * Mathf.Cos(az) * cosEl);
        }

        /// <summary>Sets <paramref name="t"/>.localPosition to <see cref="PointAtAngles"/>. Does nothing for a null transform.</summary>
        public static void Place(Transform t, float azimuthDeg, float elevationDeg, float distanceM)
        {
            if (t != null) t.localPosition = PointAtAngles(azimuthDeg, elevationDeg, distanceM);
        }
    }
}
