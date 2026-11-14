using System.Linq;
using UntitledGuitarApp.Core.Theory;
using Xunit;

// Ground truth: docs/MASTER_PLAN.md Appendix B4 (L = 648 mm). Tolerance 0.1 mm (SL0 acceptance).
// Neck-local frame: AlongMm = distance from the nut toward the bridge; AcrossMm = offset from the neck centreline,
// positive toward string 6 (low E). Default nut string span 35 mm, saddle span 53.5 mm (B4: ≈35 and ≈52–55).
public class FretboardGeometryTests
{
    const double Tol = 0.1;
    static readonly FretboardGeometry Geo = new FretboardGeometry(FretboardConfig.Default);

    [Theory]
    [InlineData(0, 0.0)] [InlineData(1, 36.4)] [InlineData(2, 70.7)] [InlineData(3, 103.1)]
    [InlineData(4, 133.7)] [InlineData(5, 162.5)] [InlineData(12, 324.0)]
    public void FretDistanceMm_AppendixB4_Within0p1mm(int fret, double mm)
    {
        Assert.InRange(Geo.FretDistanceMm(fret), mm - Tol, mm + Tol);
    }

    [Fact]
    public void FretDistanceMm_Fret12_IsExactlyHalfScale()
    {
        Assert.Equal(324.0, Geo.FretDistanceMm(12), 9);
    }

    [Theory]
    [InlineData(1, 27.3)] [InlineData(2, 62.1)] [InlineData(3, 95.0)]
    public void FingertipAlongMm_AppendixB4_Within0p1mm(int fret, double mm)
    {
        Assert.InRange(Geo.FingertipAlongMm(fret), mm - Tol, mm + Tol);
    }

    [Fact]
    public void StringAcrossMm_AtNut_SpansNutStringSpacingCentered()
    {
        Assert.Equal(17.5, Geo.StringAcrossMm(6, 0.0), 9);   // low E on the +y side
        Assert.Equal(-17.5, Geo.StringAcrossMm(1, 0.0), 9);  // high E on the −y side
        Assert.Equal(35.0 / 5.0, Geo.StringAcrossMm(4, 0.0) - Geo.StringAcrossMm(3, 0.0), 9);
    }

    [Fact]
    public void StringAcrossMm_AtSaddle_SpansSaddleStringSpacing()
    {
        double span = Geo.StringAcrossMm(6, 648.0) - Geo.StringAcrossMm(1, 648.0);
        Assert.Equal(FretboardConfig.Default.SaddleStringSpanMm, span, 9);
    }

    [Fact]
    public void StringAcrossMm_Midway_InterpolatesLinearly()
    {
        double nut = 35.0, saddle = FretboardConfig.Default.SaddleStringSpanMm;
        Assert.Equal((nut + saddle) / 4.0, Geo.StringAcrossMm(6, 324.0), 9);
    }

    [Fact]
    public void FingertipTarget_GLowE_IsFret3OnString6()
    {
        var p = Geo.FingertipTarget(6, 3);
        Assert.InRange(p.AlongMm, 95.0 - Tol, 95.0 + Tol);
        Assert.Equal(Geo.StringAcrossMm(6, p.AlongMm), p.AcrossMm, 9);
        Assert.True(p.AcrossMm > 17.5); // strings fan out toward the bridge
    }

    [Fact]
    public void FingertipDots_G_OneDotPerFingerAtB4Positions()
    {
        var dots = Geo.FingertipDots(ChordLibrary.Get("G")).ToArray();
        Assert.Equal(3, dots.Length);
        var d6 = dots.Single(d => d.String == 6);
        Assert.Equal(2, d6.Finger);
        Assert.InRange(d6.Position.AlongMm, 95.0 - Tol, 95.0 + Tol);
        var d5 = dots.Single(d => d.String == 5);
        Assert.InRange(d5.Position.AlongMm, 62.1 - Tol, 62.1 + Tol);
    }

    [Fact]
    public void FingertipDots_Em_HasNoDotsOnOpenStrings()
    {
        var dots = Geo.FingertipDots(ChordLibrary.Get("Em")).ToArray();
        Assert.Equal(new[] { 5, 4 }, dots.Select(d => d.String).OrderByDescending(s => s).ToArray());
    }

    [Fact]
    public void Config_CustomScale_ScalesDistances()
    {
        var short24 = new FretboardGeometry(new FretboardConfig(628.65, 35.0, 53.5));
        Assert.Equal(314.325, short24.FretDistanceMm(12), 9);
    }

    [Fact]
    public void FretDistanceMm_NegativeFret_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Geo.FretDistanceMm(-1));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Geo.FingertipAlongMm(0));
    }

    [Fact]
    public void Config_InvalidValues_Throw()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new FretboardConfig(0, 35, 53.5));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new FretboardConfig(648, -1, 53.5));
    }
}
