using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logbook;

/// <summary>Issue #146: the touchdown event and the flight stamps through the extractor, the
/// fold, the store file (old and new shape), the delete tombstone and the CSV.</summary>
public sealed class LogbookLandingAnalysisTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sessionsDir;
    private readonly LogbookOptions _options;
    private readonly JsonlEventLog _eventLog;

    public LogbookLandingAnalysisTests()
    {
        _dir = Directory.CreateTempSubdirectory("pc-logbook-landing-").FullName;
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

    private static DebriefFactExtractor Extractor() => new(NullLogger<DebriefFactExtractor>.Instance);

    private LogbookService Create()
    {
        var service = new LogbookService(
            Extractor(), _eventLog, OptionsSupport.Monitor(_options), NullLogger<LogbookService>.Instance);
        service.Start();
        return service;
    }

    private static SessionLogBuilder Landed() => new SessionLogBuilder()
        .At("09:58:00").Event("flight.route", new { role = "departure", airport = "EGLL", runway = "27R" })
        .At("09:59:00").Event("flight.route", new { role = "arrival", airport = "LIRF", runway = "16L" })
        .At("10:00:00").Phase("Departure", "PushbackAndStart")
        .At("10:11:00").Phase("TakeoffRoll", "InitialClimb", iasKt: 150)
        .At("12:03:00").Phase("Approach", "LandingRollout", groundSpeedKt: 131);

    private static object Touchdown(double rate, bool wentAround = false, int bounces = 0, double ias = 137.4, double? pitch = 4.6)
        => new
        {
            contactUtc = "2026-08-08T12:03:00+00:00",
            verticalSpeedFpm = rate,
            iasKt = ias,
            groundSpeedKt = 131.0,
            pitchDeg = pitch,
            bankDeg = -0.5,
            bounces,
            wentAround,
            accelerationYRawMax = 1.842,
            accelerationYRawMin = 0.731,
            accelerationYRawBeforeContact = 1.004,
        };

    // ---- extractor ----

    [Fact]
    public void Extractor_ReadsTheTouchdownEventAndTheStamps()
    {
        var path = Landed()
            .At("10:00:05").Event("flight-times", new { offBlocksUtc = "2026-08-08T02:00:05+00:00", takeoffUtc = (string?)null, landingUtc = (string?)null, onBlocksUtc = (string?)null })
            .At("10:11:05").Event("flight-times", new { offBlocksUtc = "2026-08-08T02:00:05+00:00", takeoffUtc = "2026-08-08T02:11:05+00:00", landingUtc = (string?)null, onBlocksUtc = (string?)null })
            .At("12:03:06").Event("touchdown", Touchdown(-183, bounces: 1))
            .At("12:10:00").Phase("TaxiIn", "Shutdown")
            .At("12:10:01").Event("flight-times", new { offBlocksUtc = "2026-08-08T02:00:05+00:00", takeoffUtc = "2026-08-08T02:11:05+00:00", landingUtc = "2026-08-08T04:03:01+00:00", onBlocksUtc = "2026-08-08T04:10:00+00:00" })
            .Write(_sessionsDir);

        var facts = Extractor().Extract(path);

        Assert.Equal(-183, facts.TouchdownVerticalSpeedFpm);
        Assert.Equal(137.4, facts.TouchdownIasKt);
        Assert.Equal(4.6, facts.TouchdownPitchDeg);
        Assert.Equal(1, facts.Bounces);
        // The stamps are the SIM clock's (02:00Z here), not the session's wall clock (10:00Z).
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 2, 0, 5, TimeSpan.Zero), facts.OffBlocksUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 2, 11, 5, TimeSpan.Zero), facts.TakeoffUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 4, 3, 1, TimeSpan.Zero), facts.LandingUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 4, 10, 0, TimeSpan.Zero), facts.OnBlocksUtc);
        // The phase-derived facts are untouched.
        Assert.Equal(131, facts.TouchdownGroundSpeedKt);
        Assert.Equal(112, facts.FlightMinutes);
    }

    [Fact]
    public void Extractor_WithoutTheEvent_LeavesTheNewFactsNull()
    {
        // A session recorded before the touchdown recorder existed.
        var path = Landed().At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir);

        var facts = Extractor().Extract(path);

        Assert.Null(facts.TouchdownVerticalSpeedFpm);
        Assert.Null(facts.TouchdownIasKt);
        Assert.Null(facts.TouchdownPitchDeg);
        Assert.Null(facts.Bounces);
        Assert.Null(facts.OffBlocksUtc);
        Assert.Null(facts.OnBlocksUtc);
        Assert.Equal(131, facts.TouchdownGroundSpeedKt);
        Assert.Equal(130, facts.BlockMinutes);
    }

    [Fact]
    public void Extractor_AGoAroundAfterTouch_DoesNotStandInForTheLanding()
    {
        var path = Landed()
            .At("12:03:10").Event("touchdown", Touchdown(-420, wentAround: true))
            .At("12:14:00").Event("touchdown", Touchdown(-160))
            .At("12:20:00").Phase("TaxiIn", "Shutdown")
            .Write(_sessionsDir);

        Assert.Equal(-160, Extractor().Extract(path).TouchdownVerticalSpeedFpm);
    }

    [Fact]
    public void Extractor_FirstSettledLandingWins_AndAGoAroundOnlySessionKeepsItsTouch()
    {
        var twoLegs = Landed()
            .At("12:03:06").Event("touchdown", Touchdown(-150))
            .At("15:40:00").Event("touchdown", Touchdown(-390))     // a second leg in the same session
            .Write(_sessionsDir, "session-20260808-100001");
        var onlyGoArounds = Landed()
            .At("12:03:06").Event("touchdown", Touchdown(-510, wentAround: true))
            .At("12:09:00").Event("touchdown", Touchdown(-240, wentAround: true))
            .Write(_sessionsDir, "session-20260808-100002");

        Assert.Equal(-150, Extractor().Extract(twoLegs).TouchdownVerticalSpeedFpm);
        Assert.Equal(-510, Extractor().Extract(onlyGoArounds).TouchdownVerticalSpeedFpm);
    }

    [Fact]
    public void Extractor_ATouchdownWithNoAttitude_KeepsTheRate()
    {
        var path = Landed().At("12:03:06").Event("touchdown", Touchdown(-200, pitch: null)).Write(_sessionsDir);

        var facts = Extractor().Extract(path);

        Assert.Equal(-200, facts.TouchdownVerticalSpeedFpm);
        Assert.Null(facts.TouchdownPitchDeg);
    }

    // ---- fold + store file ----

    [Fact]
    public void Fold_CarriesTheLandingFieldsAndStamps_IntoTheStoreFile()
    {
        var path = Landed()
            .At("12:03:06").Event("touchdown", Touchdown(-183, bounces: 2))
            .At("12:10:00").Phase("TaxiIn", "Shutdown")
            .At("12:10:01").Event("flight-times", new { offBlocksUtc = "2026-08-08T02:00:05+00:00", takeoffUtc = "2026-08-08T02:11:05+00:00", landingUtc = "2026-08-08T04:03:01+00:00", onBlocksUtc = "2026-08-08T04:10:00+00:00" })
            .Write(_sessionsDir);
        var logbook = Create();

        logbook.FoldSession(path);

        // Re-read from disk: what a restart would see.
        var flight = Assert.Single(Create().Flights);
        Assert.Equal(-183, flight.TouchdownVerticalSpeedFpm);
        Assert.Equal(137.4, flight.TouchdownIasKt);
        Assert.Equal(4.6, flight.TouchdownPitchDeg);
        Assert.Equal(2, flight.Bounces);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 4, 10, 0, TimeSpan.Zero), flight.OnBlocksUtc);
        Assert.Equal(131, flight.TouchdownGroundSpeedKt);
    }

    [Fact]
    public void Backfill_ToleratesSessionsWithoutTheTouchdownEvent()
    {
        Landed().At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir, "session-20260701-080000");
        Landed().At("12:03:06").Event("touchdown", Touchdown(-140))
            .At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir, "session-20261003-080000");
        var logbook = Create();

        Assert.Equal(2, logbook.Backfill());

        var flights = logbook.Flights.OrderBy(f => f.SessionId, StringComparer.Ordinal).ToList();
        Assert.Null(flights[0].TouchdownVerticalSpeedFpm);
        Assert.True(flights[0].Landed);
        Assert.Equal(-140, flights[1].TouchdownVerticalSpeedFpm);
    }

    /// <summary>A logbook.json exactly as 0.5.0-rc.12 wrote it: no landing fields, no stamps,
    /// no removedSessionIds.</summary>
    private const string PreChangeStore = """
        {
          "version": 1,
          "flights": [
            {
              "sessionId": "session-20260913-070512",
              "date": "2026-09-13",
              "origin": "EGLL",
              "destination": "LIRF",
              "departureRunway": "27R",
              "arrivalRunway": "16L",
              "blockMinutes": 152,
              "flightMinutes": 121,
              "liftoffIasKt": 151.2,
              "touchdownGroundSpeedKt": 133.5,
              "landed": true,
              "approachResult": "stable",
              "abnormals": [ "APU FAULT" ],
              "defectsRaised": 1,
              "defectsRectified": 0,
              "defectsCarried": 2
            },
            {
              "sessionId": "session-20260920-181144",
              "date": "2026-09-20",
              "origin": "LIRF",
              "destination": "EGLL",
              "landed": true,
              "abnormals": [],
              "defectsRaised": 0,
              "defectsRectified": 0,
              "defectsCarried": 0
            }
          ],
          "days": [
            { "dayId": "day-20260913", "date": "2026-09-13", "legs": 1, "route": [ "EGLL", "LIRF" ], "stabilizedApproaches": 1, "judgedApproaches": 1, "abnormals": 1, "memoryDrills": 0, "defectsRaised": 1, "defectsRectified": 0 }
          ]
        }
        """;

    [Fact]
    public void APreChangeStoreFile_Loads_WithTheNewFieldsEmpty()
    {
        File.WriteAllText(_options.Path, PreChangeStore);

        var logbook = Create();

        Assert.Equal(2, logbook.Flights.Count);
        var flight = logbook.Flights[0];
        Assert.Equal("EGLL", flight.Origin);
        Assert.Equal(133.5, flight.TouchdownGroundSpeedKt);
        Assert.Equal("APU FAULT", Assert.Single(flight.Abnormals));
        Assert.Null(flight.TouchdownVerticalSpeedFpm);
        Assert.Null(flight.TouchdownIasKt);
        Assert.Null(flight.TouchdownPitchDeg);
        Assert.Null(flight.Bounces);
        Assert.Null(flight.OffBlocksUtc);
        Assert.Null(flight.OnBlocksUtc);
        Assert.Single(logbook.Days);

        var totals = logbook.GetAggregates();
        Assert.Equal(2, totals.Landings);
        Assert.Null(totals.AverageTouchdownVerticalSpeedFpm);   // nothing measured: no invented average
        Assert.Equal(0, totals.MeasuredTouchdowns);
    }

    [Fact]
    public void APreChangeStoreFile_SurvivesAFoldAndASave_Unharmed()
    {
        File.WriteAllText(_options.Path, PreChangeStore);
        var path = Landed().At("12:03:06").Event("touchdown", Touchdown(-170))
            .At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir, "session-20261003-080000");

        Create().FoldSession(path);

        var reloaded = Create();
        Assert.Equal(3, reloaded.Flights.Count);
        Assert.Equal(152, reloaded.Flights[0].BlockMinutes);
        Assert.Equal(-170, reloaded.Flights[2].TouchdownVerticalSpeedFpm);
        // Additive only: an old flight does not grow null-valued keys on save.
        var json = File.ReadAllText(_options.Path);
        Assert.Equal(1, CountOf(json, "touchdownVerticalSpeedFpm"));
    }

    [Fact]
    public void Aggregates_AverageOnlyTheMeasuredLandings()
    {
        File.WriteAllText(_options.Path, PreChangeStore);
        var logbook = Create();
        logbook.FoldSession(Landed().At("12:03:06").Event("touchdown", Touchdown(-100))
            .At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir, "session-20261001-080000"));
        logbook.FoldSession(Landed().At("12:03:06").Event("touchdown", Touchdown(-301))
            .At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir, "session-20261002-080000"));

        var totals = logbook.GetAggregates();

        Assert.Equal(4, totals.Landings);
        Assert.Equal(2, totals.MeasuredTouchdowns);
        Assert.Equal(-200, totals.AverageTouchdownVerticalSpeedFpm);    // (−100 − 301) / 2 = −200.5 → −200
    }

    // ---- delete ----

    [Fact]
    public void RemoveFlight_DeletesIt_RaisesChanged_AndABackfillDoesNotBringItBack()
    {
        Landed().At("12:10:00").Phase("TaxiIn", "Shutdown").Write(_sessionsDir, "session-20261003-080000");
        var logbook = Create();
        Assert.Equal(1, logbook.Backfill());
        var changes = 0;
        logbook.Changed += (_, _) => changes++;

        Assert.True(logbook.RemoveFlight("SESSION-20261003-080000"));

        Assert.Empty(logbook.Flights);
        Assert.Equal(1, changes);
        Assert.False(logbook.RemoveFlight("session-20261003-080000"));     // already gone
        Assert.False(logbook.RemoveFlight(" "));
        Assert.Equal(0, logbook.Backfill());
        Assert.Empty(logbook.Flights);
        // And not after a restart either.
        var restarted = Create();
        Assert.Equal(0, restarted.Backfill());
        Assert.Empty(restarted.Flights);
    }

    // ---- CSV ----

    [Fact]
    public void Csv_HeaderAndRow_AreInvariantAndUtcIso()
    {
        var flight = new LogbookFlight
        {
            SessionId = "session-20261003-080000",
            Date = "2026-10-03",
            Origin = "EGLL",
            Destination = "LIRF",
            DepartureRunway = "27R",
            ArrivalRunway = "16L",
            // A stamp with an offset must come out as UTC.
            OffBlocksUtc = new DateTimeOffset(2026, 10, 3, 10, 0, 5, TimeSpan.FromHours(2)),
            TakeoffUtc = new DateTimeOffset(2026, 10, 3, 8, 11, 5, TimeSpan.Zero),
            LandingUtc = new DateTimeOffset(2026, 10, 3, 10, 3, 1, TimeSpan.Zero),
            OnBlocksUtc = new DateTimeOffset(2026, 10, 3, 10, 10, 0, TimeSpan.Zero),
            BlockMinutes = 130,
            FlightMinutes = 112,
            Landed = true,
            LiftoffIasKt = 151.26,
            TouchdownGroundSpeedKt = 131.0,
            TouchdownVerticalSpeedFpm = -183.4,
            TouchdownIasKt = 137.44,
            TouchdownPitchDeg = 4.6,
            Bounces = 1,
            ApproachResult = "stable",
            DefectsRaised = 1,
        };

        var previous = CultureInfo.CurrentCulture;
        string csv;
        try
        {
            // A comma-decimal Windows region must not leak into the file.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            csv = LogbookCsv.Build([flight]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var lines = csv.Split("\r\n");
        Assert.Equal(
            "sessionId,date,origin,destination,departureRunway,arrivalRunway,offBlocksUtc,takeoffUtc,landingUtc,onBlocksUtc,"
            + "blockMinutes,flightMinutes,landed,liftoffIasKt,touchdownGroundSpeedKt,touchdownVerticalSpeedFpm,touchdownIasKt,"
            + "touchdownPitchDeg,bounces,approachResult,abnormals,defectsRaised,defectsRectified,defectsCarried",
            lines[0]);
        Assert.Equal(
            "session-20261003-080000,2026-10-03,EGLL,LIRF,27R,16L,2026-10-03T08:00:05Z,2026-10-03T08:11:05Z,2026-10-03T10:03:01Z,2026-10-03T10:10:00Z,"
            + "130,112,true,151.3,131,-183,137.4,4.6,1,stable,,1,0,0",
            lines[1]);
        Assert.Equal("", lines[2]);     // the file ends with a line break
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Csv_UnknownValuesAreEmptyCells_AndEveryRowHasEveryColumn()
    {
        var csv = LogbookCsv.Build([new LogbookFlight { SessionId = "session-20260701-080000" }]);

        var row = csv.Split("\r\n")[1];
        Assert.Equal("session-20260701-080000,,,,,,,,,,,,false,,,,,,,,,0,0,0", row);
        Assert.Equal(LogbookCsv.Columns.Count, row.Split(',').Length);
    }

    [Fact]
    public void Csv_QuotesCommasAndQuotes_AndDefusesFormulaCells()
    {
        var csv = LogbookCsv.Build(
        [
            new LogbookFlight
            {
                SessionId = "s1",
                Abnormals = ["ENG 1 FIRE, \"LOOP A\"", "=HYPERLINK(\"http://x\")"],
                Origin = "=1+1",
                TouchdownVerticalSpeedFpm = -200,
            },
        ]);

        var row = csv.Split("\r\n")[1];
        Assert.Contains("\"ENG 1 FIRE, \"\"LOOP A\"\"; =HYPERLINK(\"\"http://x\"\")\"", row, StringComparison.Ordinal);
        Assert.Contains(",'=1+1,", row, StringComparison.Ordinal);
        // A negative NUMBER is a number, not a formula to defuse.
        Assert.Contains(",-200,", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_EmptyLogbook_IsJustTheHeader_AndTheFileNameIsSortable()
    {
        Assert.Equal(string.Join(',', LogbookCsv.Columns) + "\r\n", LogbookCsv.Build([]));
        Assert.Equal(
            "prosimcompanion-logbook-20261003-081500.csv",
            LogbookCsv.FileName(new DateTimeOffset(2026, 10, 3, 10, 15, 0, TimeSpan.FromHours(2))));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
