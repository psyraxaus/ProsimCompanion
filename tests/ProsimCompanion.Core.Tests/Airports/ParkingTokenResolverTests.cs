using ProsimCompanion.Core.Airports.Parking;
using Xunit;

namespace ProsimCompanion.Core.Tests.Airports;

/// <summary>The identity-based gate resolver: what the pilot types → the scenery stand → the
/// tokens GSX accepts. The W40 case is the EFHK refusal of 2026-10-03/04.</summary>
public sealed class ParkingTokenResolverTests
{
    private static AirportParking Stand(ParkingName name, int number, string suffix = "", string? gateName = null, string? uiName = null)
        => AirportParking.Bare(new ParkingIdentity(name, number, suffix), ParkingDataSources.GsxIni)
            with { GsxGateName = gateName, GsxUiName = uiName };

    private static readonly AirportParkings Efhk = new(
        "EFHK",
        [
            Stand(ParkingName.Gate, 17, gateName: "Gate 17", uiName: "Apron 1E (Gates 5-23) | Gate 17"),
            Stand(ParkingName.GateW, 40, gateName: "Gate 40", uiName: "Apron 1W (Gates W34-W48) | Gate 40"),
            Stand(ParkingName.GateW, 34, gateName: "Gate 34"),
            Stand(ParkingName.GateW, 34, "B", gateName: "Gate 34B"),
            Stand(ParkingName.GateS, 45, gateName: "Gate 45"),
            Stand(ParkingName.GateS, 45, "A", gateName: "Gate 45A"),
            Stand(ParkingName.Parking, 401, gateName: "Stand 401"),
            Stand(ParkingName.GateW, 48, "A", gateName: "Gate 48A"),
        ],
        ParkingDataSources.GsxIni | ParkingDataSources.GsxPy,
        [],
        DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("W40")]
    [InlineData("w40")]
    [InlineData("W 40")]
    [InlineData("Gate W40")]
    [InlineData("gate w 40")]
    [InlineData("W-40")]
    [InlineData("Gate 40")]                              // GSX's own name
    [InlineData("Apron 1W (Gates W34-W48) | Gate 40")]  // GSX's full menu name
    public void W40_InEveryShape_IsGateW40(string token)
    {
        var match = ParkingTokenResolver.Resolve(Efhk, token)!;

        Assert.Equal(new ParkingIdentity(ParkingName.GateW, 40, ""), match.Parking.Identity);
        Assert.Equal(ParkingMatchConfidence.Exact, match.Confidence);
        Assert.Equal(40, match.GsxTokens[0]);
        Assert.Equal("Gate 40", match.GsxTokens[1]);
        Assert.Equal("Apron 1W (Gates W34-W48) | Gate 40", match.GsxTokens[2]);
        Assert.Equal("Gate W40", match.GsxTokens[3]);
    }

    [Fact]
    public void BareNumber_WithOneStand_IsExact()
    {
        var match = ParkingTokenResolver.Resolve(Efhk, "401")!;
        Assert.Equal(ParkingName.Parking, match.Parking.Identity.Name);
        Assert.Equal(ParkingMatchConfidence.Exact, match.Confidence);
    }

    [Fact]
    public void BareNumber_OfALetteredGate_IsLikely()
    {
        var match = ParkingTokenResolver.Resolve(Efhk, "40")!;
        Assert.Equal(ParkingName.GateW, match.Parking.Identity.Name);
        Assert.Equal(ParkingMatchConfidence.Likely, match.Confidence);
    }

    [Fact]
    public void Suffix_IsHonoured()
    {
        Assert.Equal("B", ParkingTokenResolver.Resolve(Efhk, "W34B")!.Parking.Identity.Suffix);
        Assert.Equal("", ParkingTokenResolver.Resolve(Efhk, "W34")!.Parking.Identity.Suffix);
        Assert.Equal("A", ParkingTokenResolver.Resolve(Efhk, "S45A")!.Parking.Identity.Suffix);
    }

    [Fact]
    public void OnlySuffixedStandExists_UnsuffixedToken_IsLikely()
    {
        var match = ParkingTokenResolver.Resolve(Efhk, "W48")!;
        Assert.Equal("A", match.Parking.Identity.Suffix);
        Assert.Equal(ParkingMatchConfidence.Likely, match.Confidence);
    }

    [Fact]
    public void UnknownStand_IsNull()
    {
        Assert.Null(ParkingTokenResolver.Resolve(Efhk, "W50"));
        Assert.Null(ParkingTokenResolver.Resolve(Efhk, "999"));
        Assert.Null(ParkingTokenResolver.Resolve(Efhk, ""));
        Assert.Null(ParkingTokenResolver.Resolve(null, "W40"));
    }

    [Fact]
    public void SharedNumber_PrefersThePlainGate_AndListsTheRest()
    {
        var airport = new AirportParkings("TEST",
            [Stand(ParkingName.Gate, 12), Stand(ParkingName.GateA, 12), Stand(ParkingName.Parking, 12)],
            ParkingDataSources.Facility, [], DateTimeOffset.UnixEpoch);

        var match = ParkingTokenResolver.Resolve(airport, "12")!;
        Assert.Equal(ParkingName.Gate, match.Parking.Identity.Name);
        Assert.Equal(ParkingMatchConfidence.Ambiguous, match.Confidence);
        Assert.Equal(2, match.Alternatives.Count);

        Assert.Equal(ParkingName.GateA, ParkingTokenResolver.Resolve(airport, "A12")!.Parking.Identity.Name);
    }

    [Theory]
    [InlineData("W40", 'W', 40, "", null)]
    [InlineData("Stand 313", null, 313, "", "Stand")]
    [InlineData("545R", null, 545, "R", null)]
    [InlineData("gate d-27", 'D', 27, "", "gate")]
    [InlineData("34b", null, 34, "B", null)]
    public void Token_Parses(string text, char? letter, int number, string suffix, string? word)
    {
        var token = ParkingToken.Parse(text)!;
        Assert.Equal(letter, token.Letter);
        Assert.Equal(number, token.Number);
        Assert.Equal(suffix, token.Suffix);
        Assert.Equal(word, token.FacilityWord);
    }

    [Theory]
    [InlineData("Terminal 1 | Gate 5")]
    [InlineData("north apron")]
    [InlineData("")]
    public void Token_NonDesignators_AreNull(string text) => Assert.Null(ParkingToken.Parse(text));

    [Theory]
    [InlineData(ParkingName.GateW, 40, "", "Gate W40", "GATE_W", "gate w")]
    [InlineData(ParkingName.Gate, 17, "", "Gate 17", "GATE", "gate")]
    [InlineData(ParkingName.Parking, 401, "", "Parking 401", "PARKING", "parking")]
    [InlineData(ParkingName.NParking, 5, "W", "N Parking 5W", "N_PARKING", "n parking")]
    [InlineData(ParkingName.Dock, 3, "", "Dock 3", "DOCK", "dock")]
    [InlineData(ParkingName.None, 101, "", "None 101", "0", "none")]
    public void Identity_Names(ParkingName name, int number, string suffix, string display, string pyKey, string iniWords)
    {
        var id = new ParkingIdentity(name, number, suffix);
        Assert.Equal(display, id.DefaultDisplayName);
        Assert.Equal(pyKey, id.PyGroupKey);
        Assert.Equal(iniWords, id.IniNameWords);
        Assert.Equal(name, ParkingIdentity.ParsePyGroupKey(pyKey));
        Assert.Equal(name, ParkingIdentity.ParseIniNameWords(iniWords));
    }
}
