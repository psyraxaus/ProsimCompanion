using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Issue #156: answering GSX's own "Select Position at …" page for the arrival gate.
/// The rows are the ones EFHK showed on 2026-10-04 plus the shapes seen at EGLL and LIRF.</summary>
public sealed class GsxPositionMenuPlannerTests
{
    private static readonly string[] Efhk =
    [
        "Apron 1W (Gates W34-W48)",
        "Apron 1 (Gates 11-18)",
        "Apron 2 (Gates 21-31)",
        "Apron 4 (Cargo, 401-411)",
        "Remote Stands 8XX/9XX",
        "Next",
    ];

    [Fact]
    public void Efhk_W40_PicksTheWestApronGroup()
    {
        var pick = GsxPositionMenuPlanner.Pick(Efhk, "W40");

        Assert.NotNull(pick);
        Assert.Equal("Apron 1W (Gates W34-W48)", pick.Entry);
        Assert.False(pick.IsPosition);
    }

    [Theory]
    [InlineData("401", "Apron 4 (Cargo, 401-411)")]
    [InlineData("25", "Apron 2 (Gates 21-31)")]
    [InlineData("835", "Remote Stands 8XX/9XX")]
    [InlineData("912", "Remote Stands 8XX/9XX")]
    public void Ranges_CoverTheToken(string gate, string expected)
        => Assert.Equal(expected, GsxPositionMenuPlanner.Pick(Efhk, gate)!.Entry);

    [Theory]
    [InlineData("W50")]      // outside W34-W48
    [InlineData("40")]       // no prefix: not the W group, and 40 is in no plain range
    [InlineData("701")]
    public void NothingCovers_LeavesTheMenu(string gate)
        => Assert.Null(GsxPositionMenuPlanner.Pick(Efhk, gate));

    [Fact]
    public void TwoGroupsCovering_IsAmbiguous_LeavesTheMenu()
    {
        string[] rows = ["Terminal 1 (Gates 1-20)", "Terminal 1 West (Gates 10-30)"];

        Assert.Null(GsxPositionMenuPlanner.Pick(rows, "15"));
    }

    [Fact]
    public void AFlatList_PicksThePositionRowItself()
    {
        string[] rows = ["Gate 1", "Gate 2", "Stand 3 [Medium]", "Previous", "Next"];

        var pick = GsxPositionMenuPlanner.Pick(rows, "3");

        Assert.Equal("Stand 3 [Medium]", pick!.Entry);
        Assert.True(pick.IsPosition);
    }

    [Fact]
    public void ARowNamingTheToken_WinsOverARange()
    {
        string[] rows = ["Apron 1W (Gates W34-W48)", "Gate W40 (wide body)"];

        var pick = GsxPositionMenuPlanner.Pick(rows, "W40");

        Assert.Equal("Gate W40 (wide body)", pick!.Entry);
        Assert.True(pick.IsPosition);
    }

    [Fact]
    public void TokenInsideALongerNumber_DoesNotMatch()
    {
        string[] rows = ["Gate W401", "Gate W4"];

        Assert.Null(GsxPositionMenuPlanner.Pick(rows, "W40"));
    }

    [Fact]
    public void NavigationRows_AreNeverPicked()
        => Assert.Null(GsxPositionMenuPlanner.Pick(["Next", "Previous", "Change Facility [Apron 4 (Cargo, 401-411)    Stand 401]"], "401"));

    [Theory]
    [InlineData("Terminal 5B (531-548)|Stand 546", "546", true)]
    [InlineData("Remote Stands 8XX/9XX | Stand 835", "835", true)]
    [InlineData("Terminal 3 (301-365)", "311", true)]
    [InlineData("Terminal 3 (301-365)", "W311", false)]
    [InlineData("Apron 1W (Gates W34-W48)", "W34", true)]
    [InlineData("Apron 1W (Gates W34-W48)", "W48", true)]
    public void Covers_ReadsEveryRangeShape(string row, string token, bool expected)
        => Assert.Equal(expected, GsxPositionMenuPlanner.Covers(row, token));

    [Fact]
    public void SampleNames_ForTheRefusalDiagnostics()
    {
        var parkings = new[]
        {
            new GsxParking("Terminal 2 | Gate W40", "Gate W40", "Gate W40", 40, null, null),
            new GsxParking("Terminal 2 | Gate W41", "Gate W41", "Gate W41", 41, null, null),
            new GsxParking(null, null, null, 7, null, null),
        };

        Assert.Equal(["Gate W40", "Gate W41"], GsxGateResolver.SampleNames(parkings, 8));
        Assert.Equal(["Gate W40"], GsxGateResolver.SampleNames(parkings, 1));
    }
}
