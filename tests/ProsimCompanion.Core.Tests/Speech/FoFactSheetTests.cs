using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #149: the fact sheet is built from the stores, omits what it does not
/// know, and every number it prints is in the allowed set.</summary>
public sealed class FoFactSheetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 13, 20, 0, TimeSpan.Zero);

    private sealed class Live(double? fob, double? zfw, double? gw) : ILiveAircraftFacts
    {
        public double? FuelOnBoardKg => fob;
        public double? ZeroFuelWeightKg => zfw;
        public double? GrossWeightKg => gw;
    }

    private static FoFactInputs Full() => new(
        new FlightStateView(FlightPhase.Cruise, new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, AltitudeFt = 37012, GroundSpeedKt = 447.6, IndicatedAirspeedKt = 271,
            VerticalSpeedFpm = -30, OatC = -52.4, FmsCruiseAltFt = 37000,
        }, true, true),
        new FlightProgressSnapshot
        {
            DistanceToGoNm = 312.4, DistanceFlownNm = 468, EtaUtc = Now.AddMinutes(45), EtaBasis = ProgressBasis.Position,
            MinutesToTod = 25.4,
        },
        new FlightTimesSnapshot(Now.AddMinutes(-80), Now.AddMinutes(-70), null, null),
        new OfpData
        {
            Callsign = "BAW552", FlightNumber = "552", OriginIcao = "EGLL", OriginName = "London Heathrow",
            DestinationIcao = "LIRF", DestinationName = "Rome Fiumicino", AlternateIcao = "LIRN",
            PlannedRunwayOut = "27R", PlannedRunwayIn = "16L", CruiseFlightLevel = 370, CostIndex = "25",
            FuelPlanRampKg = 8120, FuelPlanLandingKg = 3050, FuelMinTakeoffKg = 7000, PaxCount = 150,
            ScheduledOutUtc = Now.AddMinutes(-90), EstimatedEnroute = TimeSpan.FromMinutes(130),
        },
        new LoadsheetSnapshot(LoadsheetSnapshot.EmptySlot, new LoadsheetSlotView(LoadsheetSlotStatus.Sent, 1, null, 58800, 66900, 28, 27, 8100, 148, null)),
        new Live(6200, 58800, 65000),
        new HeroWeatherSnapshot(
            WeatherCard.Empty(WeatherCardRole.Local),
            WeatherCard.From(WeatherCardRole.Destination, "LIRF", "Rome Fiumicino",
                WxProbe.Found(new WxFacts("METAR LIRF …", 270, 12, 9999, 1013.2, 24, "B", "16L", 2500))),
            Now, false),
        new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 410),
        [new TechLogDefect { Title = "APU bleed inoperative", Category = MelCategory.C, DueDate = "2026-10-13" }],
        Now);

    [Fact]
    public void Build_ListsEveryKnownFact_AsLabelledLines()
    {
        var sheet = FoFactSheetBuilder.Build(Full());
        var text = sheet.Text;

        Assert.Contains("- Flight phase: cruise", text, StringComparison.Ordinal);
        Assert.Contains("- Altitude: 37000 feet", text, StringComparison.Ordinal);
        Assert.Contains("- Fuel on board: 6200 kilograms (6.2 tonnes)", text, StringComparison.Ordinal);
        Assert.Contains("- Planned landing fuel: 3050 kilograms (3.1 tonnes)", text, StringComparison.Ordinal);
        Assert.Contains("- Final loadsheet zero fuel weight: 58800 kilograms (58.8 tonnes)", text, StringComparison.Ordinal);
        Assert.Contains("- Passengers on the loadsheet: 148", text, StringComparison.Ordinal);
        Assert.Contains("- Destination: Rome Fiumicino (LIRF)", text, StringComparison.Ordinal);
        Assert.Contains("- Alternate: LIRN", text, StringComparison.Ordinal);
        Assert.Contains("- Planned cruise level: flight level 370", text, StringComparison.Ordinal);
        Assert.Contains("- Distance to destination: 312 nautical miles (great-circle direct)", text, StringComparison.Ordinal);
        Assert.Contains("- Estimated arrival: 14:05 zulu", text, StringComparison.Ordinal);
        Assert.Contains("- Time to destination: 45 minutes", text, StringComparison.Ordinal);
        Assert.Contains("- Time to top of descent: about 25 minutes (3 to 1 estimate)", text, StringComparison.Ordinal);
        Assert.Contains("- Takeoff time: 12:10 zulu", text, StringComparison.Ordinal);
        Assert.Contains("- Time airborne so far: 1 hours 10 minutes", text, StringComparison.Ordinal);
        Assert.Contains("- Time now: 13:20 zulu", text, StringComparison.Ordinal);
        Assert.Contains("- Destination weather (LIRF): wind 270 degrees at 12 knots, visibility 10 kilometres or more, ceiling 2500 feet, temperature 24 degrees, QNH 1013, ATIS information B, runway in use 16L", text, StringComparison.Ordinal);
        Assert.Contains("- Briefed minimums: decision altitude 410 feet", text, StringComparison.Ordinal);
        Assert.Contains("- Open tech log items: 1", text, StringComparison.Ordinal);
        Assert.Contains("- Tech log item: APU bleed inoperative (MEL category C, due 2026-10-13)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Local weather", text, StringComparison.Ordinal);      // card had no observation
        Assert.DoesNotContain("Landing time", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EveryPrintedNumber_IsAllowed_AndSoAreTheSpokenForms()
    {
        var sheet = FoFactSheetBuilder.Build(Full());
        var allowed = sheet.AllowedNumbers;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sheet.Text, @"\d+(?:\.\d+)?"))
        {
            // Dates and clock times print as text with digits the verifier would also see.
            var value = double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (m.Value is "2026" or "10" or "13" or "05" or "20")
            {
                continue;
            }

            Assert.True(allowed.Any(a => Math.Abs(a - value) < 0.06), $"printed number {m.Value} is not in the allowed set");
        }

        Assert.Contains(6.2, allowed);        // "six point two tonnes"
        Assert.Contains(1405, allowed);       // "fourteen zero five"
        Assert.Contains(14, allowed);
        Assert.Contains(5, allowed);
        Assert.Contains(70, allowed);         // "seventy minutes airborne"
        Assert.Contains(1.2, allowed);        // "one point two hours"
        Assert.Contains(370, allowed);
        Assert.Contains(37000, allowed);
    }

    [Fact]
    public void Build_WithNothing_IsAlmostEmpty_AndNeverGuesses()
    {
        var sheet = FoFactSheetBuilder.Build(new FoFactInputs(null, null, null, null, null, null, null, null, null, Now));

        var only = Assert.Single(sheet.Facts);
        Assert.Equal("Time now", only.Label);
        Assert.False(sheet.IsEmpty);
        Assert.Equal("- Time now: 13:20 zulu", sheet.Text);
    }

    [Fact]
    public void Build_OnTheGround_OmitsTheAirborneFigures_AndUsesPlannedPaxWithoutAFinal()
    {
        var inputs = Full() with
        {
            Flight = new FlightStateView(FlightPhase.Preflight, new FlightDataSnapshot { IsValid = true, OnGround = true, OatC = 15 }, false, true),
            Loadsheet = LoadsheetSnapshot.Empty,
            Times = FlightTimesSnapshot.Empty,
            OpenDefects = [],
        };

        var text = FoFactSheetBuilder.Build(inputs).Text;

        Assert.Contains("- Flight phase: preflight at the gate", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Altitude", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ground speed", text, StringComparison.Ordinal);
        Assert.Contains("- Planned passengers: 150", text, StringComparison.Ordinal);
        Assert.DoesNotContain("loadsheet", text, StringComparison.Ordinal);
        Assert.Contains("- Open tech log items: none", text, StringComparison.Ordinal);
    }
}
