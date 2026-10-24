using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Briefings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The two cross-package wires of the November 2026 batch: the departure NOTAM
/// highlights as threat lines (#189 → #180) and the briefed EOSID after gear up (#180 → #187).</summary>
public sealed class NotamThreatsTests
{
    private static OfpNotam N(string icao, string q, string text, string id) => new(id, icao, q, text, null, null, false);

    [Fact]
    public void Lines_NameTheRunwaysAndTaxiways_DepartureOnly_EachOnce_CappedAtFour()
    {
        var notams = new List<OfpNotam>
        {
            N("EKCH", "QMRLC", "RWY 12 CLSD FOR TKOF AND LDG DUE TO WIP.", "A1"),
            N("EKCH", "QMRLC", "RWY 30 CLSD FOR TKOF DUE TO WIP.", "A2"),
            N("EKCH", "QMXLC", "TWY G CLSD BTN STAND G137 AND TWY G5 DUE TO WIP.", "A3"),
            N("EKCH", "QICAS", "ILS RWY 22L U/S.", "A4"),
            N("EKCH", "QLPAS", "PAPI RWY 04L/22R U/S.", "A5"),
            N("EKCH", "QOBXX", "OBST WITHDRAWN.", "A6"),
            N("EFHK", "QMRLC", "RWY 15 CLSD.", "B1"),
        };

        var lines = NotamThreats.Lines(notams, "EKCH");

        Assert.Equal(["runway 12 closed by NOTAM", "runway 30 closed by NOTAM", "taxiway G and G5 closed", "ILS runway 22L unserviceable"], lines);
        Assert.Equal(4, lines.Count); // the PAPI line is the fifth highlight — capped
        Assert.Equal([12, 30, 5, 22], NotamThreats.Numbers(lines));
        Assert.Empty(NotamThreats.Lines(notams, null));
        Assert.Empty(NotamThreats.Lines([], "EKCH"));
    }

    [Fact]
    public void Lines_WithoutADesignator_StillNameTheClass()
    {
        var lines = NotamThreats.Lines([N("EGLL", "QLPAS", "PAPI U/S.", "C1"), N("EGLL", "QMTCH", "THR DISPLACED 300M.", "C2")], "EGLL");
        Assert.Equal(["a PAPI unserviceable", "a displaced threshold"], lines);
    }

    [Fact]
    public void EngineOutLine_OnlyForAPublishedRoutingBriefedRecently()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var published = new EmergencyBriefFacts(
            Core.Weather.RunwaySurfaceState.Dry, false, 3500, 1500, 1500, false,
            "climb runway heading to 1500 feet, then left to LAM", true, "27R", "FLEX 52", null, false, 80);

        Assert.Equal("Engine-out routing: climb runway heading to 1500 feet, then left to LAM.",
            EfatoService.EngineOutLine(new DepartureEmergencyBrief("EGLL", "27R", published, now.AddMinutes(-20)), now));
        Assert.Null(EfatoService.EngineOutLine(new DepartureEmergencyBrief("EGLL", "27R", published, now.AddHours(-4)), now)); // stale
        Assert.Null(EfatoService.EngineOutLine(new DepartureEmergencyBrief("EGLL", "27R", published with { EngineOutIsPublished = false, EngineOutProcedure = EmergencyBriefFacts.DefaultEngineOutProcedure }, now), now));
        Assert.Null(EfatoService.EngineOutLine(null, now));
    }
}
