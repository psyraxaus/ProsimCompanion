using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>What counts as the in-flight airport pick having worked (ticket t-20261010-0726,
/// EFHK→EGCC 2026-10-10): GSX reporting the destination loaded, or GSX answering with its own
/// "Select Position at &lt;destination&gt;" page — attempts 2 and 3 of that flight showed that
/// page at once and were still logged GsxNoResponse after the 20 s airport wait.</summary>
public sealed class GsxGateSelectionAirportPickTests
{
    private static GsxStateMirror MirrorShowing(string title)
    {
        var mirror = new GsxStateMirror();
        mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = title,
            ["entries"] = new JsonArray("Select from Map", "Search parking...", "Gate\t(103 suitable parkings)"),
        });
        mirror.ApplyState("menuShown", JsonValue.Create(true));
        return mirror;
    }

    [Fact]
    public void AirportLoaded_IsSuccess()
    {
        var mirror = new GsxStateMirror();
        mirror.ApplyState("airport", new JsonObject { ["icao"] = "EGCC" });

        Assert.True(GsxGateSelectionService.AirportPickVerified(mirror, "EGCC"));
    }

    [Fact]
    public void SelectPositionPageForTheDestination_IsSuccess()
        => Assert.True(GsxGateSelectionService.AirportPickVerified(MirrorShowing("Select Position at EGCC/Manchester"), "EGCC"));

    [Fact]
    public void SelectPositionPageForAnotherAirport_IsNotSuccess()
        => Assert.False(GsxGateSelectionService.AirportPickVerified(MirrorShowing("Select Position at EFHK/Vantaa"), "EGCC"));

    [Fact]
    public void TheAirportListItself_IsNotSuccess()
        => Assert.False(GsxGateSelectionService.AirportPickVerified(MirrorShowing("Select airport"), "EGCC"));

    [Fact]
    public void HiddenPositionPage_IsNotSuccess()
    {
        var mirror = MirrorShowing("Select Position at EGCC/Manchester");
        mirror.ApplyState("menuShown", JsonValue.Create(false));

        Assert.False(GsxGateSelectionService.AirportPickVerified(mirror, "EGCC"));
    }
}
