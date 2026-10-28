using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Display;
using Xunit;

namespace ProsimCompanion.Core.Tests.Display;

/// <summary>The maths of the Screen &amp; FOV page against the design doc's reference rig:
/// a 52" 16:9 TV at 1.5 m (panel 1.151 × 0.648 m, vFOV 24.4°, hFOV 42.0°, InitialZoom
/// 0.137, dev-mode Fov 0.426 rad).</summary>
public sealed class FovCalculatorTests
{
    private static DisplayEntry Reference(DisplayRole role = DisplayRole.Main, string name = "Main")
        => new() { Name = name, Role = role, DiagonalInches = 52, AspectWidth = 16, AspectHeight = 9, DistanceMetres = 1.5 };

    [Fact]
    public void Compute_ReferenceRig_MatchesTheDesignNumbers()
    {
        var geometry = FovCalculator.Compute(Reference());

        Assert.Equal(1.151, geometry.WidthMetres, 0.002);
        Assert.Equal(0.648, geometry.HeightMetres, 0.002);
        Assert.Equal(24.4, geometry.VerticalFovDegrees, 0.05);
        Assert.Equal(42.0, geometry.HorizontalFovDegrees, 0.05);
        Assert.Equal(0.0, geometry.PitchOffsetDegrees, 0.0001);
    }

    [Fact]
    public void Plan_ReferenceRig_WritesTheDocumentedZoomAndFov()
    {
        var plan = FovCalculator.Plan(new DisplayOptions { Displays = [Reference()] });

        Assert.Equal(0.1367, plan.InitialZoom, 0.0005);
        Assert.Equal(0.426, plan.ExpectedFovRadians, 0.001);
        Assert.Equal(plan.TrueVerticalFovDegrees, plan.AppliedVerticalFovDegrees, 0.0001); // realism 1.0 by default
        Assert.Empty(plan.Rows);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(24.4)]
    [InlineData(49.2)]
    [InlineData(90.0)]
    public void ZoomAndFov_RoundTrip(double vFov)
        => Assert.Equal(vFov, FovCalculator.VerticalFovForInitialZoom(FovCalculator.InitialZoomForVerticalFov(vFov)), 1e-9);

    [Fact]
    public void VerticalFovForInitialZoom_SimDefault_IsAbout49Degrees()
        => Assert.Equal(49.2, FovCalculator.VerticalFovForInitialZoom(0.35), 0.05);

    [Fact]
    public void BlendRealism_ZeroIsTheSimDefault_HalfIsTheMidpoint_OneIsTrue()
    {
        Assert.Equal(49.2, FovCalculator.BlendRealism(24.4, 0.0), 1e-9);
        Assert.Equal((49.2 + 24.4) / 2, FovCalculator.BlendRealism(24.4, 0.5), 1e-9);
        Assert.Equal(24.4, FovCalculator.BlendRealism(24.4, 1.0), 1e-9);
        Assert.Equal(24.4, FovCalculator.BlendRealism(24.4, 7.0), 1e-9); // clamped
    }

    [Fact]
    public void Plan_Realism_BlendsTheAppliedFov()
    {
        var plan = FovCalculator.Plan(new DisplayOptions { Realism = 0.5, Displays = [Reference()] });

        Assert.Equal((49.2 + plan.TrueVerticalFovDegrees) / 2, plan.AppliedVerticalFovDegrees, 1e-9);
        Assert.Equal(FovCalculator.InitialZoomForVerticalFov(plan.AppliedVerticalFovDegrees), plan.InitialZoom, 1e-12);
    }

    [Fact]
    public void ScreenCentreAboveTheEye_LooksUp_WhichIsNegativeMsfsPitch()
    {
        var entry = Reference();
        entry.CentreAboveEyeMetres = 0.2;

        var geometry = FovCalculator.Compute(entry);
        var plan = FovCalculator.Plan(new DisplayOptions { Displays = [entry] });

        Assert.True(geometry.PitchOffsetDegrees > 0);
        Assert.Equal(7.595, geometry.PitchOffsetDegrees, 0.01); // atan(0.2 / 1.5)
        Assert.Equal(-geometry.PitchOffsetDegrees, plan.PitchDegrees, 1e-12);
    }

    [Fact]
    public void Plan_ZeroMainDisplays_Throws()
    {
        var options = new DisplayOptions { Displays = [Reference(DisplayRole.Left)] };

        var ex = Assert.Throws<InvalidOperationException>(() => FovCalculator.Plan(options));
        Assert.Contains("0 found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TwoMainDisplays_Throws()
    {
        var options = new DisplayOptions { Displays = [Reference(), Reference(name: "Second")] };

        var ex = Assert.Throws<InvalidOperationException>(() => FovCalculator.Plan(options));
        Assert.Contains("2 found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SecondaryDisplays_CarryYawAsLateralRotation_AndTheirDeltaFromApplied()
    {
        var left = Reference(DisplayRole.Left, "Left TV");
        left.YawDegrees = -45;
        left.CentreAboveEyeMetres = 0.1;
        var overhead = new DisplayEntry { Name = "Overhead", Role = DisplayRole.Overhead, DiagonalInches = 27, DistanceMetres = 1.0, YawDegrees = 0 };

        var plan = FovCalculator.Plan(new DisplayOptions { Displays = [left, Reference(), overhead] });

        Assert.Equal(2, plan.Rows.Count);
        var leftRow = plan.Rows[0];
        Assert.Same(left, leftRow.Display);
        Assert.Equal(-45, leftRow.LateralRotationDegrees);
        Assert.Equal(FovCalculator.Compute(left).PitchOffsetDegrees, leftRow.VerticalRotationDegrees, 1e-12);
        Assert.Equal(0.0, leftRow.DeltaFromAppliedDegrees, 1e-9); // same screen as Main: no mismatch

        var overheadRow = plan.Rows[1];
        Assert.Equal(FovCalculator.Compute(overhead).VerticalFovDegrees, overheadRow.IdealVerticalFovDegrees, 1e-12);
        Assert.Equal(overheadRow.IdealVerticalFovDegrees - plan.AppliedVerticalFovDegrees, overheadRow.DeltaFromAppliedDegrees, 1e-12);
    }

    [Fact]
    public void Compute_ZeroDistance_Throws()
    {
        var entry = Reference();
        entry.DistanceMetres = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() => FovCalculator.Compute(entry));
    }

    [Fact]
    public void DefaultOptions_AreANeutralSingleScreen()
    {
        var options = new DisplayOptions();

        var main = Assert.Single(options.Displays);
        Assert.Equal(DisplayRole.Main, main.Role);
        Assert.Equal(32, main.DiagonalInches);
        Assert.Equal(16, main.AspectWidth);
        Assert.Equal(9, main.AspectHeight);
        Assert.Equal(0.8, main.DistanceMetres);
        Assert.Equal(1.0, options.Realism);
        Assert.False(options.ApplyPitch);
        Assert.Same(main, options.SingleMain());
    }
}
