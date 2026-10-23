using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Acars;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Company;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Company;

/// <summary>Issue #185: the MVT text, the delay-code list and its matching, the punctuality
/// verdict at the OUT edge, the uplink slots, and the facts through the extractor, the logbook
/// and the Delay cell.</summary>
public sealed class MovementMessageTests : IDisposable
{
    private static readonly DateTimeOffset Std = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-mvt-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    // ---- composer ----

    [Fact]
    public void Off_LaysOutHeaderTimesEtaAndDelay_OneGroupPerLine()
    {
        var text = MovementMessageComposer.Off(
            "BAW123", "G-EUYB", "EGLL", "LIRF",
            Std.AddMinutes(22), Std.AddMinutes(35), new DateTimeOffset(2026, 10, 3, 12, 40, 0, TimeSpan.Zero), "15", 22);

        Assert.Equal("MVT\nBAW123/03.GEUYB.EGLL\nAD 1022/1035 EA 1240 LIRF\nDL 15/0022", text);
    }

    [Fact]
    public void Off_AcrossMidnight_KeepsTheOutDayAndFourDigitTimes()
    {
        var text = MovementMessageComposer.Off(
            "BAW123", "G-EUYB", "EGLL", "LIRF",
            new DateTimeOffset(2026, 10, 3, 23, 58, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 4, 0, 10, 0, TimeSpan.Zero), null);

        Assert.Equal("MVT\nBAW123/03.GEUYB.EGLL\nAD 2358/0010 LIRF", text);
    }

    [Fact]
    public void Off_WithoutASchedule_HasNoDelayLineEvenWithACode()
    {
        var text = MovementMessageComposer.Off("BAW123", "G-EUYB", "EGLL", "LIRF", Std, Std.AddMinutes(12), null, "15", null);

        Assert.DoesNotContain("DL", text, StringComparison.Ordinal);
        Assert.Null(MovementMessageComposer.DelayLine("15", 0));
        Assert.Equal("DL 93/0130", MovementMessageComposer.DelayLine("93", 90));
    }

    [Fact]
    public void In_LaysOutOnAndInTimes()
    {
        var text = MovementMessageComposer.In("BAW123", "G-EUYB", "LIRF", Std.AddHours(2).AddMinutes(3), Std.AddHours(2).AddMinutes(10));

        Assert.Equal("MVT\nBAW123/03.GEUYB.LIRF\nAA 1203/1210", text);
    }

    [Theory]
    [InlineData(22, 15, false)]
    [InlineData(15, 15, true)]
    [InlineData(-4, 15, true)]
    public void OnTime_IsOffBlocksWithinTheTolerance(int delay, int tolerance, bool expected)
    {
        Assert.Equal(expected, MovementMessageComposer.OnTime(delay, tolerance));
        Assert.Null(MovementMessageComposer.OnTime(null, tolerance));
    }

    [Fact]
    public void DelayMinutes_IsTheOutStampAgainstTheSchedule_NullWithoutOne()
    {
        Assert.Equal(22, MovementMessageComposer.DelayMinutes(Std, Std.AddMinutes(22)));
        Assert.Equal(-3, MovementMessageComposer.DelayMinutes(Std, Std.AddMinutes(-3)));
        Assert.Null(MovementMessageComposer.DelayMinutes(null, Std));
    }

    // ---- catalogue ----

    [Fact]
    public void Catalog_ShippedFileRoundTrips_AndAnEmptyFileKeepsTheDefaults()
    {
        var catalog = new DelayCodeCatalog();

        Assert.Equal(DelayCodeCatalog.Defaults.Count, DelayCodeCatalog.Parse(DelayCodeCatalog.DefaultsJson()).Count);
        Assert.True(catalog.Load("""{"codes":[{"code":"15","title":"Boarding","spoken":["boarding"]}]}"""));
        Assert.Single(catalog.Entries);
        Assert.False(catalog.Load("{}"));
        Assert.False(catalog.Load("not json"));
        Assert.Single(catalog.Entries);
    }

    [Theory]
    [InlineData("delay code 15", "15")]
    [InlineData("delay boarding", "15")]
    [InlineData("15 late passengers", "15")]
    [InlineData("it was the late inbound", "93")]
    [InlineData("sunshine", null)]
    public void Catalog_MatchesByNumberFirst_ThenByAlias(string words, string? code)
    {
        Assert.Equal(code, DelayCodeCatalog.Match(words, DelayCodeCatalog.Defaults)?.Code);
    }

    [Theory]
    [InlineData("delay code one five", "15")]
    [InlineData("fifteen", "15")]
    [InlineData("Delay code 93.", "93")]
    [InlineData("late passengers", "11")]
    [InlineData("no idea", null)]
    public void Phrases_PickTheChoiceTheWordsName(string heard, string? code)
    {
        var choices = DelayCodeCatalog.Defaults.Select(e => new QuestionChoice($"{e.Code} {e.Title}", e.Code, e.Title)).ToList();

        Assert.Equal(code, DelayCodePhrases.Pick(heard, choices, DelayCodeCatalog.Defaults)?.Token);
    }

    // ---- service ----

    [Fact]
    public async Task LateOffBlocks_AsksTheDelayCode_WithTheListAsChoices()
    {
        var h = Harness(delayCodes: true);
        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std.AddMinutes(22), null, null, null));

        var snapshot = h.Delay.Snapshot();
        Assert.True(snapshot.Asking.QuestionStands);
        Assert.Equal(22, snapshot.DelayMinutes);
        Assert.Equal(h.Codes.Entries.Count, snapshot.Asking.Question!.Choices.Count);
        Assert.Contains(snapshot.Asking.Question.Choices, c => c.Token == "15" && c.Label == "15 Boarding");
        Assert.Equal("We're 22 minutes late off blocks — delay code?", snapshot.Asking.Question.Prompt);
    }

    [Theory]
    [InlineData(10, true)]      // inside the tolerance
    [InlineData(22, false)]     // late, but the switch is off
    public async Task OnTimeOrSwitchOff_AsksNothing(int delay, bool delayCodes)
    {
        var h = Harness(delayCodes);
        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std.AddMinutes(delay), null, null, null));

        Assert.False(h.Delay.Snapshot().Asking.QuestionStands);
    }

    [Fact]
    public async Task TakeoffAndOnBlocks_UplinkTheMovementMessages_InSlots06And07()
    {
        var h = Harness(delayCodes: true);
        h.Progress.Update(_ => new FlightProgressSnapshot { EtaUtc = Std.AddHours(2).AddMinutes(40) });

        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std.AddMinutes(22), null, null, null));
        h.Delay.Choose("15", "test");
        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std.AddMinutes(22), Std.AddMinutes(35), null, null));
        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std.AddMinutes(22), Std.AddMinutes(35), Std.AddHours(2).AddMinutes(3), Std.AddHours(2).AddMinutes(10)));

        Assert.Equal(2, h.Sent.Count);
        Assert.Equal("06", h.Sent[0].Id);
        Assert.Equal("MVT\nBAW123/03.GEUYB.EGLL\nAD 1022/1035 EA 1240 LIRF\nDL 15/0022", h.Sent[0].Content);
        Assert.Equal("07", h.Sent[1].Id);
        Assert.Equal("MVT\nBAW123/03.GEUYB.LIRF\nAA 1203/1210\nDL 15/0022", h.Sent[1].Content);
        Assert.All(h.Sent, m => Assert.False(m.Accept));
    }

    [Fact]
    public async Task MovementMessagesOff_SendsNothing()
    {
        var h = Harness(delayCodes: false, movementMessages: false);

        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std, null, null, null));
        await h.Service.OnTimesAsync(new FlightTimesSnapshot(Std, Std.AddMinutes(12), null, null));

        Assert.Empty(h.Sent);
    }

    // ---- extractor, logbook, cell ----

    [Fact]
    public void Extractor_ReadsTheScheduleThePunctualityAndTheCode()
    {
        var path = new SessionLogBuilder()
            .At("09:30:00").Event("ofp.loaded", new { origin = "EGLL", destination = "LIRF", scheduledOutUtc = "2026-10-03T10:00:00+00:00", scheduledInUtc = "2026-10-03T12:35:00+00:00" })
            .At("10:22:00").Event("flight.punctuality", new { delayMinutes = 22, toleranceMinutes = 15, onTime = false })
            .At("10:23:00").Event("flight.delayCode", new { code = "15", title = "Boarding", delayMinutes = 22 })
            .At("12:10:00").Phase("TaxiIn", "Shutdown")
            .Write(_dir);

        var facts = new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance).Extract(path);

        Assert.Equal(Std, facts.ScheduledOutUtc);
        Assert.Equal(Std.AddHours(2).AddMinutes(35), facts.ScheduledInUtc);
        Assert.Equal("15", facts.DelayCode);
        Assert.Equal(22, facts.DelayMinutes);
        Assert.False(facts.OnTime);
    }

    [Fact]
    public void Logbook_FoldsTheFields_AndCountsOnTimePerformance()
    {
        var sessions = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(sessions);
        var eventLog = new JsonlEventLog(sessions, NullLogger<JsonlEventLog>.Instance);
        var logbook = new LogbookService(
            new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance), eventLog,
            OptionsSupport.Monitor(new LogbookOptions { Path = Path.Combine(_dir, "logbook.json") }), NullLogger<LogbookService>.Instance);
        logbook.Start();

        logbook.FoldSession(Leg("session-20261003-080000", onTime: true).Write(sessions, "session-20261003-080000"));
        logbook.FoldSession(Leg("session-20261003-140000", onTime: false).Write(sessions, "session-20261003-140000"));
        logbook.FoldSession(Leg("session-20261004-080000", onTime: true, scheduled: false).Write(sessions, "session-20261004-080000"));

        var late = logbook.Flights.Single(f => f.SessionId == "session-20261003-140000");
        Assert.Equal("15", late.DelayCode);
        Assert.Equal(22, late.DelayMinutes);
        Assert.False(late.OnTime);
        Assert.Equal(Std, late.ScheduledOutUtc);
        var unscheduled = logbook.Flights.Single(f => f.SessionId == "session-20261004-080000");
        Assert.Null(unscheduled.OnTime);
        Assert.Null(unscheduled.DelayMinutes);

        var totals = logbook.GetAggregates();
        Assert.Equal(2, totals.ScheduledFlights);
        Assert.Equal(1, totals.OnTimeFlights);
        Assert.Equal(50, totals.OnTimePct);
        Assert.Equal("+22 · 15", LogbookPresentation.Delay(late));
        Assert.Equal("—", LogbookPresentation.Delay(unscheduled));
    }

    [Theory]
    [InlineData(22, "15", false, "+22 · 15")]
    [InlineData(22, null, false, "+22")]
    [InlineData(7, null, true, "+7 on time")]
    [InlineData(0, null, true, "0")]
    [InlineData(-4, null, true, "−4")]
    public void DelayCell_ReadsMinutesAndCode(int minutes, string? code, bool onTime, string expected)
    {
        Assert.Equal(expected, LogbookPresentation.Delay(new LogbookFlight { DelayMinutes = minutes, DelayCode = code, OnTime = onTime }));
    }

    [Fact]
    public void LogbookCsv_CarriesTheFiveNewColumns()
    {
        var csv = LogbookCsv.Build([new LogbookFlight { SessionId = "s", ScheduledOutUtc = Std, DelayCode = "15", DelayMinutes = 22, OnTime = false }]);
        var lines = csv.Split("\r\n");

        Assert.EndsWith(",scheduledOutUtc,scheduledInUtc,delayCode,delayMinutes,onTime", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",2026-10-03T10:00:00Z,,15,22,false", lines[1], StringComparison.Ordinal);
    }

    private static SessionLogBuilder Leg(string id, bool onTime, bool scheduled = true)
    {
        var leg = new SessionLogBuilder()
            .At("09:30:00").Event("ofp.loaded", scheduled
                ? new { origin = "EGLL", destination = "LIRF", flightNumber = "BAW123", scheduledOutUtc = (string?)"2026-10-03T10:00:00+00:00" }
                : new { origin = "EGLL", destination = "LIRF", flightNumber = "BAW123", scheduledOutUtc = (string?)null })
            .At("10:00:00").Phase("Departure", "PushbackAndStart");
        if (scheduled)
        {
            leg.At("10:22:00").Event("flight.punctuality", new { delayMinutes = onTime ? 5 : 22, toleranceMinutes = 15, onTime })
                .At("10:23:00").Event("flight.delayCode", onTime ? null : new { code = "15", title = "Boarding", delayMinutes = 22 });
        }

        return leg
            .At("10:35:00").Phase("TakeoffRoll", "InitialClimb", iasKt: 150)
            .At("12:03:00").Phase("Approach", "LandingRollout", groundSpeedKt: 131)
            .At("12:10:00").Phase("TaxiIn", "Shutdown");
    }

    private Rig Harness(bool delayCodes, bool movementMessages = true)
    {
        var signals = new GroundOpsSignals();
        var delay = new DelayCodeStore(signals);
        var progress = new FlightProgressStore();
        var ofp = new OfpStore();
        ofp.Set(new OfpData
        {
            FlightNumber = "BAW123", Callsign = "BAW123", AircraftReg = "G-EUYB", OriginIcao = "EGLL", DestinationIcao = "LIRF",
            ScheduledOutUtc = Std, ScheduledInUtc = Std.AddHours(2).AddMinutes(35), EstimatedEnroute = TimeSpan.FromHours(2),
        });
        var sent = new List<AcarsMessage>();
        var acars = new Mock<IAcarsUplink>();
        acars.Setup(a => a.SendAsync(It.IsAny<AcarsMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AcarsMessage, CancellationToken>((m, _) => sent.Add(m))
            .ReturnsAsync(true);
        var options = new CompanyOptions
        {
            Enabled = true, MovementMessages = movementMessages, MovementMessagesSpoken = false, DelayCodes = delayCodes, DelayToleranceMinutes = 15,
        };
        var codes = new DelayCodeCatalog();
        var service = new MovementMessageService(
            OptionsSupport.Monitor(options), new FlightTimesStore(), ofp, progress, delay, codes, signals,
            Mock.Of<ISpeechArbiter>(), new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance),
            NullLogger<MovementMessageService>.Instance, acars.Object);
        service.Start();
        return new Rig(service, delay, progress, codes, sent);
    }

    private sealed record Rig(MovementMessageService Service, DelayCodeStore Delay, FlightProgressStore Progress, DelayCodeCatalog Codes, List<AcarsMessage> Sent);
}
