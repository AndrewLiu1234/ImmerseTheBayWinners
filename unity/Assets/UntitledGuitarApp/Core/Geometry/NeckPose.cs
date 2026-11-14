using System.Numerics;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Geometry
{
    /// <summary>The four alignment dwells, in the order the player looks at them (MASTER_PLAN §20).</summary>
    public enum AlignTarget { NutLowE = 0, NutHighE = 1, Fret5LowE = 2, Fret12LowE = 3 }

    /// <summary>A gaze ray in world millimetres: from the head pivot along the view direction at the moment of a dwell.</summary>
    public readonly struct GazeRay
    {
        public readonly Vector3 Origin;
        /// <summary>Any length; the solver normalises it. Zero or non-finite → the solve fails.</summary>
        public readonly Vector3 Direction;

        public GazeRay(Vector3 origin, Vector3 direction) { Origin = origin; Direction = direction; }
    }

    /// <summary>
    /// Rigid pose of the neck-local frame in world mm. Neck-local: origin = nut on the centreline, +X = Along (toward the
    /// bridge), +Y = Across (toward string 6), +Z = X×Y (fretboard normal), as in <see cref="FretboardGeometry"/>.
    /// Unity: world mm → metres is Runtime's job (×0.001).
    /// </summary>
    public sealed class NeckPose
    {
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        public NeckPose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }

        public Vector3 AlongAxis => Vector3.Transform(Vector3.UnitX, Rotation);
        public Vector3 AcrossAxis => Vector3.Transform(Vector3.UnitY, Rotation);
        public Vector3 NormalAxis => Vector3.Transform(Vector3.UnitZ, Rotation);

        public Vector3 ToWorld(NeckPoint p) => Position + Vector3.Transform(new Vector3((float)p.AlongMm, (float)p.AcrossMm, 0f), Rotation);
    }

    /// <summary>Outcome of <see cref="NeckSolver.Solve"/>. On failure <see cref="Pose"/> is null and residuals are double.MaxValue (never NaN).
    /// Note: with gaze noise the true nut error is typically ~5× the RMS residual (mostly depth along the gaze).</summary>
    public sealed class NeckSolveResult
    {
        public bool Success { get; }
        /// <summary>Why the solve failed (for UGALog and the "re-align" hint); null on success.</summary>
        public string Failure { get; }
        public NeckPose Pose { get; }
        /// <summary>RMS over the 4 targets of the distance between the fitted point and its gaze ray, mm.</summary>
        public double ResidualRmsMm { get; }
        public double ResidualMaxMm { get; }
        /// <summary>Per-target ray distance, mm, indexed by <see cref="AlignTarget"/>. Tells the player which dwell to redo.</summary>
        public double[] TargetResidualMm { get; }

        internal NeckSolveResult(NeckPose pose, double[] perTarget, double rms, double max)
        {
            Success = true; Pose = pose; TargetResidualMm = perTarget; ResidualRmsMm = rms; ResidualMaxMm = max;
        }

        internal NeckSolveResult(string failure)
        {
            Success = false; Failure = failure; TargetResidualMm = new double[0];
            ResidualRmsMm = double.MaxValue; ResidualMaxMm = double.MaxValue;
        }
    }
}
