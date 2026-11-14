using System;
using System.Numerics;
using UntitledGuitarApp.Core.Geometry;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// Synthetic head pivot at the origin + a known neck pose → 4 dwell rays (+ optional seeded angular noise) →
// the solver must recover the pose (SL8 acceptance: residual < 5 mm without noise; residual reported with noise).
// World frame in the tests: mm, +x right, +y up, +z forward (Unity-like). The solver doesn't depend on it.
public class NeckSolverTests
{
    static readonly FretboardGeometry Geo = new FretboardGeometry(FretboardConfig.Default);
    static readonly NeckSolver Solver = new NeckSolver(Geo, NeckSolverConfig.Default);

    // Seated player looking down-left at the neck: nut ~45–70 cm away, neck running to the right, fretboard tilted up.
    public static TheoryData<float, float, float, float, float, float> Poses => new TheoryData<float, float, float, float, float, float>
    {
        // nut x, y, z (mm)     yaw, pitch, roll (deg)
        { -300f, -250f, 400f,   10f, -15f, 60f },
        { -250f, -300f, 350f,  -20f, -10f, 45f },
        { -350f, -200f, 500f,   25f, -25f, 70f },
        { -150f, -350f, 300f,    0f,   0f, 90f },
    };

    static NeckPose Truth(float x, float y, float z, float yaw, float pitch, float roll)
    {
        const float d = MathF.PI / 180f;
        var q = Quaternion.CreateFromYawPitchRoll(yaw * d, pitch * d, roll * d);
        return new NeckPose(new Vector3(x, y, z), Quaternion.Normalize(q));
    }

    static GazeRay[] RaysFor(NeckPose truth, Vector3 pivot)
    {
        var rays = new GazeRay[NeckSolver.TargetCount];
        for (int i = 0; i < rays.Length; i++)
        {
            var p = truth.ToWorld(Solver.ModelPoint((AlignTarget)i));
            rays[i] = new GazeRay(pivot, Vector3.Normalize(p - pivot));
        }
        return rays;
    }

    // Rotates each ray by `deg` about a seeded random axis perpendicular to it.
    static GazeRay[] AddNoise(GazeRay[] rays, double deg, int seed)
    {
        var rng = new Random(seed);
        var outRays = new GazeRay[rays.Length];
        for (int i = 0; i < rays.Length; i++)
        {
            var dir = rays[i].Direction;
            var rnd = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
            var axis = Vector3.Normalize(Vector3.Cross(dir, rnd));
            var q = Quaternion.CreateFromAxisAngle(axis, (float)(deg * Math.PI / 180.0));
            outRays[i] = new GazeRay(rays[i].Origin, Vector3.Normalize(Vector3.Transform(dir, q)));
        }
        return outRays;
    }

    static float PointError(NeckPose a, NeckPose b, AlignTarget t)
        => Vector3.Distance(a.ToWorld(Solver.ModelPoint(t)), b.ToWorld(Solver.ModelPoint(t)));

    [Theory, MemberData(nameof(Poses))]
    public void Solve_NoNoise_RecoversPoseWithResidualUnder5mm(float x, float y, float z, float yaw, float pitch, float roll)
    {
        var truth = Truth(x, y, z, yaw, pitch, roll);
        var r = Solver.Solve(RaysFor(truth, Vector3.Zero));

        Assert.True(r.Success, r.Failure);
        Assert.InRange(r.ResidualRmsMm, 0.0, 5.0);
        Assert.InRange(r.ResidualMaxMm, 0.0, 5.0);
        foreach (AlignTarget t in Enum.GetValues(typeof(AlignTarget)))
            Assert.InRange(PointError(r.Pose, truth, t), 0f, 1f);
        // a point that is not an alignment target (fret 3, string 1) also lands
        var dot = Geo.FingertipTarget(1, 3);
        Assert.InRange(Vector3.Distance(r.Pose.ToWorld(dot), truth.ToWorld(dot)), 0f, 1f);
    }

    [Fact]
    public void Solve_NoNoise_PivotNotAtOrigin_Recovers()
    {
        var truth = Truth(-300f, -250f, 400f, 10f, -15f, 60f);
        var pivot = new Vector3(20f, 1650f, -40f);
        var shifted = new NeckPose(truth.Position + pivot, truth.Rotation);
        var r = Solver.Solve(RaysFor(shifted, pivot));
        Assert.True(r.Success, r.Failure);
        Assert.InRange(PointError(r.Pose, shifted, AlignTarget.NutLowE), 0f, 1f);
        Assert.InRange(PointError(r.Pose, shifted, AlignTarget.Fret12LowE), 0f, 1f);
    }

    [Theory, MemberData(nameof(Poses))]
    public void Solve_HalfDegreeNoise_ReportsPositiveResidualAndStaysNearTruth(float x, float y, float z, float yaw, float pitch, float roll)
    {
        var truth = Truth(x, y, z, yaw, pitch, roll);
        var r = Solver.Solve(AddNoise(RaysFor(truth, Vector3.Zero), 0.5, seed: 7));

        Assert.True(r.Success, r.Failure);
        Assert.True(r.ResidualRmsMm > 0.05, $"residual {r.ResidualRmsMm} should reflect the noise");
        Assert.False(double.IsNaN(r.ResidualRmsMm) || double.IsInfinity(r.ResidualRmsMm));
        Assert.Equal(NeckSolver.TargetCount, r.TargetResidualMm.Length);
        Assert.InRange(PointError(r.Pose, truth, AlignTarget.NutLowE), 0f, 30f);
    }

    [Fact]
    public void Solve_MoreNoise_ReportsLargerResidual()
    {
        var truth = Truth(-300f, -250f, 400f, 10f, -15f, 60f);
        var rays = RaysFor(truth, Vector3.Zero);
        var small = Solver.Solve(AddNoise(rays, 0.2, seed: 3));
        var large = Solver.Solve(AddNoise(rays, 1.5, seed: 3));
        Assert.True(small.Success && large.Success);
        Assert.True(large.ResidualRmsMm > small.ResidualRmsMm,
            $"1.5° residual {large.ResidualRmsMm:F2} mm should exceed 0.2° residual {small.ResidualRmsMm:F2} mm");
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        var rays = AddNoise(RaysFor(Truth(-300f, -250f, 400f, 10f, -15f, 60f), Vector3.Zero), 0.5, seed: 11);
        var a = Solver.Solve(rays);
        var b = Solver.Solve(rays);
        Assert.Equal(a.ResidualRmsMm, b.ResidualRmsMm);
        Assert.Equal(a.Pose.Position, b.Pose.Position);
    }

    [Fact]
    public void Solve_NullOrWrongCount_FailsWithoutThrowing()
    {
        Assert.False(Solver.Solve(null).Success);
        Assert.False(Solver.Solve(new GazeRay[3]).Success);
    }

    [Fact]
    public void Solve_ZeroOrNaNDirection_Fails()
    {
        var rays = RaysFor(Truth(-300f, -250f, 400f, 10f, -15f, 60f), Vector3.Zero);
        rays[2] = new GazeRay(Vector3.Zero, Vector3.Zero);
        Assert.False(Solver.Solve(rays).Success);
        rays[2] = new GazeRay(Vector3.Zero, new Vector3(float.NaN, 0, 1));
        Assert.False(Solver.Solve(rays).Success);
    }

    [Fact]
    public void Solve_AllRaysIdentical_FailsAsDegenerate()
    {
        var d = Vector3.Normalize(new Vector3(-0.5f, -0.5f, 0.7f));
        var rays = new[] { new GazeRay(Vector3.Zero, d), new GazeRay(Vector3.Zero, d), new GazeRay(Vector3.Zero, d), new GazeRay(Vector3.Zero, d) };
        var r = Solver.Solve(rays);
        Assert.False(r.Success);
        Assert.False(string.IsNullOrEmpty(r.Failure));
    }

    [Fact]
    public void Solve_NeckOutsideSearchRange_Fails()
    {
        var far = Truth(-1500f, -1200f, 2500f, 10f, -15f, 60f); // ~3 m away; the search covers 30–90 cm
        Assert.False(Solver.Solve(RaysFor(far, Vector3.Zero)).Success);
    }

    [Fact]
    public void Reanchor_AfterSmallYaw_PutsNutBackOnNewRay()
    {
        var truth = Truth(-300f, -250f, 400f, 10f, -15f, 60f);
        var solved = Solver.Solve(RaysFor(truth, Vector3.Zero)).Pose;

        // the guitar (or yaw drift) swings 4° about the head pivot
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 4f * MathF.PI / 180f);
        var moved = new NeckPose(Vector3.Transform(truth.Position, yaw), Quaternion.Normalize(yaw * truth.Rotation));
        var nutRay = RaysFor(moved, Vector3.Zero)[(int)AlignTarget.NutLowE];

        var re = Solver.Reanchor(solved, nutRay);
        Assert.InRange(PointError(re, moved, AlignTarget.NutLowE), 0f, 0.5f);
        Assert.InRange(PointError(re, moved, AlignTarget.Fret12LowE), 0f, 10f);
        Assert.True(PointError(solved, moved, AlignTarget.NutLowE) > 20f); // it really had drifted
    }

    [Fact]
    public void Reanchor_KeepsNutDistanceFromPivot()
    {
        var truth = Truth(-300f, -250f, 400f, 10f, -15f, 60f);
        var nutBefore = truth.ToWorld(Solver.ModelPoint(AlignTarget.NutLowE)).Length();
        var newRay = new GazeRay(Vector3.Zero, Vector3.Normalize(new Vector3(-0.4f, -0.4f, 0.8f)));
        var re = Solver.Reanchor(truth, newRay);
        Assert.Equal(nutBefore, re.ToWorld(Solver.ModelPoint(AlignTarget.NutLowE)).Length(), 2);
    }

    [Fact]
    public void ModelPoint_UsesFretboardGeometry()
    {
        var nutLow = Solver.ModelPoint(AlignTarget.NutLowE);
        Assert.Equal(0.0, nutLow.AlongMm, 9);
        Assert.Equal(17.5, nutLow.AcrossMm, 9);
        Assert.Equal(-17.5, Solver.ModelPoint(AlignTarget.NutHighE).AcrossMm, 9);
        Assert.Equal(Geo.FretDistanceMm(5), Solver.ModelPoint(AlignTarget.Fret5LowE).AlongMm, 9);
        Assert.Equal(324.0, Solver.ModelPoint(AlignTarget.Fret12LowE).AlongMm, 9);
    }

    [Fact]
    public void NeckPose_ToWorld_AxesFollowRotation()
    {
        var pose = new NeckPose(new Vector3(10, 20, 30), Quaternion.Identity);
        Assert.Equal(new Vector3(110, 15, 30), pose.ToWorld(new NeckPoint(100, -5)));
        Assert.Equal(Vector3.UnitX, pose.AlongAxis);
        Assert.Equal(Vector3.UnitY, pose.AcrossAxis);
    }
}
