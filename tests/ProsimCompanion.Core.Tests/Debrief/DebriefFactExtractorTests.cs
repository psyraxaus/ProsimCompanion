using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Debrief;
using Xunit;

namespace ProsimCompanion.Core.Tests.Debrief;

public sealed class DebriefFactExtractorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-debrief-").FullName;
    private readonly DebriefFactExtractor _extractor = new(NullLogger<DebriefFactExtractor>.Instance);

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

    [Fact]
    public void Extract_FullSession_MapsEveryFact()
    {
        var path = new SessionLogBuilder()
            .At("09:55:00").Event("session-started")
            .At("09:58:00").Event("flight.route", new { role = "departure", airport = "YSSY", runway = "16R" })
            .At("09:58:30").Event("flight.route", new { role = "arrival", airport = "YMML", runway = "34" })
            .At("10:00:00").Phase("Preflight", "PushbackAndStart")                       // off-block
            .At("10:05:00").Phase("PushbackAndStart", "TaxiOut")                          // not off-block (first wins)
            .At("10:10:00").Phase("TaxiOut", "TakeoffRoll")
            .At("10:11:00").Phase("TakeoffRoll", "InitialClimb", iasKt: 152.4)            // liftoff
            .At("10:12:00").Event("callout.fired", new { id = "positiveClimb" })
            .At("10:15:00").Event("flow.advisory", new { id = "landingLights", spoken = true })
            .At("10:16:00").Event("flow.advisory", new { id = "landingLights", spoken = true })   // dedupe
            .At("10:17:00").Event("flow.advisory", new { id = "seatbelts", spoken = false, reason = "rate-limited" })
            .At("10:20:00").Event("failure.detected", new { id = "apu-fault", title = "APU FAULT" })
            .At("10:25:00").Event("failure.cleared", new { id = "apu-fault" })
            .At("10:26:00").Event("failure.detected", new { id = "pack-1-fault", title = "PACK 1 FAULT" })
            .At("10:30:00").Event("checklist.voice", new { name = "after takeoff", phase = "start" })
            .At("10:31:00").Event("checklist.voice", new { name = "after takeoff", phase = "end" })
            .At("10:32:00").Event("checklist.voice", new { name = "descent", phase = "cancelled" })
            .At("10:33:00").Event("fuel.check", new { fobKg = 7900.0 })                 // a cruise check: NOT a debrief fuel source (#155)
            .At("10:33:30").Event("flight-times", new { takeoffUtc = "2026-01-01T10:16:00Z", takeoffFobKg = 8200.0 })
            .At("10:40:00").Event("cabin.report", new { report = "cabin.ready" })
            .At("10:41:00").Event("radio.set", new { box = 1, khz = 128500 })
            .At("10:42:00").Event("radio.swapped", new { box = 1, khz = 121500 })
            .At("10:43:00").Event("drill.completed", new { id = "drill-tcas-ra" })
            .At("10:44:00").Event("approach.gate", new
            {
                gate = "1000",
                aglFt = 1000.0,
                result = "unstable",
                criteria = new object[]
                {
                    new { name = "gear", value = "down", limit = "down", result = "Pass" },
                    new { name = "sink", value = "1400 fpm", limit = "<= 1000 fpm", result = "Fail" },
                },
            })
            .At("10:45:00").Event("approach.gate", new { gate = "500", aglFt = 500.0, result = "stable", criteria = Array.Empty<object>() })
            .At("10:46:00").Event("fuel.check", new { fobKg = 7100.0 })
            .At("10:52:30").Event("flight-times", new { landingUtc = "2026-01-01T10:52:00Z", landingFobKg = 6800.0 })
            .At("11:00:30").Event("flight-times", new { onBlocksUtc = "2026-01-01T11:00:00Z", onBlocksFobKg = 6700.0 })
            .At("10:52:00").Phase("Approach", "LandingRollout", groundSpeedKt: 128.6)     // touchdown
            .At("10:55:00").Event("techlog.raised", new { id = "def-1", title = "x" })
            .At("10:56:00").Event("techlog.rectified", new { id = "def-0", title = "y" })
            .At("10:57:00").Event("techlog.briefed", new { open = 3, onCommand = false })
            .At("10:58:00").Event("speech.suppressed", new { tag = "advisory" })
            .At("10:59:00").Event("callout.degraded", new { id = "vls", reason = "no data" })
            .At("11:00:00").Phase("TaxiIn", "Shutdown")                                   // on-block (first)
            .At("11:20:00").Phase("Preflight", "Shutdown")                                // must NOT stretch block time
            .At("11:20:30").Event("techlog.carried", new { sessionId = "s", open = 2 })
            .Write(_dir);

        var facts = _extractor.Extract(path);

        Assert.Equal(60, facts.BlockMinutes);      // 10:00 → 11:00, first Shutdown wins
        Assert.Equal(41, facts.FlightMinutes);     // 10:11 → 10:52
        Assert.Equal(152.4, facts.LiftoffIasKt);
        Assert.Equal(128.6, facts.TouchdownGroundSpeedKt);

        Assert.Equal(2, facts.Gates.Count);
        Assert.Equal("1000", facts.Gates[0].Name);
        Assert.Equal(1000.0, facts.Gates[0].AglFt);
        Assert.Equal("unstable", facts.Gates[0].Result);
        Assert.Equal("sink", facts.Gates[0].FailingCriterion);
        Assert.Null(facts.Gates[1].FailingCriterion);

        Assert.Equal(1, facts.CalloutsFired);
        Assert.Equal(1, facts.SpeechSuppressed);
        Assert.Equal(1, facts.Degradations);
        Assert.Equal(1, facts.ChecklistsCompleted);
        Assert.Equal(["after takeoff"], facts.ChecklistNames);
        Assert.Equal(["landingLights"], facts.Advisories);
        Assert.Equal(1, facts.CabinReports);
        Assert.Equal(2, facts.RadioTunes);
        Assert.Equal(1, facts.MemoryDrills);

        Assert.Equal(8200.0, facts.StartFobKg);
        Assert.Equal(6700.0, facts.FinalFobKg);
        Assert.Equal(1500.0, facts.FuelUsedKg);

        Assert.Equal(1, facts.DefectsRaised);
        Assert.Equal(1, facts.DefectsRectified);
        Assert.Equal(3, facts.DefectsCarried);     // max of briefed(3) and carried(2)

        Assert.Equal(2, facts.Abnormals.Count);
        Assert.Equal(new AbnormalFact("APU FAULT", Cleared: true), facts.Abnormals[0]);
        Assert.Equal(new AbnormalFact("PACK 1 FAULT", Cleared: false), facts.Abnormals[1]);

        Assert.Equal("YSSY", facts.Origin);
        Assert.Equal("16R", facts.DepartureRunway);
        Assert.Equal("YMML", facts.Destination);
        Assert.Equal("34", facts.ArrivalRunway);
        Assert.True(facts.HasData);
    }

    [Fact]
    public void Extract_SkipsUnparseableLines()
    {
        var path = new SessionLogBuilder()
            .At("10:00:00").Event("callout.fired", new { id = "a" })
            .RawLine("{ torn line, not json")
            .RawLine("")
            .At("10:01:00").Event("callout.fired", new { id = "b" })
            .Write(_dir);

        Assert.Equal(2, _extractor.Extract(path).CalloutsFired);
    }

    [Fact]
    public void Extract_MissingFileOrBlankPath_YieldsEmpty()
    {
        Assert.Same(DebriefFacts.Empty, _extractor.Extract(Path.Combine(_dir, "nope.jsonl")));
        Assert.Same(DebriefFacts.Empty, _extractor.Extract(""));
    }

    [Fact]
    public void Extract_NoTimesWithoutOrderedEndpoints()
    {
        // Shutdown before any off-block: no block time; touchdown without liftoff: no flight time.
        var path = new SessionLogBuilder()
            .At("10:00:00").Phase("TaxiIn", "Shutdown")
            .At("10:05:00").Phase("Approach", "LandingRollout", groundSpeedKt: 120)
            .Write(_dir);

        var facts = _extractor.Extract(path);
        Assert.Null(facts.BlockMinutes);
        Assert.Null(facts.FlightMinutes);
        Assert.Equal(120, facts.TouchdownGroundSpeedKt);
    }

    [Fact]
    public void Extract_ReadsWhileWriterHoldsTheFile()
    {
        var path = new SessionLogBuilder()
            .At("10:00:00").Event("callout.fired", new { id = "a" })
            .Write(_dir);

        // Simulate the live event-log writer: open for append, sharing reads only.
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        Assert.Equal(1, _extractor.Extract(path).CalloutsFired);
    }

    /// <summary>Issue #155: the owner's EGLL→EFHK debrief said "burned 1.7 t, landing with 5.1 t"
    /// because the fuel came from the FIRST and LAST cruise fuel check. Cruise checks are not a
    /// fuel source; the flight-times stamps are, and a session without them says nothing.</summary>
    [Fact]
    public void Extract_FuelComesFromTheFlightTimeStamps_NeverFromCruiseChecks()
    {
        var withChecksOnly = new SessionLogBuilder()
            .At("22:30:41").Event("fuel.check", new { fobKg = 6827.0, fix = "GREFI" })
            .At("23:00:51").Event("fuel.check", new { fobKg = 5953.0, fix = "LOBBI" })
            .At("23:30:51").Event("fuel.check", new { fobKg = 5100.0, fix = "ALAMI" })
            .Write(_dir);

        var silent = _extractor.Extract(withChecksOnly);
        Assert.Null(silent.StartFobKg);
        Assert.Null(silent.FinalFobKg);
        Assert.Null(silent.FuelUsedKg);

        var withStamps = new SessionLogBuilder()
            .At("21:38:10").Event("flight-times", new { takeoffUtc = "2026-10-03T06:46:26Z", takeoffFobKg = 9350.0 })
            .At("22:30:41").Event("fuel.check", new { fobKg = 6827.0, fix = "GREFI" })
            .At("23:30:51").Event("fuel.check", new { fobKg = 5100.0, fix = "ALAMI" })
            .At("00:03:16").Event("flight-times", new { landingUtc = "2026-10-03T09:11:32Z", landingFobKg = 4530.0 })
            .Write(_dir);

        var landedNotYetOnBlocks = _extractor.Extract(withStamps);
        Assert.Equal(9350.0, landedNotYetOnBlocks.StartFobKg);
        Assert.Equal(4530.0, landedNotYetOnBlocks.FinalFobKg);      // landing fuel stands in until on blocks
        Assert.Equal(4820.0, landedNotYetOnBlocks.FuelUsedKg);
    }

    /// <summary>Issue #164 (Mario, speech off, EDDM→LHDC 2026-10-10): no briefing ran, so no
    /// flight.route was written and the logbook, debrief and duty-day leg had no airports. The
    /// planned route from ofp.loaded (or the legacy airport-coordinates shape) stands in; the
    /// last plan loaded BEFORE the landing wins and the return plan loaded after it is ignored.</summary>
    [Fact]
    public void Extract_FallsBackToThePlannedRoute_WhenNoBriefingNamedIt()
    {
        var ofpOnly = new SessionLogBuilder()
            .At("09:18:48").Event("ofp.loaded", new { requestId = "1", origin = "EDDM", destination = "LHBP", flightNumber = "DLH1687" })
            .At("09:20:00").Event("ofp.loaded", new { requestId = "2", origin = "EDDM", destination = "LHDC", flightNumber = "DLH1687" }) // re-planned before departure
            .At("10:25:45").Phase("Preflight", "PushbackAndStart")
            .At("11:56:14").Phase("Approach", "LandingRollout", groundSpeedKt: 130.0)
            .At("12:07:55").Phase("TaxiIn", "Shutdown")
            .At("12:09:05").Event("ofp.loaded", new { requestId = "3", origin = "LHDC", destination = "EDDM", flightNumber = "DLH1688" }) // next sector's plan
            .Write(_dir, "session-ofp-only");

        var planned = _extractor.Extract(ofpOnly);
        Assert.Equal("EDDM", planned.Origin);
        Assert.Equal("LHDC", planned.Destination);

        var legacyShape = new SessionLogBuilder()
            .At("09:18:48").Event("airport-coordinates", new
            {
                attempt = 1,
                origin = new { icao = "EDDM", found = true },
                destination = new { icao = "LHDC", found = true },
            })
            .Write(_dir, "session-legacy");

        var legacy = _extractor.Extract(legacyShape);
        Assert.Equal("EDDM", legacy.Origin);
        Assert.Equal("LHDC", legacy.Destination);

        var briefed = new SessionLogBuilder()
            .At("09:18:48").Event("ofp.loaded", new { requestId = "1", origin = "EDDM", destination = "LHDC" })
            .At("11:00:00").Event("flight.route", new { role = "arrival", airport = "LHBP", runway = "31L" }) // diverted
            .Write(_dir, "session-briefed");

        var reality = _extractor.Extract(briefed);
        Assert.Equal("EDDM", reality.Origin);      // plan fills the gap the briefing left
        Assert.Equal("LHBP", reality.Destination); // the briefing's route wins where it exists
    }
}
