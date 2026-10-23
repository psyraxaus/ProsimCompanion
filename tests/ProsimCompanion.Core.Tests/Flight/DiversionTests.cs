using System.Text.Json;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #184: the OFP overlay that is the diversion's one seam, the store lifecycle,
/// the consumers that read it, the voice parse and the logbook / debrief facts.</summary>
public sealed class DiversionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static OfpData Plan() => new()
    {
        RequestId = "1722", OriginIcao = "EGLL", OriginName = "LONDON HEATHROW",
        DestinationIcao = "LIRF", DestinationName = "ROME FIUMICINO", DestinationIata = "FCO",
        AlternateIcao = "LIRN", AlternateName = "NAPLES CAPODICHINO", PlannedRunwayIn = "16L",
        FuelPlanLandingKg = 3050, FuelPlanRampKg = 8200, PaxCount = 150,
        Navlog = [new OfpFix("TOC", new GeoPoint(51.0, 0.5), 7000, TimeSpan.FromMinutes(20), 36000, false)],
    };

    [Fact]
    public void Overlay_ReplacesTheDestination_DropsWhatBelongedToTheOldOne_KeepsTheRest()
    {
        var diverted = Plan().WithDiversion("lirn", "Naples");

        Assert.Equal("LIRN", diverted.DestinationIcao);
        Assert.Equal("Naples", diverted.DestinationName);
        Assert.Equal("", diverted.DestinationIata);
        Assert.Equal("", diverted.PlannedRunwayIn);
        Assert.Equal("", diverted.AlternateIcao);
        Assert.Equal(0, diverted.FuelPlanLandingKg);
        Assert.Empty(diverted.Navlog);
        Assert.Equal("EGLL", diverted.OriginIcao);
        Assert.Equal(8200, diverted.FuelPlanRampKg);
        Assert.Equal(150, diverted.PaxCount);
        Assert.Equal("1722", diverted.RequestId);
    }

    [Fact]
    public void Store_Declare_OverlaysTheOfp_Cancel_Restores_NewOfpEndsIt_ResetClears()
    {
        var ofp = new OfpStore();
        var signals = new GroundOpsSignals();
        using var store = new DiversionStore(ofp, signals);
        var changes = 0;
        ofp.Changed += (_, _) => changes++;
        ofp.Set(Plan());
        changes = 0;

        Assert.False(store.Declare("LIRF", null, DiversionReason.Weather, T0)); // the planned destination
        Assert.False(store.Declare("LI", null, DiversionReason.Weather, T0));   // not an ICAO
        Assert.True(store.Declare("LIRN", "Naples", DiversionReason.Weather, T0));

        Assert.True(ofp.IsDiverted);
        Assert.Equal("LIRN", ofp.Current!.DestinationIcao);
        Assert.Equal("LIRF", ofp.Planned!.DestinationIcao);
        Assert.Equal(1, changes);
        var snapshot = store.Current;
        Assert.True(snapshot.Active);
        Assert.Equal("LIRF", snapshot.OriginalDestinationIcao);
        Assert.Equal("ROME FIUMICINO", snapshot.OriginalDestinationName);
        Assert.Equal(DiversionReason.Weather, snapshot.Reason);

        // Re-declared to a third airport: the original stays the plan's.
        Assert.True(store.Declare("LIRP", null, DiversionReason.Medical, T0.AddMinutes(5)));
        Assert.Equal("LIRF", store.Current.OriginalDestinationIcao);
        Assert.Equal("LIRP", ofp.Current!.DestinationIcao);

        store.AddOffer("arrival briefing");
        store.AddOffer("arrival briefing");
        Assert.Equal(["arrival briefing"], store.Current.Offers);

        Assert.True(store.Cancel());
        Assert.False(store.Cancel());
        Assert.False(ofp.IsDiverted);
        Assert.Equal("LIRF", ofp.Current!.DestinationIcao);
        Assert.Single(ofp.Current.Navlog);

        Assert.True(store.Declare("LIRN", null, DiversionReason.Fuel, T0));
        ofp.Set(Plan()); // a new OFP is a new leg
        Assert.False(ofp.IsDiverted);
        // The store still says active until the cycle resets — the service's Cancel clears it.
        signals.RaiseFlightCycleReset();
        Assert.False(store.Current.Active);
        Assert.False(ofp.IsDiverted);
    }

    [Fact]
    public void Consumers_FollowTheOverlay_WeatherCardsAndProgressKey()
    {
        var plan = Plan();
        var diverted = plan.WithDiversion("LIRN", "Naples");

        // Hero weather cards in the cruise: destination card shows the new airport, no alternate card.
        var cruise = WeatherCardPlanner.Plan(diverted, FlightPhase.Cruise);
        Assert.Equal("LIRN", cruise.SecondIcao);
        Assert.Equal(WeatherCardRole.Destination, cruise.SecondRole);
        var descent = WeatherCardPlanner.Plan(diverted, FlightPhase.Descent);
        Assert.Equal("LIRN", descent.LocalIcao);
        Assert.Equal(WeatherCardRole.Destination, descent.SecondRole); // no alternate left to show

        // The progress service re-resolves airports when its OFP key changes — the key carries the destination.
        Assert.NotEqual($"{plan.RequestId}|{plan.OriginIcao}|{plan.DestinationIcao}|{plan.AlternateIcao}",
            $"{diverted.RequestId}|{diverted.OriginIcao}|{diverted.DestinationIcao}|{diverted.AlternateIcao}");
    }

    [Theory]
    [InlineData("divert to the alternate", true, null, DiversionReason.Other)]
    [InlineData("divert to alternate for weather", true, null, DiversionReason.Weather)]
    [InlineData("we are diverting", true, null, DiversionReason.Other)]
    [InlineData("divert to echo golf lima lima", true, "EGLL", DiversionReason.Other)]
    [InlineData("diverting to lima india romeo november for a sick passenger", true, "LIRN", DiversionReason.Medical)]
    [InlineData("divert to EGLL for fuel", true, "EGLL", DiversionReason.Fuel)]
    [InlineData("divert to echo golf for technical", true, null, DiversionReason.Technical)]
    [InlineData("request taxi", false, null, DiversionReason.Other)]
    public void ParseDivert_AlternatePhoneticAndReasonWords(string said, bool isDivert, string? icao, DiversionReason reason)
    {
        var ok = DiversionService.ParseDivert(said, out var parsedIcao, out var parsedReason);

        Assert.Equal(isDivert, ok);
        Assert.Equal(icao, parsedIcao);
        Assert.Equal(reason, parsedReason);
    }

    [Theory]
    [InlineData("weather", DiversionReason.Weather)]
    [InlineData("Technical", DiversionReason.Technical)]
    [InlineData("medical emergency", DiversionReason.Medical)]
    [InlineData("low fuel", DiversionReason.Fuel)]
    [InlineData("", DiversionReason.Other)]
    [InlineData(null, DiversionReason.Other)]
    public void ParseReason_Words(string? text, DiversionReason expected)
        => Assert.Equal(expected, FlightCommandHandlers.ParseReason(text));

    [Fact]
    public void CompanyTexts_AreTheAirlineFormat()
    {
        Assert.Equal("DIVERSION LIRN REASON WEATHER", DiversionService.CompanyMessage("lirn", DiversionReason.Weather));
        Assert.Equal("ROGER DIVERSION, HANDLING ARRANGED LIRN, CALL ON BLOCKS", DiversionService.CompanyReply("LIRN"));
    }

    [Fact]
    public void DebriefFacts_FollowTheDiversion_AndTheLogbookRecordsIt()
    {
        var dir = Directory.CreateTempSubdirectory("pc-diversion-").FullName;
        try
        {
            var extractor = new DebriefFactExtractor(Microsoft.Extensions.Logging.Abstractions.NullLogger<DebriefFactExtractor>.Instance);
            var path = new Tests.Debrief.SessionLogBuilder()
                .At("09:58:00").Event("flight.route", new { role = "departure", airport = "EGLL", runway = "27R" })
                .At("09:58:30").Event("flight.route", new { role = "arrival", airport = "LIRF", runway = "16L" })
                .At("11:20:00").Event("flight.diverted", new { from = "LIRF", to = "LIRN", reason = "weather", source = "voice" })
                .Write(dir);
            var facts = extractor.Extract(path);

            Assert.True(facts.Diverted);
            Assert.Equal("LIRN", facts.Destination);
            Assert.Equal("LIRF", facts.PlannedDestination);
            Assert.Equal("weather", facts.DiversionReason);
            Assert.Null(facts.ArrivalRunway);

            var cancelledPath = new Tests.Debrief.SessionLogBuilder()
                .At("09:58:30").Event("flight.route", new { role = "arrival", airport = "LIRF", runway = "16L" })
                .At("11:20:00").Event("flight.diverted", new { from = "LIRF", to = "LIRN", reason = "weather" })
                .At("11:30:00").Event("flight.diversionCancelled", new { backTo = "LIRF" })
                .Write(dir, "session-20260808-110000");
            var cancelled = extractor.Extract(cancelledPath);
            Assert.False(cancelled.Diverted);
            Assert.Equal("LIRF", cancelled.Destination);
            Assert.Null(cancelled.PlannedDestination);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        var flight = new LogbookFlight { Origin = "EGLL", Destination = "LIRN", Diverted = true, PlannedDestination = "LIRF", DiversionReason = "weather" };
        Assert.Equal("EGLL → LIRN · DIV", ProsimCompanion.Web.Components.LogbookPresentation.Route(flight));
        var csv = LogbookCsv.Build([flight]);
        Assert.Contains("diverted,plannedDestination,diversionReason", csv, StringComparison.Ordinal);
        Assert.Contains(",true,LIRF,weather", csv, StringComparison.Ordinal);
        // An older file without the fields loads as not diverted.
        var old = JsonSerializer.Deserialize<LogbookFlight>("""{"SessionId":"s","Origin":"EGLL","Destination":"LIRF"}""")!;
        Assert.False(old.Diverted);
        Assert.Null(old.PlannedDestination);
    }
}
