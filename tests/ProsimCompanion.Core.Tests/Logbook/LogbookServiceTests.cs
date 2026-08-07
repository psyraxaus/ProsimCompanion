using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logbook;

public sealed class LogbookServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sessionsDir;
    private readonly LogbookOptions _options;
    private readonly JsonlEventLog _eventLog;

    public LogbookServiceTests()
    {
        _dir = Directory.CreateTempSubdirectory("pc-logbook-").FullName;
        _sessionsDir = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(_sessionsDir);
        _options = new LogbookOptions { Path = Path.Combine(_dir, "logbook.json") };
        _eventLog = new JsonlEventLog(_sessionsDir, NullLogger<JsonlEventLog>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort — the event-log writer may still hold its file
        }
    }

    private LogbookService Create()
    {
        var service = new LogbookService(
            new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance),
            _eventLog,
            OptionsSupport.Monitor(_options),
            NullLogger<LogbookService>.Instance);
        service.Start();
        return service;
    }

    private string WriteLandedSession(string sessionId, string destination = "YMML")
        => new SessionLogBuilder()
            .At("09:58:00").Event("flight.route", new { role = "departure", airport = "YSSY", runway = "16R" })
            .At("09:59:00").Event("flight.route", new { role = "arrival", airport = destination, runway = "34" })
            .At("10:00:00").Phase("Preflight", "PushbackAndStart")
            .At("10:11:00").Phase("TakeoffRoll", "InitialClimb", iasKt: 150)
            .At("10:45:00").Event("approach.gate", new { gate = "500", aglFt = 500.0, result = "stable", criteria = Array.Empty<object>() })
            .At("10:52:00").Phase("Approach", "LandingRollout", groundSpeedKt: 130)
            .At("11:00:00").Phase("TaxiIn", "Shutdown")
            .Write(_sessionsDir, sessionId);

    // ---- folding ----

    [Fact]
    public void FoldSession_RecordsAndIsIdempotent()
    {
        var path = WriteLandedSession("session-20260807-100000");
        var service = Create();

        service.FoldSession(path);
        service.FoldSession(path); // second fold is a no-op

        var flight = Assert.Single(service.Flights);
        Assert.Equal("session-20260807-100000", flight.SessionId);
        Assert.Equal("2026-08-07", flight.Date);
        Assert.Equal("YSSY", flight.Origin);
        Assert.Equal("YMML", flight.Destination);
        Assert.Equal("16R", flight.DepartureRunway);
        Assert.Equal("34", flight.ArrivalRunway);
        Assert.Equal(60, flight.BlockMinutes);
        Assert.Equal(41, flight.FlightMinutes);
        Assert.True(flight.Landed);
        Assert.Equal("stable", flight.ApproachResult);
    }

    [Fact]
    public void FoldSession_SkipsMeaninglessSession()
    {
        var path = new SessionLogBuilder()
            .At("10:00:00").Event("session-started")
            .At("10:01:00").Event("callout.fired", new { id = "x" })
            .Write(_sessionsDir, "session-20260807-110000");

        var service = Create();
        service.FoldSession(path);
        Assert.Empty(service.Flights);
    }

    [Fact]
    public void FoldSession_SkipsWhenDisabledOrMissing()
    {
        var path = WriteLandedSession("session-20260807-100000");
        _options.Enabled = false;
        var service = Create();
        service.FoldSession(path);
        service.FoldSession(Path.Combine(_sessionsDir, "nope.jsonl"));
        Assert.Empty(service.Flights);
    }

    [Fact]
    public void FoldSession_UnstableGateWinsOverall()
    {
        var path = new SessionLogBuilder()
            .At("10:44:00").Event("approach.gate", new { gate = "1000", aglFt = 1000.0, result = "unstable", criteria = Array.Empty<object>() })
            .At("10:45:00").Event("approach.gate", new { gate = "500", aglFt = 500.0, result = "stable", criteria = Array.Empty<object>() })
            .At("10:52:00").Phase("Approach", "LandingRollout", groundSpeedKt: 140)
            .Write(_sessionsDir, "session-20260807-120000");

        var service = Create();
        service.FoldSession(path);
        Assert.Equal("unstable", Assert.Single(service.Flights).ApproachResult);
    }

    // ---- persistence ----

    [Fact]
    public void Store_RoundTripsAcrossInstances()
    {
        var path = WriteLandedSession("session-20260807-100000");
        Create().FoldSession(path);

        var reloaded = Create();
        Assert.Single(reloaded.Flights);
    }

    [Fact]
    public void Store_CorruptFileMovedAsideAndFreshStart()
    {
        File.WriteAllText(_options.Path, "no json here [");
        var service = Create();
        Assert.Empty(service.Flights);
        Assert.Single(Directory.GetFiles(_dir, "logbook.json.corrupt-*.bak"));
    }

    // ---- aggregates ----

    [Fact]
    public void GetAggregates_ComputesOnRead_WithFixedFastestSlowestSemantics()
    {
        File.WriteAllText(_options.Path, """
            {
              "version": 1,
              "flights": [
                { "sessionId": "s1", "date": "2026-08-01", "destination": "YMML",
                  "blockMinutes": 90, "flightMinutes": 75, "touchdownGroundSpeedKt": 135,
                  "landed": true, "approachResult": "stable" },
                { "sessionId": "s2", "date": "2026-08-02", "destination": "YMML",
                  "blockMinutes": 60, "flightMinutes": 45, "touchdownGroundSpeedKt": 121,
                  "landed": true, "approachResult": "unstable" },
                { "sessionId": "s3", "date": "2026-08-03", "destination": "YSSY",
                  "blockMinutes": 36, "landed": false, "approachResult": "indeterminate" }
              ]
            }
            """);

        var aggregates = Create().GetAggregates();

        Assert.Equal(3, aggregates.TotalFlights);
        Assert.Equal(3.1, aggregates.TotalBlockHours);  // 186 min → 3.1 (1 dp)
        Assert.Equal(2.0, aggregates.TotalFlightHours); // 120 min
        Assert.Equal(2, aggregates.Landings);
        Assert.Equal(2, aggregates.JudgedApproaches);   // indeterminate is not judged
        Assert.Equal(1, aggregates.StabilizedApproaches);
        Assert.Equal(50, aggregates.StabilizedRatePct);

        var ymml = Assert.Single(aggregates.Airports);
        Assert.Equal("YMML", ymml.Icao);
        Assert.Equal(2, ymml.Landings);
        Assert.Equal(135, ymml.FastestTouchdownGsKt); // the FIX: fastest = max
        Assert.Equal(121, ymml.SlowestTouchdownGsKt); // slowest = min
    }

    [Fact]
    public void GetAggregates_EmptyStore()
    {
        var aggregates = Create().GetAggregates();
        Assert.Equal(0, aggregates.TotalFlights);
        Assert.Null(aggregates.StabilizedRatePct);
    }

    // ---- comparison ----

    [Fact]
    public void DescribeComparison_FirstAndSubsequentLandings_ExcludingCurrentSession()
    {
        var service = Create();
        service.FoldSession(WriteLandedSession("session-20260806-100000"));
        service.FoldSession(WriteLandedSession("session-20260807-100000"));

        var landedFacts = DebriefFacts.Empty with { Destination = "YMML", FlightMinutes = 41 };

        // Two priors (neither excluded) → landing number 3.
        Assert.Equal("That's landing number 3 into YMML.",
            service.DescribeComparison(landedFacts, "session-20260808-100000"));

        // The current session's own (already-folded) record is excluded from the count.
        Assert.Equal("That's landing number 2 into YMML.",
            service.DescribeComparison(landedFacts, "session-20260807-100000"));

        Assert.Equal("That's your first landing into YBBN.",
            service.DescribeComparison(landedFacts with { Destination = "YBBN" }, null));
    }

    [Fact]
    public void DescribeComparison_NullWithoutDestinationOrLanding()
    {
        var service = Create();
        Assert.Null(service.DescribeComparison(DebriefFacts.Empty, null));
        Assert.Null(service.DescribeComparison(
            DebriefFacts.Empty with { Destination = "YMML" }, null)); // didn't land
    }

    // ---- backfill ----

    [Fact]
    public void Backfill_FoldsAllSessionsExceptCurrent()
    {
        WriteLandedSession("session-20260806-100000");
        WriteLandedSession("session-20260807-100000");
        // A meaningless old session — enumerated but not recorded.
        new SessionLogBuilder().At("10:00:00").Event("session-started")
            .Write(_sessionsDir, "session-20260805-100000");

        var service = Create();
        Assert.Equal(2, service.Backfill());
        Assert.Equal(2, service.Flights.Count);
        Assert.DoesNotContain(service.Flights,
            f => f.SessionId == Path.GetFileNameWithoutExtension(_eventLog.Path));

        Assert.Equal(0, service.Backfill()); // idempotent re-run
    }

    // ---- duty days (company day mode) ----

    [Fact]
    public void RecordDay_IsIdempotentByDayId_ReplacingWithTheLatestFacts()
    {
        var service = Create();
        service.RecordDay(new LogbookDay { DayId = "2026-08-08-A", Legs = 1, BlockMinutes = 80 });
        service.RecordDay(new LogbookDay { DayId = "2026-08-08-a", Legs = 2, BlockMinutes = 165 }); // case-insensitive re-run

        var day = Assert.Single(service.Days);
        Assert.Equal(2, day.Legs);
        Assert.Equal(165, day.BlockMinutes);

        service.RecordDay(new LogbookDay { DayId = "2026-08-09-A", Legs = 3 });
        Assert.Equal(2, service.Days.Count);
    }

    [Fact]
    public void RecordDay_PersistsAcrossRestartsAndSkipsBlankOrDisabled()
    {
        var service = Create();
        service.RecordDay(new LogbookDay { DayId = "" }); // no id — nothing to key on
        Assert.Empty(service.Days);

        service.RecordDay(new LogbookDay { DayId = "day-1", Legs = 2, DutyMinutes = 360 });

        var reloaded = Create(); // fresh instance over the same store file
        var day = Assert.Single(reloaded.Days);
        Assert.Equal("day-1", day.DayId);
        Assert.Equal(360, day.DutyMinutes);

        _options.Enabled = false;
        reloaded.RecordDay(new LogbookDay { DayId = "day-2" });
        Assert.Single(reloaded.Days);
    }
}
