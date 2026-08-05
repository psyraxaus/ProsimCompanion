using System.Text.Json.Nodes;
using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft.WeightAndBalance;

public sealed class LoadsheetEnvelopeTests
{
    private static LoadsheetContext Context() => new()
    {
        Ident = "1722",
        Callsign = "FIN536",
        DepartureIata = "RVN",
        ArrivalIata = "HEL",
        AircraftTailNumber = "OH-LXK",
        ScheduledDepartureTime = "10 10May26",
        EditionNumber = 3,
        MaxZfwKg = 62500,
        MaxTowKg = 79000,
        MaxLawKg = 64500,
        WaterWasteKg = 1080,
        DispatcherFirstName = "Judith",
        DispatcherLastName = "Nguyen",
        Time = new DateTime(2026, 5, 10, 9, 41, 7, DateTimeKind.Utc),
    };

    private static LoadsheetData Data() => new()
    {
        ZeroFuelWeight = 59000,
        ZeroFuelWeightMac = 26.9,
        TakeoffWeight = 66000,
        TakeoffWeightMac = 25.4,
        FuelWeight = 7000,
        LandingWeight = 61600,
        LizfwIndex = 706.35,
        LitowIndex = 704.10,
        TotalPassengers = 99,
        PassengersByZone = [18, 22, 27, 32],
        ForwardCargoWeight = 1200,
        AftCargoWeight = 1800,
    };

    [Fact]
    public void Prelim_EncodesProsimEnumInts_AndFlatMacs()
    {
        var json = JsonNode.Parse(LoadsheetEnvelope.Build(Data(), Context(), isFinal: false, prelimForChk: null))!;

        Assert.Equal(1, (int)json["type"]!);                       // Preliminary = 1
        Assert.Equal(0, (int)json["zfw"]!["unit"]!);               // Kg = 0
        Assert.Equal(59000.0, (double)json["zfw"]!["value"]!);
        Assert.Equal(26.9, (double)json["macZfw"]!);               // flat double, not a weight object
        Assert.Equal(3, (int)json["editionNumber"]!);
        Assert.Equal("05/10/2026 09:41:07", (string)json["time"]!);
        Assert.Equal("KG", (string)json["units"]!);
        Assert.Equal(18, (int)json["paxC1"]!);
        Assert.Equal(59, (int)json["paxC3"]!);                     // zones 3+4
        Assert.Equal(18, (int)json["paxBusiness"]!);
        Assert.Equal(81, (int)json["paxEconomy"]!);
        Assert.Equal(7000.0, (double)json["tof"]!["value"]!);      // TOW − ZFW
        Assert.Equal(4400.0, (double)json["tif"]!["value"]!);      // TOW − LAW
        Assert.Equal(2900.0, (double)json["undld"]!["value"]!);    // maxLaw − LAW
        Assert.False((bool)json["zfwChk"]!);                       // CHK flags never set on prelim
    }

    [Fact]
    public void Prelim_NearLimitMarkers_CarryTheLeadingSpace()
    {
        var data = Data();
        data.ZeroFuelWeight = 62000;                               // within 1000 kg of maxZfw
        var json = JsonNode.Parse(LoadsheetEnvelope.Build(data, Context(), isFinal: false, prelimForChk: null))!;

        Assert.Equal(" L", (string)json["zfwLim"]!);
        Assert.Equal("", (string)json["towLim"]!);
    }

    [Fact]
    public void Final_SetsChkFlags_AgainstTheCachedPrelim_AndNoLimMarkers()
    {
        var prelim = Data();
        var final = Data();
        final.ZeroFuelWeight = 62000;                              // +3000 kg > 1000 tolerance AND near max
        final.ZeroFuelWeightMac = 27.6;                            // +0.7 > 0.5 tolerance

        var json = JsonNode.Parse(LoadsheetEnvelope.Build(final, Context(), isFinal: true, prelimForChk: prelim))!;

        Assert.Equal(2, (int)json["type"]!);                       // Final = 2
        Assert.True((bool)json["zfwChk"]!);
        Assert.True((bool)json["macZfwChk"]!);
        Assert.False((bool)json["towChk"]!);
        Assert.Equal("", (string)json["zfwLim"]!);                 // lim markers are prelim-only
    }
}
