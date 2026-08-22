using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The 2026-08-22 EGLL fixes (issues #44/#75): the anchor's identity ladder, decorated-stand
/// nearest matching, the Change Facility parse, and the FO's parking-conflict wording.
/// </summary>
public sealed class GateFixesTests
{
    private static GsxParking Parking(string? uiName, string? uiGateName, string? bglName = null)
        => new(uiName, uiGateName, bglName, Number: null, Latitude: null, Longitude: null);

    [Fact]
    public void AnchorLadder_TriesEveryIdentity_DisplayNameFirst()
    {
        // Stand 313 (2026-08-22): 'Stand 313' was refused not_found while GSX's own menus
        // named the full facility|gate key — the ladder must offer both, plus the bare
        // designator the docs' "no prefix" reading suggests.
        var parkings = new[] { Parking("Terminal 3 (301-365)|Stand 313", "Stand 313") };

        var ladder = GsxGateResolver.AnchorTokenLadder(parkings, "Terminal 3 (301-365)|Stand 313");

        Assert.Equal(["Stand 313", "Terminal 3 (301-365)|Stand 313", "313"], ladder);
    }

    [Fact]
    public void AnchorLadder_UnknownParking_IsEmpty()
        => Assert.Empty(GsxGateResolver.AnchorTokenLadder(
            [Parking("Terminal 3|Stand 313", "Stand 313")], "Terminal 5|Stand 547"));

    [Fact]
    public void AnchorToken_KeepsTheOriginalFirstRung()
        // The pre-ladder behaviour (display gate name) stays the first attempt.
        => Assert.Equal("Gate D27", GsxGateResolver.ResolveAnchorToken(
            [Parking("D-Pier =< Medium | Gate D27", "Gate D27")], "D-Pier =< Medium | Gate D27"));

    [Theory]
    [InlineData("547", "Stand 547 with Safedock©", true)]
    [InlineData("Stand 547", "Stand 547 with Safedock©", true)]
    [InlineData("31", "Stand 313", false)]
    [InlineData("31", "Stand 313 with Safedock©", false)]
    [InlineData("D57", " Gate D57", true)]
    public void NearestMatch_HandlesDecoratedStands_WithoutGuessing(
        string requested, string nearest, bool expected)
        => Assert.Equal(expected, GsxGateResolver.IsUnambiguousNearestMatch(requested, nearest));

    [Fact]
    public void ChangeFacilityEntry_ParsesTheBracketedFacility()
        => Assert.Equal(
            "Terminal 5B (531-548) Stand 547 with Safedock©",
            GsxQuestionCatalog.ExtractFacility(
                "Change Facility [Terminal 5B (531-548) Stand 547 with Safedock©]"));

    [Fact]
    public void ParkingConflictAdvisory_SpeaksCleanFacility_AndTheTwoActions()
    {
        var text = ParkingConflictAdvisoryService.ComposeAdvisory(
            "Terminal 5B (531-548) Stand 547 with Safedock©");

        Assert.Equal(
            "Captain, GSX doesn't recognise our parking position — restarting won't help."
            + " It offers Terminal 5B Stand 547 with Safedock."
            + " Select the stand in the GSX menu, or reposition the aircraft.",
            text);
    }
}
