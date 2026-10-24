using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Prosim.Simbrief;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #189: the NOTAM classifier and importer, the step-point derivation and the lead
/// rule, the FTL table lookup and the FDP warning edge.</summary>
public sealed class SmallWinsNovTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);

    // ---- NOTAMs ----

    private static OfpNotam N(string q, string text, string id = "A1/26") => new(id, "EKCH", q, text, null, null, false);

    [Theory]
    [InlineData("QMRLC", "RWY 12 CLSD FOR TKOF AND LDG DUE TO WIP.", NotamHighlightClass.RunwayClosed)]
    [InlineData("QMXLC", "TWY G CLSD BTN STAND G137 AND TWY G5.", NotamHighlightClass.TaxiwayClosed)]
    [InlineData("QICAS", "ILS RWY 22L U/S.", NotamHighlightClass.IlsUnserviceable)]
    [InlineData("QIGAS", "GP RWY 04R U/S.", NotamHighlightClass.IlsUnserviceable)]
    [InlineData("QLRAS", "RWY 22R EDGE LGT U/S.", NotamHighlightClass.LightingUnserviceable)]
    [InlineData("QLXAS", "TWY Z CENTERLINE LIGHTS BTN TWY ZJ AND TWY VK U/S.", NotamHighlightClass.LightingUnserviceable)]
    [InlineData("QLPAS", "PAPI RWY 04L U/S.", NotamHighlightClass.Papi)]
    [InlineData("QMTCH", "RWY 22L THR DISPLACED 300M.", NotamHighlightClass.DisplacedThreshold)]
    [InlineData("", "RWY 09 CLSD DUE WIP", NotamHighlightClass.RunwayClosed)]
    [InlineData("", "ILS RWY 27 NOT AVBL", NotamHighlightClass.IlsUnserviceable)]
    public void Classifier_PicksTheLoudClasses(string q, string text, NotamHighlightClass expected)
    {
        Assert.Equal(expected, NotamHighlighter.Classify(N(q, text)));
    }

    [Theory]
    [InlineData("QMRLT", "RWY 04R TKOF NOT PERMITTED WHEN RVR IS BLW 550M.")]
    [InlineData("QOBXX", "OBST ID WITHDRAWN. REF AIP AIRAC SUP 29/26.")]
    [InlineData("QPDCH", "ALL IFR DEPARTURES: CLIMB GRADIENT 5.5 PERCENT.")]
    [InlineData("QFDLT", "DOCKING GUIDANCE SYSTEM MAY NOT OPERATE.")]
    [InlineData("", "FIREWORKS DISPLAY 2 NM SOUTH OF AD.")]
    public void Classifier_LeavesTheRestOut(string q, string text)
    {
        Assert.Null(NotamHighlighter.Classify(N(q, text)));
    }

    [Fact]
    public void Highlights_OrderByClass_EachIdOnce_FilterByStation()
    {
        var notams = new List<OfpNotam>
        {
            N("QLPAS", "PAPI RWY 04L U/S.", "C1/26"),
            N("QMRLC", "RWY 12 CLSD.", "A1/26"),
            N("QMRLC", "RWY 12 CLSD.", "A1/26"),
            new("B1/26", "EFHK", "QMXLC", "TWY Z CLSD.", null, null, false),
        };

        var all = NotamHighlighter.Highlights(notams);
        Assert.Equal(["A1/26", "B1/26", "C1/26"], all.Select(h => h.Notam.Id));
        Assert.Equal(["EFHK"], NotamHighlighter.Highlights(notams, "EFHK").Select(h => h.Notam.Icao));
        Assert.Equal("RWY 12 CLSD.", NotamHighlighter.ShortText(all[0].Notam));
        Assert.EndsWith("…", NotamHighlighter.ShortText(N("", new string('X', 200))), StringComparison.Ordinal);
    }

    [Fact]
    public void Importer_ReadsOriginObject_DestinationArray_AndTheFirStationRecords_EachIdOnce()
    {
        var ofp = JsonNode.Parse("""
        {
          "origin": { "icao_code": "EFHK", "notam": { "notam_id": "A5818/26", "location_icao": "EFHK", "notam_qcode": "QLXAS", "notam_text": "TWY Z CENTERLINE LIGHTS U/S.", "date_effective": "2026-10-11T10:25:00Z", "date_expire": "2026-10-23T12:00:00Z", "date_expire_is_estimated": "1" } },
          "destination": { "icao_code": "EKCH", "notam": [
            { "notam_id": "A1746/26", "location_icao": "EKCH", "notam_qcode": "QMRLC", "notam_text": "RWY 12 CLSD FOR TKOF AND LDG DUE TO WIP.", "date_effective": "2026-09-22T04:00:00Z", "date_expire": "2026-10-23T18:00:00Z", "date_expire_is_estimated": {} },
            { "notam_id": "A1791/26", "location_icao": "EKCH", "notam_qcode": "QFDLT", "notam_text": "DGS MAY NOT OPERATE.", "date_effective": "2026-09-24T10:59:00Z", "date_expire": "PERM" } ] },
          "notams": { "notamdrec": [
            { "notam_id": "A1746/26", "icao_id": "EKCH", "notam_qcode": "QMRLC", "notam_text": "duplicate", "notam_effective_dtg": "202609220400", "notam_expire_dtg": "202610231800" },
            { "notam_id": "A1747/26", "icao_id": "EKCH", "notam_qcode": "QMRLC", "notam_text": "RWY 30 CLSD FOR TKOF DUE TO WIP.", "notam_effective_dtg": "202609220400", "notam_expire_dtg_estimated": "202610231800" },
            { "notam_id": "Z0001/26", "icao_id": "ESAA", "notam_qcode": "QRRCA", "notam_text": "RESTRICTED AREA ACTIVE.", "notam_effective_dtg": "202610240600" } ] }
        }
        """);

        var notams = SimbriefImportService.ParseNotams(ofp);

        Assert.Equal(["A5818/26", "A1746/26", "A1791/26", "A1747/26"], notams.Select(n => n.Id));
        var origin = notams[0];
        Assert.Equal("EFHK", origin.Icao);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 10, 25, 0, TimeSpan.Zero), origin.EffectiveUtc);
        Assert.True(origin.ExpiryEstimated);
        Assert.Null(notams[2].ExpiresUtc); // PERM
        Assert.False(notams[1].ExpiryEstimated);
        var fir = notams[3];
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero), fir.ExpiresUtc);
        Assert.True(fir.ExpiryEstimated);
        Assert.Equal("RWY 12 CLSD FOR TKOF AND LDG DUE TO WIP.", notams[1].Text); // the destination copy wins over the FIR duplicate

        Assert.Equal([NotamHighlightClass.RunwayClosed, NotamHighlightClass.RunwayClosed, NotamHighlightClass.LightingUnserviceable],
            NotamHighlighter.Highlights(notams).Select(h => h.Class));
    }

    [Fact]
    public void Importer_PlanWithoutNotams_GivesAnEmptyList()
    {
        Assert.Empty(SimbriefImportService.ParseNotams(JsonNode.Parse("""{ "origin": { "icao_code": "EGLL", "notam": "" }, "destination": { "icao_code": "LIRF" } }""")));
        Assert.Empty(SimbriefImportService.ParseNotams(null));
        Assert.Null(SimbriefImportService.NotamTime("PERM"));
        Assert.Null(SimbriefImportService.NotamTime("not a time"));
    }

    // ---- step climbs ----

    private static OfpFix Fix(string ident, double? alt, double lon, bool procedure = false)
        => new(ident, new GeoPoint(50, lon), null, null, alt, procedure);

    private static readonly IReadOnlyList<OfpFix> Navlog =
    [
        Fix("SID1", 5000, 0.0, procedure: true), Fix("TOC", 35000, 1.0), Fix("ABC", 35000, 2.0), Fix("DEF", 35000, 3.0),
        Fix("TOPLA", 37000, 4.0), Fix("GHI", 37000, 5.0), Fix("JKL", 39000, 6.0), Fix("SPIKE", 41000, 6.5), Fix("MNO", 39000, 7.0),
        Fix("TOD", 39000, 8.0), Fix("STAR1", 12000, 9.0, procedure: true),
    ];

    [Fact]
    public void Derive_FindsTheHeldStepsAfterTopOfClimb_NotTheClimbNorASpike()
    {
        var steps = StepClimbPlan.Derive(Navlog);
        Assert.Equal(["TOPLA", "JKL"], steps.Select(s => s.Ident));
        Assert.Equal(370, steps[0].ToFlightLevel);
        Assert.Equal(35000, steps[0].FromFt);
        Assert.Equal(390, steps[1].ToFlightLevel);
    }

    [Fact]
    public void Next_SkipsPassedAndReachedSteps()
    {
        var steps = StepClimbPlan.Derive(Navlog);
        Assert.Equal("TOPLA", StepClimbPlan.Next(steps, Navlog, new GeoPoint(50, 2.5), 35000)!.Ident);
        Assert.Equal("JKL", StepClimbPlan.Next(steps, Navlog, new GeoPoint(50, 2.5), 37000)!.Ident);   // already at 370: the next one up
        Assert.Equal("JKL", StepClimbPlan.Next(steps, Navlog, new GeoPoint(50, 4.6), 35000)!.Ident);   // TOPLA passed
        Assert.Null(StepClimbPlan.Next(steps, Navlog, new GeoPoint(50, 7.5), 39000));
        Assert.Equal("TOPLA", StepClimbPlan.Next(steps, Navlog, null, 35000)!.Ident);                   // no position: the first unreached
    }

    [Fact]
    public void Advisor_SpeaksOnceWithinTheLead_InTheCruise_AndAnswersTheQuestion()
    {
        var speech = new SpeechOptions { StepClimbAdvice = true, StepClimbLeadNm = 20 };
        var phase = new FakePhaseSource();
        phase.SetPhase(FlightPhase.Cruise);
        var data = new FakeFlightSource { Snapshot = new FlightDataSnapshot { IsValid = true, AltitudeFt = 35000 } };
        var progress = new FlightProgressStore();
        var ofp = new OfpStore();
        ofp.Set(new OfpData { Navlog = Navlog });
        var store = new StepClimbStore();
        var arbiter = new FakeArbiter();
        var advisor = new StepClimbAdvisor(
            OptionsSupport.Monitor(speech), OptionsSupport.Monitor(new SayIntentionsOptions { Enabled = true }), phase, data, progress, ofp, store,
            new GroundOpsSignals(), arbiter, SpeechTestSupport.TempEventLog(), NullLogger<StepClimbAdvisor>.Instance);

        // 60 nm west of TOPLA (one degree of longitude at 50N is about 38.6 nm): outside the lead.
        progress.Update(_ => new FlightProgressSnapshot { Position = new GeoPoint(50, 2.45) });
        Assert.Null(advisor.Process(T0));
        Assert.Equal(370, store.Snapshot().NextFlightLevel);
        Assert.Contains("three seven zero", advisor.Answer(T0), StringComparison.Ordinal);
        Assert.Contains("miles ahead", advisor.Answer(T0), StringComparison.Ordinal);

        // 15 nm out: the advice, once, with the SayIntentions offer.
        progress.Update(_ => new FlightProgressSnapshot { Position = new GeoPoint(50, 3.6) });
        var step = advisor.Process(T0.AddMinutes(5));
        Assert.Equal("TOPLA", step!.Ident);
        Assert.Contains(arbiter.Requests, r => r.Text.StartsWith("Planned step to flight level three seven zero at", StringComparison.Ordinal) && r.Text.Contains("request climb", StringComparison.Ordinal));
        Assert.Null(advisor.Process(T0.AddMinutes(6)));
        Assert.True(store.Snapshot().Advised);

        // Not in the cruise: silent even inside the lead.
        phase.SetPhase(FlightPhase.Descent);
        progress.Update(_ => new FlightProgressSnapshot { Position = new GeoPoint(50, 5.7) });
        Assert.Null(advisor.Process(T0.AddMinutes(30)));
    }

    // ---- FTL / FDP ----

    [Theory]
    [InlineData(6, 0, 2, 13 * 60)]
    [InlineData(6, 0, 1, 13 * 60)]
    [InlineData(13, 29, 10, 9 * 60)]
    [InlineData(13, 30, 3, 12 * 60 + 15)]
    [InlineData(17, 0, 5, 9 * 60 + 30)]
    [InlineData(17, 0, 6, 9 * 60)]
    [InlineData(23, 59, 2, 11 * 60)]
    [InlineData(4, 59, 1, 11 * 60)]   // the 17:00–04:59 band wraps midnight
    [InlineData(5, 15, 3, 11 * 60 + 45)]
    [InlineData(5, 45, 4, 11 * 60 + 45)]
    public void FtlTable_LooksUpTheBandAndTheSectorColumn(int hour, int minute, int sectors, int expectedMinutes)
    {
        var report = new DateTimeOffset(2026, 10, 24, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), FtlTable.Default.MaxFdp(report, sectors));
    }

    [Fact]
    public void FtlTable_ShippedFileEqualsTheDefault_AndBadJsonIsRefused()
    {
        var path = Path.Combine(RepoRoot(), "src", "ProsimCompanion.App", "config", "ftl-easa.json");
        var fromFile = FtlTable.Parse(File.ReadAllText(path), path);
        Assert.NotNull(fromFile);
        Assert.Equal(Flat(FtlTable.Default.Rows), Flat(fromFile!.Rows));
        Assert.Equal(Flat(FtlTable.Default.Rows), Flat(FtlTable.Parse(FtlTable.DefaultJson())!.Rows));
        Assert.Null(FtlTable.Parse("{}"));
        Assert.Null(FtlTable.Parse("""{ "rows": [ { "from": "06:00", "to": "13:29", "maxFdp": [ "13:00" ] } ] }"""));
        Assert.Null(FtlTable.Parse("not json"));
    }

    private static IReadOnlyList<string> Flat(IReadOnlyList<FtlRow> rows)
        => rows.Select(r => $"{r.FromMinutes}-{r.ToMinutes}:{string.Join('/', r.MaxFdpMinutes)}").ToList();

    private static DayState Day(string? reportUtc, string? nextScheduledOn, int? nextBlock = null)
        => new()
        {
            DayId = "d",
            StartedUtc = "2026-10-24T07:10:00+00:00",
            ReportTimeUtc = reportUtc,
            Mode = DayMode.Planned,
            Legs =
            [
                new DayLeg { Index = 1, From = "EGLL", To = "LIRF", ActualOffUtc = "2026-10-24T07:00:00+00:00", ActualOnUtc = "2026-10-24T09:30:00+00:00", BlockMinutes = 150 },
                new DayLeg { Index = 2, From = "LIRF", To = "EGLL", ScheduledOffUtc = "2026-10-24T10:30:00+00:00", ScheduledOnUtc = nextScheduledOn, BlockMinutes = nextBlock },
            ],
        };

    private static readonly FdpConfiguration Config = new(FtlTable.Default, 60, Enabled: true);

    [Fact]
    public void Fdp_RotationReport_NextLegInsideTheMaximum_NoWarning()
    {
        var status = FdpCalculator.Evaluate(Day("2026-10-24T06:00:00+00:00", "2026-10-24T13:00:00+00:00"), Config, T0.AddHours(4))!;
        Assert.Equal("rotation", status.ReportSource);
        Assert.Equal(1, status.SectorsFlown);
        Assert.Equal(2, status.SectorsWithNext);
        Assert.Equal(13 * 60, status.MaxFdpMinutes);
        Assert.Equal(4 * 60, status.FdpSoFarMinutes);
        Assert.Equal(7 * 60, status.ProjectedFdpMinutes);
        Assert.Equal(6 * 60, status.MarginMinutes);
        Assert.False(status.Exceeds);
    }

    [Fact]
    public void Fdp_WarningEdge_OneMinuteOverTheMaximum()
    {
        var atTheLimit = FdpCalculator.Evaluate(Day("2026-10-24T06:00:00+00:00", "2026-10-24T19:00:00+00:00"), Config, T0.AddHours(4))!;
        Assert.False(atTheLimit.Exceeds);
        Assert.Equal(0, atTheLimit.MarginMinutes);

        var over = FdpCalculator.Evaluate(Day("2026-10-24T06:00:00+00:00", "2026-10-24T19:01:00+00:00"), Config, T0.AddHours(4))!;
        Assert.True(over.Exceeds);
        Assert.Equal(-1, over.MarginMinutes);
        Assert.Contains("19:01 zulu", DaySummaryComposer.FdpWarningLine(over.ProjectedEndUtc!.Value, over.MaxFdpMinutes, over.ReportUtc, over.SectorsWithNext), StringComparison.Ordinal);
        Assert.Contains("13 hours", DaySummaryComposer.FdpWarningLine(over.ProjectedEndUtc!.Value, over.MaxFdpMinutes, over.ReportUtc, over.SectorsWithNext), StringComparison.Ordinal);
    }

    [Fact]
    public void Fdp_ReportFromTheFirstSession_WhenTheRotationHasNone()
    {
        var status = FdpCalculator.Evaluate(Day(null, "2026-10-24T13:00:00+00:00"), Config, T0.AddHours(4))!;
        Assert.Equal("firstSession", status.ReportSource);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 6, 10, 0, TimeSpan.Zero), status.ReportUtc);
    }

    [Fact]
    public void Fdp_NoScheduleAndNoBlock_HasNoProjection_AndNeverWarns_OffGivesNull()
    {
        var status = FdpCalculator.Evaluate(Day("2026-10-24T06:00:00+00:00", null), Config, T0.AddHours(4))!;
        Assert.Null(status.ProjectedEndUtc);
        Assert.False(status.Exceeds);

        var byBlock = FdpCalculator.Evaluate(Day("2026-10-24T06:00:00+00:00", null, nextBlock: 150), Config, T0.AddHours(4))!;
        Assert.Equal(T0.AddHours(4).AddMinutes(150), byBlock.ProjectedEndUtc);

        Assert.Null(FdpCalculator.Evaluate(Day("2026-10-24T06:00:00+00:00", "2026-10-24T13:00:00+00:00"), Config with { Enabled = false }, T0));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }
}
