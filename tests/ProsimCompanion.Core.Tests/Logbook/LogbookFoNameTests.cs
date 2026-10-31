using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logbook;

/// <summary>The roster FO's name on the leg (issue #194): extracted from the session, carried
/// on the logbook flight and its CSV, and absent (null) on files from before the field.</summary>
public sealed class LogbookFoNameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-logbook-fo-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LogbookService Create(string logbookPath)
    {
        var sessions = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(sessions);
        var service = new LogbookService(
            new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance),
            new JsonlEventLog(sessions, NullLogger<JsonlEventLog>.Instance),
            OptionsSupport.Monitor(new LogbookOptions { Path = logbookPath }),
            NullLogger<LogbookService>.Instance);
        service.Start();
        return service;
    }

    [Fact]
    public void OlderLogbook_WithoutFoName_LoadsUnchanged_AndTheCsvHasTheColumnLast()
    {
        var path = Path.Combine(_dir, "logbook.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "flights": [
                { "sessionId": "s1", "date": "2026-08-01", "origin": "YSSY", "destination": "YMML",
                  "blockMinutes": 90, "flightMinutes": 75, "landed": true, "approachResult": "stable" }
              ]
            }
            """);

        var service = Create(path);

        var flight = Assert.Single(service.Flights);
        Assert.Null(flight.FoName);
        var rows = LogbookCsv.Build(service.Flights).Split("\r\n");
        Assert.Equal("foName", LogbookCsv.Columns[^1]);
        Assert.EndsWith(",foName", rows[0], StringComparison.Ordinal);
        Assert.EndsWith(",", rows[1], StringComparison.Ordinal); // the last cell is empty, not missing
    }

    [Fact]
    public void Extractor_ReadsTheFoNameFromThePersonaEvents_AndTheFlightCarriesIt()
    {
        var sessions = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(sessions);
        var sessionPath = new SessionLogBuilder()
            .At("09:50:00").Event("persona.rosterDraw", new { id = "sam", name = "Sam", reason = "sim-day" })
            .At("09:58:00").Event("flight.route", new { role = "departure", airport = "YSSY", runway = "16R" })
            .At("09:59:00").Event("persona.foOnDuty", new { id = "sam", name = "Sam" })
            .At("10:00:00").Phase("Preflight", "PushbackAndStart")
            .At("10:11:00").Phase("TakeoffRoll", "InitialClimb", iasKt: 150)
            .At("10:52:00").Phase("Approach", "LandingRollout", groundSpeedKt: 130)
            .At("11:00:00").Phase("TaxiIn", "Shutdown")
            .Write(sessions, "session-20261101-100000");

        var facts = new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance).Extract(sessionPath);
        Assert.Equal("Sam", facts.FoName);

        var service = Create(Path.Combine(_dir, "logbook.json"));
        service.FoldSession(sessionPath);
        var flight = Assert.Single(service.Flights);
        Assert.Equal("Sam", flight.FoName);
        Assert.EndsWith(",Sam\r\n", LogbookCsv.Build(service.Flights), StringComparison.Ordinal);
    }
}
