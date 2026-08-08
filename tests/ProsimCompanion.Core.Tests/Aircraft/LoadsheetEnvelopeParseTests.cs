using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>
/// Envelope round-trip for the restart restore path (issue #30): everything the FINAL
/// generation needs from a restored prelim — trip fuel, CHK baselines, edition inheritance —
/// must survive Build → TryParse.
/// </summary>
public sealed class LoadsheetEnvelopeParseTests
{
    private static LoadsheetData Data() => new()
    {
        ZeroFuelWeight = 58200,
        ZeroFuelWeightMac = 27.4,
        TakeoffWeight = 66900,
        TakeoffWeightMac = 29.1,
        FuelWeight = 8700,
        LandingWeight = 61400,
        LizfwIndex = 51.2,
        LitowIndex = 53.8,
        TotalPassengers = 174,
        PassengersByZone = [30, 60, 44, 40],
        ForwardCargoWeight = 1500,
        AftCargoWeight = 900,
    };

    private static LoadsheetContext Context() => new()
    {
        Ident = "PV123",
        Callsign = "PROTO123",
        EditionNumber = 3,
        DepartureIata = "CBR",
        ArrivalIata = "SYD",
        AircraftTailNumber = "VH-PVA",
        MaxZfwKg = 64300,
        MaxTowKg = 79000,
        MaxLawKg = 67400,
        WaterWasteKg = 1080,
        DispatcherFirstName = "Katie",
        DispatcherLastName = "Nguyen",
        Time = new DateTime(2026, 8, 9, 10, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void RoundTrip_RestoresWhatTheFinalNeeds()
    {
        var json = LoadsheetEnvelope.Build(Data(), Context(), isFinal: false, prelimForChk: null);

        Assert.True(LoadsheetEnvelope.TryParse(json, out var isFinal, out var data, out var ctx));
        Assert.False(isFinal);

        // Trip fuel for the final's LAW recovery.
        Assert.Equal(66900 - 61400, data.TakeoffWeight - data.LandingWeight, precision: 1);
        // CHK baselines.
        Assert.Equal(58200, data.ZeroFuelWeight, precision: 1);
        Assert.Equal(27.4, data.ZeroFuelWeightMac, precision: 2);
        Assert.Equal(29.1, data.TakeoffWeightMac, precision: 2);
        Assert.Equal(174, data.TotalPassengers);
        Assert.Equal(8700, data.FuelWeight, precision: 1);
        // Zones 3+4 were merged at build time; the sum survives.
        Assert.Equal(174, data.PassengersByZone.Sum());
        // Cargo total survives (split is lost by design).
        Assert.Equal(2400, data.ForwardCargoWeight + data.AftCargoWeight, precision: 1);

        // Edition inheritance + context the final reuses.
        Assert.Equal(3, ctx.EditionNumber);
        Assert.Equal("PV123", ctx.Ident);
        Assert.Equal("Katie", ctx.DispatcherFirstName);
        Assert.Equal(64300, ctx.MaxZfwKg, precision: 1);
        Assert.Equal(67400, ctx.MaxLawKg, precision: 1);
        Assert.Equal(new DateTime(2026, 8, 9, 10, 30, 0), ctx.Time);
    }

    [Fact]
    public void FinalEnvelope_ParsesAsFinal()
    {
        var json = LoadsheetEnvelope.Build(Data(), Context(), isFinal: true, prelimForChk: Data());

        Assert.True(LoadsheetEnvelope.TryParse(json, out var isFinal, out _, out _));
        Assert.True(isFinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Null")] // ProSim's literal placeholder for an unset string dataref
    [InlineData("not json at all")]
    [InlineData("{\"unrelated\":true}")]
    public void UnusableContent_IsRejected(string? json)
        => Assert.False(LoadsheetEnvelope.TryParse(json, out _, out _, out _));
}
