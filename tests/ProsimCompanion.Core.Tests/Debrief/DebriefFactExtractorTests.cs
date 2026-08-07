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
            .At("10:33:00").Event("fuel.check", new { fobKg = 8200.0 })
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
            .At("10:46:00").Event("fuel.check", new { fobKg = 6700.0 })
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
}
