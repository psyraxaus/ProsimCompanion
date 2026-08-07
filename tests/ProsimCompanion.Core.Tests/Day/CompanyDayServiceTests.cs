using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Speech.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

public sealed class CompanyDayServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeCompanyChannel : ICompanyChannel
    {
        public List<string> Messages { get; } = [];

        public void DeliverMessage(string text) => Messages.Add(text);
    }

    private readonly string _dir;
    private readonly string _sessionsDir;
    private readonly string _rotationsDir;
    private readonly JsonlEventLog _eventLog;
    private readonly FakePhaseSource _phases = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakeCompanyChannel _company = new();
    private readonly DayStatusStore _store = new();
    private readonly Mock<IDebriefFactExtractor> _extractor = new();
    private readonly Mock<ITechLogService> _techLog = new();
    private readonly Mock<ILogbookService> _logbook = new();
    private readonly DayOptions _options;
    private readonly CompanyDayService _service;

    public CompanyDayServiceTests()
    {
        _dir = Directory.CreateTempSubdirectory("pc-day-").FullName;
        _sessionsDir = Path.Combine(_dir, "sessions");
        _rotationsDir = Path.Combine(_dir, "rotations"); // not created — progressive by default
        Directory.CreateDirectory(_sessionsDir);
        _eventLog = new JsonlEventLog(_sessionsDir, NullLogger<JsonlEventLog>.Instance);
        _options = new DayOptions
        {
            Enabled = true,
            Path = Path.Combine(_dir, "daystate.json"),
            RotationsFolder = _rotationsDir,
        };
        _extractor.Setup(e => e.Extract(It.IsAny<string>())).Returns(DebriefFacts.Empty);
        _techLog.SetupGet(t => t.OpenDefects).Returns([]);
        _service = Create();
    }

    private CompanyDayService Create() => new(
        _phases,
        _eventLog,
        _extractor.Object,
        _techLog.Object,
        _logbook.Object,
        new DaySummaryComposer(),
        new DayStateFile(OptionsSupport.Monitor(_options), NullLogger<DayStateFile>.Instance),
        _store,
        _company,
        _arbiter,
        OptionsSupport.Monitor(_options),
        NullLogger<CompanyDayService>.Instance);

    public void Dispose()
    {
        _service.Dispose();
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WritePlannedRotation()
    {
        Directory.CreateDirectory(_rotationsDir);
        File.WriteAllText(Path.Combine(_rotationsDir, "rotation.json"), """
            {
              "dayId": "2026-08-08-A",
              "reportTimeUtc": "2026-08-08T05:30:00Z",
              "legs": [
                { "from": "EGLL", "to": "EGCC", "flightNo": "BA123", "scheduledOffUtc": "2026-08-08T07:15:00Z", "scheduledOnUtc": "2026-08-08T08:05:00Z" },
                { "from": "EGCC", "to": "EGLL", "flightNo": "BA124", "scheduledOffUtc": "2026-08-08T09:00:00Z", "scheduledOnUtc": "2026-08-08T09:55:00Z" }
              ]
            }
            """);
    }

    private void CompleteLegOne(DateTimeOffset shutdownAt, DebriefFacts facts)
    {
        _service.HandlePhase(FlightPhase.PushbackAndStart, shutdownAt.AddMinutes(-80));
        _service.HandlePhase(FlightPhase.Shutdown, shutdownAt);
        _extractor.Setup(e => e.Extract(It.IsAny<string>())).Returns(facts);
        var sessionId = Path.GetFileNameWithoutExtension(_eventLog.Path);
        _service.FillLegFacts(new SessionFinalizationContext(_eventLog.Path, sessionId), shutdownAt.AddSeconds(2));
    }

    // ---- start / auto-start / voice ----

    [Fact]
    public void AutoStart_BeginsProgressiveDayAtFirstPreflight()
    {
        _service.HandlePhase(FlightPhase.Preflight, T0);

        var view = _store.Snapshot().View;
        Assert.NotNull(view);
        Assert.True(view.Active);
        Assert.Equal("OnLeg", view.State);
        Assert.Equal(1, view.LegIndex);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Duty day started." && r.Tag == "day");
        Assert.True(File.Exists(_options.Path)); // persisted for restart recovery
    }

    [Fact]
    public void AutoStart_RespectsEnabledAndAutoStartFlags()
    {
        _options.Enabled = false;
        _service.HandlePhase(FlightPhase.Preflight, T0);
        Assert.Null(_store.Snapshot().View);

        _options.Enabled = true;
        _options.AutoStart = false;
        _service.HandlePhase(FlightPhase.Preflight, T0);
        Assert.Null(_store.Snapshot().View);

        // An explicit voice start always wins over the automation toggles.
        Assert.True(_service.TryHandle("start duty day"));
        Assert.NotNull(_store.Snapshot().View);
    }

    [Fact]
    public void Voice_StartTwice_SaysAlreadyRunning()
    {
        Assert.True(_service.TryHandle("start duty day"));
        Assert.True(_service.TryHandle("begin duty day"));
        Assert.Contains(_arbiter.Requests, r => r.Text == "Duty day is already running.");
    }

    [Fact]
    public void Voice_EndWithoutDay_SaysNoDayRunning()
    {
        Assert.True(_service.TryHandle("end the duty day"));
        Assert.Contains(_arbiter.Requests, r => r.Text == "No duty day is running.");
    }

    [Fact]
    public void Voice_IgnoresUnrelatedUtterances()
        => Assert.False(_service.TryHandle("start the pushback"));

    // ---- off-blocks / shutdown / turnaround ----

    [Fact]
    public void OffBlocks_StampedOnceAtFirstMovement()
    {
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.PushbackAndStart, T0.AddMinutes(10));
        _service.HandlePhase(FlightPhase.TaxiOut, T0.AddMinutes(14));

        var leg = _store.Snapshot().Legs.Single();
        Assert.Equal(T0.AddMinutes(10).ToString("O"), leg.ActualOffUtc);
    }

    [Fact]
    public void Shutdown_EntersTurnaroundAndStampsTheLegSession()
    {
        _service.TryStartDay("test", T0);
        var sessionId = Path.GetFileNameWithoutExtension(_eventLog.Path);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));

        var snapshot = _store.Snapshot();
        Assert.Equal("Turnaround", snapshot.View!.State);
        Assert.Equal(1, snapshot.View.LegsCompleted);
        Assert.Equal(T0.AddMinutes(90).ToString("O"), snapshot.Legs.Single().ActualOnUtc);

        // The finalization step keyed by that session id fills this leg.
        _extractor.Setup(e => e.Extract(It.IsAny<string>()))
            .Returns(DebriefFacts.Empty with { BlockMinutes = 80 });
        _service.FillLegFacts(new SessionFinalizationContext(_eventLog.Path, sessionId), T0.AddMinutes(90));
        Assert.Equal(80, _store.Snapshot().Legs.Single().BlockMinutes);
    }

    [Fact]
    public void FillLegFacts_SpeaksTurnaroundSummaryWithTechLogItems()
    {
        _techLog.SetupGet(t => t.OpenDefects).Returns([new TechLogDefect(), new TechLogDefect()]);
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with
        {
            BlockMinutes = 80,
            FlightMinutes = 65,
            Origin = "YSSY",
            Destination = "YMML",
            Gates = [new GateFact("500", 500, "stable", null)],
        });

        var summary = Assert.Single(_arbiter.Requests, r => r.Text.StartsWith("That's leg", StringComparison.Ordinal));
        Assert.Equal(
            "That's leg 1 — 80 minutes block. 2 items still in the tech log for the next sector.",
            summary.Text);
        Assert.Equal(SpeechPriority.Low, summary.Priority);

        var leg = _store.Snapshot().Legs.Single();
        Assert.Equal("YSSY", leg.From);
        Assert.Equal("YMML", leg.To);
        Assert.True(leg.Landed);
        Assert.True(leg.Stabilized);
    }

    [Fact]
    public void FillLegFacts_Silent_WhenTurnaroundSummaryDisabledOrForeignSession()
    {
        _options.TurnaroundSummary = false;
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with { BlockMinutes = 80 });
        Assert.DoesNotContain(_arbiter.Requests, r => r.Text.StartsWith("That's leg", StringComparison.Ordinal));

        // A finalization for a session no leg owns must not touch the day.
        _options.TurnaroundSummary = true;
        _service.FillLegFacts(new SessionFinalizationContext(_eventLog.Path, "session-19990101-000000"), T0);
        Assert.DoesNotContain(_arbiter.Requests, r => r.Text.StartsWith("That's leg", StringComparison.Ordinal));
    }

    // ---- next leg ----

    [Fact]
    public void NextPreflight_RotatesSessionAndChainsTheNextLeg()
    {
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with
        {
            BlockMinutes = 80,
            Origin = "YSSY",
            Destination = "YMML",
        });
        var firstSession = _eventLog.Path;

        _service.HandlePhase(FlightPhase.Preflight, T0.AddMinutes(130));

        Assert.NotEqual(firstSession, _eventLog.Path); // rotated only at next-leg start
        var snapshot = _store.Snapshot();
        Assert.Equal("OnLeg", snapshot.View!.State);
        Assert.Equal(2, snapshot.View.LegIndex);
        Assert.Equal("YMML", snapshot.Legs.Single(l => l.Index == 2).From); // From chains previous To
        Assert.Contains(_arbiter.Requests, r => r.Text == "Leg 2. New sector.");
        Assert.Empty(_company.Messages); // progressive mode has no plan to read
    }

    [Fact]
    public void PlannedDay_DeliversNextSectorCompanyMessage()
    {
        WritePlannedRotation();
        _service.TryStartDay("test", T0);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Duty day started — 2 sectors planned.");

        CompleteLegOne(T0.AddMinutes(125), DebriefFacts.Empty with { BlockMinutes = 50, Destination = "EGCC" });
        _service.HandlePhase(FlightPhase.Preflight, T0.AddMinutes(150));

        var message = Assert.Single(_company.Messages);
        Assert.Equal("Next sector, EGCC to EGLL, flight BA124, scheduled off-blocks 09:00 zulu.", message);
    }

    // ---- deviation (post-hoc, at leg completion) ----

    [Fact]
    public void PlannedDay_DestinationMismatch_RecordsDeviationAndFollowsReality()
    {
        WritePlannedRotation();
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(125), DebriefFacts.Empty with { BlockMinutes = 50, Destination = "EGBB" });

        var leg = _store.Snapshot().Legs.Single(l => l.Index == 1);
        Assert.Equal("planned EGCC, flew EGBB", leg.Deviation);
        Assert.Equal("EGBB", leg.To);

        var note = Assert.Single(_arbiter.Requests, r => r.Text.StartsWith("Note —", StringComparison.Ordinal));
        Assert.Equal("Note — the plan showed E G C C, but we flew to E G B B.", note.Text);
        Assert.Equal(SpeechPriority.Normal, note.Priority);
    }

    [Fact]
    public void ProgressiveDay_DestinationIsLearned_NeverADeviation()
    {
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with { BlockMinutes = 80, Destination = "YMML" });

        var leg = _store.Snapshot().Legs.Single();
        Assert.Equal("YMML", leg.To);
        Assert.Null(leg.Deviation);
        Assert.DoesNotContain(_arbiter.Requests, r => r.Text.StartsWith("Note —", StringComparison.Ordinal));
    }

    // ---- end of day / idle close ----

    [Fact]
    public void EndDay_SpeaksSummary_PersistsIt_AndFoldsTheLogbookRecord()
    {
        LogbookDay? recorded = null;
        _logbook.Setup(l => l.RecordDay(It.IsAny<LogbookDay>())).Callback<LogbookDay>(d => recorded = d);

        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with { BlockMinutes = 80, Destination = "YMML" });
        Assert.True(_service.TryEndDay("voice", T0.AddMinutes(100)));

        Assert.False(_store.Snapshot().View!.Active);
        var summary = Assert.Single(
            _arbiter.Requests, r => r.Text.StartsWith("That's the duty day complete.", StringComparison.Ordinal));
        Assert.Equal(SpeechPriority.Low, summary.Priority);
        Assert.Contains("1 sector flown. Total block 1 hour 20 minutes, duty 1 hour 45 minutes.",
            summary.Text, StringComparison.Ordinal);

        Assert.NotNull(recorded);
        Assert.Equal(80, recorded.BlockMinutes);
        Assert.Equal(105, recorded.DutyMinutes); // same DayMath formula as the summary

        var summaryFile = Assert.Single(Directory.GetFiles(_sessionsDir, "*.summary.txt"));
        Assert.Equal(summary.Text, File.ReadAllText(summaryFile));
        Assert.Equal($"{recorded.DayId}.summary.txt", Path.GetFileName(summaryFile));
    }

    [Fact]
    public void IdleTurnaround_AutoClosesAfterTheConfiguredIdleTime()
    {
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));

        _service.ProcessTick(T0.AddMinutes(90 + 30));
        Assert.True(_store.Snapshot().View!.Active); // not yet

        _service.ProcessTick(T0.AddMinutes(90 + 91));
        Assert.False(_store.Snapshot().View!.Active);
        Assert.Contains(_arbiter.Requests,
            r => r.Text.StartsWith("That's the duty day complete.", StringComparison.Ordinal));
    }

    [Fact]
    public void IdleClose_NeverFires_WhenDisabledOrOnLeg()
    {
        _options.AutoCloseIdleMinutes = 0;
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));
        _service.ProcessTick(T0.AddMinutes(90 + 600));
        Assert.True(_store.Snapshot().View!.Active);

        // On a leg (not a turnaround) idle time never closes the day either.
        _options.AutoCloseIdleMinutes = 90;
        _service.HandlePhase(FlightPhase.Preflight, T0.AddMinutes(700));
        _service.ProcessTick(T0.AddMinutes(700 + 600));
        Assert.True(_store.Snapshot().View!.Active);
    }

    [Fact]
    public void AfterEndedDay_NextPreflightAutoStartsAFreshDay()
    {
        _service.TryStartDay("test", T0);
        _service.TryEndDay("test", T0.AddMinutes(30));
        _service.HandlePhase(FlightPhase.Preflight, T0.AddHours(20));

        var view = _store.Snapshot().View;
        Assert.NotNull(view);
        Assert.True(view.Active);
        Assert.Equal(0, view.LegsCompleted);
    }

    // ---- debrief context line + resume ----

    [Fact]
    public void DebriefContextLine_TracksModeAndOpenState()
    {
        Assert.Null(_service.DebriefContextLine);

        _service.TryStartDay("test", T0);
        Assert.Equal("Leg 1 complete.", _service.DebriefContextLine);
        Assert.Equal("Leg 1 complete.", _store.Snapshot().DebriefContextLine);

        _service.TryEndDay("test", T0.AddMinutes(30));
        Assert.Null(_service.DebriefContextLine);
    }

    [Fact]
    public void DebriefContextLine_PlannedMultiLeg_SaysLegOfTotal()
    {
        WritePlannedRotation();
        _service.TryStartDay("test", T0);
        Assert.Equal("Leg 1 of 2 complete.", _service.DebriefContextLine);
    }

    [Fact]
    public void Restart_ResumesTheOpenDayFromDisk()
    {
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));

        var second = Create();
        try
        {
            second.Start();
            Assert.Equal("Turnaround", _store.Snapshot().View!.State);
            Assert.Equal("Leg 1 complete.", second.DebriefContextLine);
        }
        finally
        {
            second.Dispose();
        }
    }
}
