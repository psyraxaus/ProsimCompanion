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
    private readonly FakeLegBoundaryEvents _legEvents = new();
    private readonly ProsimCompanion.Core.Aircraft.Ofp.OfpStore _ofp = new();
    private readonly ProsimCompanion.Core.State.GroundOpsSignals _signals = new();
    private readonly ProsimCompanion.Core.State.EndOfDutyOfferStore _offer;
    private readonly DutyDaySignals _dutySignals = new();
    private readonly List<DutyDayEnded> _ended = [];
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
        _techLog.SetupGet(t => t.Defects).Returns([]);
        _offer = new ProsimCompanion.Core.State.EndOfDutyOfferStore(_signals);
        _dutySignals.DayEnded += e => _ended.Add(e);
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
        NullLogger<CompanyDayService>.Instance,
        new ProsimCompanion.Core.Speech.SpokenText(),
        _ofp,
        _legEvents,
        _signals,
        _offer,
        _dutySignals);

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

    // ---- next leg (ADR-0019 §1/§5, issue #212: the leg begins on its plan) ----

    /// <summary>EHAM 2026-11-01: thirty seconds after deboarding the Preflight edge started
    /// "Leg 2. New sector" with the plan just flown as its route. The edge now does nothing to
    /// the legs; the next leg waits for a different OFP.</summary>
    [Fact]
    public void PreflightInTurnaround_DoesNotStartALeg_TheNewPlanDoes()
    {
        _service.Start();
        _ofp.Set(Ofp("YSSY", "YMML", "QF1", id: "1"));
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with
        {
            BlockMinutes = 80,
            Origin = "YSSY",
            Destination = "YMML",
        });
        var firstSession = _eventLog.Path;

        _service.HandlePhase(FlightPhase.Preflight, T0.AddMinutes(91));

        Assert.Equal(firstSession, _eventLog.Path); // no rotation for a leg that does not exist
        var parked = _store.Snapshot();
        Assert.Equal("Turnaround", parked.View!.State);
        Assert.Equal(1, parked.View.LegIndex);
        Assert.Single(parked.Legs);
        Assert.DoesNotContain(_arbiter.Requests, r => r.Text.StartsWith("Leg 2.", StringComparison.Ordinal));

        // The return plan lands: now the leg begins, with THAT plan as its route.
        _ofp.Set(Ofp("YMML", "YSSY", "QF2", id: "2"));
        _legEvents.RaiseLegBegan("req:2", "req:1");

        Assert.NotEqual(firstSession, _eventLog.Path); // rotated at next-leg start
        var snapshot = _store.Snapshot();
        Assert.Equal("OnLeg", snapshot.View!.State);
        Assert.Equal(2, snapshot.View.LegIndex);
        var leg = snapshot.Legs.Single(l => l.Index == 2);
        Assert.Equal("YMML", leg.From);
        Assert.Equal("YSSY", leg.To);
        Assert.Equal("QF2", leg.FlightNo);
        var sector = Assert.Single(_arbiter.Requests, r => r.Text.StartsWith("Leg 2. New sector", StringComparison.Ordinal));
        Assert.Equal("Leg 2. New sector, Yankee Mike Mike Lima to Yankee Sierra Sierra Yankee.", sector.Text);
        Assert.Empty(_company.Messages); // progressive mode has no plan to read
    }

    [Fact]
    public void NewPlan_WithoutARoute_SaysTheShortLine()
    {
        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with { BlockMinutes = 80 });

        _service.HandleLegBegan("req:2", T0.AddMinutes(130));

        Assert.Contains(_arbiter.Requests, r => r.Text == "Leg 2. New sector.");
    }

    [Fact]
    public void PlannedDay_DeliversNextSectorCompanyMessage()
    {
        WritePlannedRotation();
        _service.TryStartDay("test", T0);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Duty day started — 2 sectors planned.");

        CompleteLegOne(T0.AddMinutes(125), DebriefFacts.Empty with { BlockMinutes = 50, Destination = "EGCC" });
        _service.HandleLegBegan("req:2", T0.AddMinutes(150));

        var message = Assert.Single(_company.Messages);
        // No IAirportNames registered here, so the ICAOs fall back to spelled form (#70).
        Assert.Equal("Next sector, Echo Golf Charlie Charlie to Echo Golf Lima Lima, flight BA124, scheduled off-blocks 09:00 zulu.", message);
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
        Assert.Equal("Note — the plan showed Echo Golf Charlie Charlie, but we flew to Echo Golf Bravo Bravo.", note.Text);
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
    public void IdleTurnaround_AutoClosesAfterTheConfiguredIdleTime_FromOnBlocks()
    {
        Assert.Equal(300, _options.AutoCloseIdleMinutes); // the 1.0 default (ADR-0019 §8)
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));

        _service.ProcessTick(T0.AddMinutes(90 + 299));
        Assert.True(_store.Snapshot().View!.Active); // not yet

        _service.ProcessTick(T0.AddMinutes(90 + 300));
        Assert.False(_store.Snapshot().View!.Active);
        Assert.Contains(_arbiter.Requests,
            r => r.Text.StartsWith("That's the duty day complete.", StringComparison.Ordinal));
        Assert.Equal("idle", Assert.Single(_ended).Source);
    }

    [Fact]
    public void IdleClock_CountsFromDeboardingComplete_NotFromPhaseEdgesOrRestarts()
    {
        _options.AutoCloseIdleMinutes = 90;
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));

        // Deboarding completes 20 min after on-blocks: the clock restarts there (EHAM 2026-11-01
        // was 88 min from shutdown to the new plan against the old 90-minute edge-reset clock).
        _service.HandleDeboarded(T0.AddMinutes(110));
        Assert.Contains(ReadEvents(), e => e.Contains("\"day.idle.started\"", StringComparison.Ordinal));

        // Phase edges inside the turnaround (the Preflight after deboarding, a ColdAndDark) and
        // a restart (a fresh service over the same state file) do NOT touch the clock.
        _service.HandlePhase(FlightPhase.Preflight, T0.AddMinutes(111));
        _service.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(150));
        using var restarted = Create();
        restarted.Start();

        restarted.ProcessTick(T0.AddMinutes(110 + 89));
        Assert.True(_store.Snapshot().View!.Active); // 89 min since deboarding
        restarted.ProcessTick(T0.AddMinutes(110 + 90));
        Assert.False(_store.Snapshot().View!.Active); // 90 min since deboarding, whatever the edges did
    }

    [Fact]
    public void IdleClose_NeverFires_WhenDisabledOrOnLeg_AndANewPlanStopsTheClock()
    {
        _options.AutoCloseIdleMinutes = 0;
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));
        _service.ProcessTick(T0.AddMinutes(90 + 600));
        Assert.True(_store.Snapshot().View!.Active);

        // A new plan begins the next leg: the backstop clock stops.
        _options.AutoCloseIdleMinutes = 90;
        _service.HandleLegBegan("req:2", T0.AddMinutes(700));
        _service.ProcessTick(T0.AddMinutes(700 + 600));
        Assert.True(_store.Snapshot().View!.Active);
    }

    // ---- the FO's end-of-duty offer (ADR-0019 §8, #217) ----

    [Fact]
    public void Offer_ComesOnce_AfterTenMinutesColdAndDarkInATurnaround_AndYesEndsTheDay()
    {
        _service.Start(); // hooks the offer store's answer
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));
        _service.HandleDeboarded(T0.AddMinutes(100));

        // Powered and parked: no offer however long.
        _service.ProcessTick(T0.AddMinutes(100 + 60));
        Assert.False(_offer.Asking.QuestionStands);

        // Cold and dark at +170: the offer waits ten minutes from the dark edge, not the deboarding.
        _service.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(170));
        _service.ProcessTick(T0.AddMinutes(179));
        Assert.False(_offer.Asking.QuestionStands);
        _service.ProcessTick(T0.AddMinutes(180));
        Assert.True(_offer.Asking.QuestionStands);
        Assert.Equal(CompanyDayService.EndOfDutyPrompt, _offer.Asking.Question!.Prompt);
        Assert.Contains(ReadEvents(), e => e.Contains("\"day.end-offer.raised\"", StringComparison.Ordinal));

        // Yes (the dialogue's accept) ends the day as "offer".
        Assert.True(_offer.Accept("voice"));
        Assert.False(_store.Snapshot().View!.Active);
        Assert.Equal("offer", Assert.Single(_ended).Source);
    }

    [Fact]
    public void Offer_DeclinedOrSilent_LeavesTheDayOpen_AndIsNeverRepeatedThatDay()
    {
        _service.Start();
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));
        _service.HandleDeboarded(T0.AddMinutes(100));
        _service.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(100));
        _service.ProcessTick(T0.AddMinutes(110));
        Assert.True(_offer.Asking.QuestionStands);

        Assert.True(_offer.Decline("voice", "captain said no"));
        Assert.True(_store.Snapshot().View!.Active);
        Assert.Empty(_ended);

        // Hours later, still dark: never asked again (the day state remembers the offer) —
        // and a restart does not forget it either.
        _service.ProcessTick(T0.AddMinutes(400));
        Assert.False(_offer.Asking.QuestionStands);
        using var restarted = Create();
        restarted.Start();
        restarted.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(401));
        restarted.ProcessTick(T0.AddMinutes(420));
        Assert.False(_offer.Asking.QuestionStands);
    }

    [Fact]
    public void Offer_NeverComes_WhenSwitchedOff_OrPowerCameBack()
    {
        _options.EndOfDutyOfferMinutes = 0;
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));
        _service.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(95));
        _service.ProcessTick(T0.AddMinutes(200));
        Assert.False(_offer.Asking.QuestionStands);

        // On, but the power came back at +200 (Preflight): the dark wait starts over.
        _options.EndOfDutyOfferMinutes = 10;
        _service.HandlePhase(FlightPhase.Preflight, T0.AddMinutes(200));
        _service.ProcessTick(T0.AddMinutes(230));
        Assert.False(_offer.Asking.QuestionStands);
        _service.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(231));
        _service.ProcessTick(T0.AddMinutes(241));
        Assert.True(_offer.Asking.QuestionStands);
    }

    // ---- end of duty: the debrief and the signal (ADR-0019 §8, #218/#222) ----

    [Fact]
    public void EndDay_RaisesTheDutyDaySignal_WithdrawsTheOffer_AndTagsTheDebriefHoldExempt()
    {
        _service.TryStartDay("test", T0);
        _service.HandlePhase(FlightPhase.Shutdown, T0.AddMinutes(90));
        _service.HandleDeboarded(T0.AddMinutes(100));
        _service.HandlePhase(FlightPhase.ColdAndDark, T0.AddMinutes(100));
        _service.ProcessTick(T0.AddMinutes(110));
        Assert.True(_offer.Asking.QuestionStands);

        Assert.True(_service.TryEndDay("web", T0.AddMinutes(115)));

        var ended = Assert.Single(_ended);
        Assert.Equal("web", ended.Source);
        Assert.False(_offer.Asking.QuestionStands); // the button answered the standing offer
        var debrief = Assert.Single(_arbiter.Requests, r => r.Text.StartsWith("That's the duty day complete.", StringComparison.Ordinal));
        Assert.Equal(CompanyDayService.DebriefTag, debrief.Tag);
        Assert.False(ProsimCompanion.Speech.Arbiter.DialogueHoldRule.Holds(debrief));
    }

    [Fact]
    public void EndDay_DebriefNamesFuelAndTheTechLogEntriesRaisedToday()
    {
        _techLog.SetupGet(t => t.Defects).Returns(
        [
            new TechLogDefect { Id = "d1", Title = "APU bleed valve fault", RaisedDate = T0.ToString("yyyy-MM-dd"), RaisedFlight = "other-session" },
            new TechLogDefect { Id = "d2", Title = "Cabin reading light 12C", RaisedDate = "2020-01-01", RaisedFlight = Path.GetFileNameWithoutExtension(_eventLog.Path) },
            new TechLogDefect { Id = "d3", Title = "Old entry from last week", RaisedDate = T0.AddDays(-7).ToString("yyyy-MM-dd"), RaisedFlight = "old" },
        ]);

        _service.TryStartDay("test", T0);
        CompleteLegOne(T0.AddMinutes(90), DebriefFacts.Empty with { BlockMinutes = 80, Destination = "YMML", FuelUsedKg = 4250 });
        Assert.True(_service.TryEndDay("voice", T0.AddMinutes(100)));

        var debrief = Assert.Single(_arbiter.Requests, r => r.Text.StartsWith("That's the duty day complete.", StringComparison.Ordinal));
        Assert.Contains(" Fuel used 4.3 tonnes.", debrief.Text, StringComparison.Ordinal);
        Assert.Contains(" Tech log entries raised: APU bleed valve fault and Cabin reading light 12C.", debrief.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Old entry", debrief.Text, StringComparison.Ordinal);
        var summaryFile = Assert.Single(Directory.GetFiles(_sessionsDir, "*.summary.txt"));
        Assert.Equal(debrief.Text, File.ReadAllText(summaryFile)); // the written summary says the same
    }

    private IReadOnlyList<string> ReadEvents()
    {
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(_sessionsDir, "*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }

        return lines;
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

    /// <summary>ADR-0019 §8: a plan loaded after the end starts a new duty day — leg 1 seeded
    /// from it, no Preflight edge needed (the aircraft may already be powered).</summary>
    [Fact]
    public void AfterEndedDay_ANewPlanAutoStartsAFreshDay_SeededFromIt()
    {
        _service.TryStartDay("test", T0);
        _service.TryEndDay("test", T0.AddMinutes(30));

        _ofp.Set(Ofp("EGLL", "EHAM", "BAW432", id: "9"));
        _service.HandleLegBegan("req:9", T0.AddMinutes(40));

        var snapshot = _store.Snapshot();
        Assert.True(snapshot.View!.Active);
        Assert.Equal(1, snapshot.View.LegIndex);
        Assert.Equal("EGLL", snapshot.View.From);
        Assert.Equal("EHAM", snapshot.View.To);

        // With the automation off the plan starts nothing.
        _service.TryEndDay("test", T0.AddMinutes(50));
        _options.AutoStart = false;
        _service.HandleLegBegan("req:10", T0.AddMinutes(60));
        Assert.False(_store.Snapshot().View!.Active);
    }

    // ---- the phantom leg of the older build (issue #212, audit row A3) ----

    /// <summary>EGLL 2026-11-01: `daystate.json` ended with `currentLegIndex 3` and a leg 3
    /// EHAM→EGLL that duplicated leg 2 and never moved — the old Preflight-edge start after the
    /// day's last landing. A resume drops it and puts the day back in its turnaround.</summary>
    [Fact]
    public void Restart_DropsThePhantomLegOfAnOlderBuild()
    {
        var engine = new CompanyDayEngine();
        engine.StartDay(null, 15, T0);
        engine.StampOffBlocks(T0.AddMinutes(10));
        engine.SeedRouteFromOfp("EHAM", "EGLL", "BAW433");
        engine.CompleteLeg("session-1", T0.AddMinutes(60));
        var phantom = engine.StartNextLeg()!; // the old build: Preflight edge, flown plan as route
        engine.SeedRouteFromOfp("EHAM", "EGLL", "BAW433");
        Assert.Equal(2, phantom.Index); // leg 2 is the phantom here
        new DayStateFile(OptionsSupport.Monitor(_options), NullLogger<DayStateFile>.Instance).Save(engine.Day!);

        var second = Create();
        try
        {
            second.Start();
            var snapshot = _store.Snapshot();
            Assert.Equal("Turnaround", snapshot.View!.State);
            Assert.Equal(1, snapshot.View.LegIndex);
            Assert.Single(snapshot.Legs);
            Assert.Equal("Leg 1 complete.", second.DebriefContextLine);
        }
        finally
        {
            second.Dispose();
        }

        // Persisted: a third start finds no phantom to drop.
        var third = new CompanyDayEngine();
        third.Resume(new DayStateFile(OptionsSupport.Monitor(_options), NullLogger<DayStateFile>.Instance).Load());
        Assert.Null(third.DropPhantomLeg());
        Assert.Single(third.Day!.Legs);
    }

    [Fact]
    public void DropPhantomLeg_LeavesARealNextLegAlone()
    {
        var engine = new CompanyDayEngine();
        engine.StartDay(null, 15, T0);
        engine.StampOffBlocks(T0.AddMinutes(10));
        engine.SeedRouteFromOfp("EGLL", "EHAM", "BAW432");
        engine.CompleteLeg("session-1", T0.AddMinutes(60));
        engine.StartNextLeg();
        engine.SeedRouteFromOfp("EHAM", "EGLL", "BAW433"); // a different route: the return leg

        Assert.Null(engine.DropPhantomLeg());
        Assert.Equal(2, engine.Day!.Legs.Count);

        // A duplicate route that already pushed back is a flown leg, never a phantom.
        var flown = new CompanyDayEngine();
        flown.StartDay(null, 15, T0);
        flown.StampOffBlocks(T0.AddMinutes(10));
        flown.SeedRouteFromOfp("EGLL", "EGLL", "TRAIN1");
        flown.CompleteLeg("session-1", T0.AddMinutes(60));
        flown.StartNextLeg();
        flown.SeedRouteFromOfp("EGLL", "EGLL", "TRAIN2");
        flown.StampOffBlocks(T0.AddMinutes(90));
        Assert.Null(flown.DropPhantomLeg());
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

    // ---- OFP → open leg route (issue #164) ----

    private static ProsimCompanion.Core.Aircraft.Ofp.OfpData Ofp(string from, string to, string flightNo, string id = "1")
        => new() { RequestId = id, OriginIcao = from, DestinationIcao = to, FlightNumber = flightNo };

    /// <summary>Mario's 2026-10-10 screenshot: "---- → ----" and "Leg 1 · ? → ?" with the OFP
    /// (EDDM→LHDC, DLH1687) loaded from the first minute. The leg now shows it at once.</summary>
    [Fact]
    public void StartDay_SeedsLegOneFromTheLoadedOfp()
    {
        _ofp.Set(Ofp("EDDM", "LHDC", "DLH1687"));
        _service.TryStartDay("web", T0);

        var snapshot = _store.Snapshot();
        Assert.Equal("EDDM", snapshot.View!.From);
        Assert.Equal("LHDC", snapshot.View.To);
        var leg = snapshot.Legs.Single();
        Assert.Equal("EDDM", leg.From);
        Assert.Equal("LHDC", leg.To);
        Assert.Equal("DLH1687", leg.FlightNo);
    }

    [Fact]
    public void OfpLoadedLater_SeedsTheOpenLeg_NeverACompletedOne()
    {
        _service.Start(); // subscribes to the OFP store
        _service.TryStartDay("web", T0);
        Assert.Null(_store.Snapshot().View!.From);

        _ofp.Set(Ofp("EDDM", "LHDC", "DLH1687"));
        Assert.Equal("EDDM", _store.Snapshot().View!.From);
        Assert.Equal("LHDC", _store.Snapshot().View!.To);

        // A wrong-then-right OFP ends right while the leg is open.
        _ofp.Set(Ofp("EDDM", "LHBP", "DLH1687", id: "2"));
        Assert.Equal("LHBP", _store.Snapshot().View!.To);

        // Speech off: no flight.route, so the facts carry no airports — the seed survives.
        CompleteLegOne(T0.AddMinutes(102), DebriefFacts.Empty with { BlockMinutes = 102 });
        var completed = _store.Snapshot().Legs.Single();
        Assert.Equal("EDDM", completed.From);
        Assert.Equal("LHBP", completed.To);

        // The return OFP arrives in the turnaround: the completed leg must not move.
        _ofp.Set(Ofp("LHBP", "EDDM", "DLH1688", id: "3"));
        Assert.Equal("LHBP", _store.Snapshot().Legs.Single().To);

        // Next leg (begun by that plan): From chains, To and the flight number come from the OFP.
        _legEvents.RaiseLegBegan("req:3", "req:2");
        var next = _store.Snapshot().Legs.Single(l => l.Index == 2);
        Assert.Equal("LHBP", next.From);
        Assert.Equal("EDDM", next.To);
        Assert.Equal("DLH1688", next.FlightNo);
        Assert.Equal("EDDM", _store.Snapshot().View!.To);
    }

    [Fact]
    public void ExtractedRoute_StillWinsOverTheOfpSeed_AtShutdown()
    {
        _ofp.Set(Ofp("EDDM", "LHDC", "DLH1687"));
        _service.TryStartDay("web", T0);
        CompleteLegOne(T0.AddMinutes(102), DebriefFacts.Empty with { BlockMinutes = 102, Destination = "LHBP" });

        Assert.Equal("LHBP", _store.Snapshot().Legs.Single().To); // diverted: reality wins
    }

    [Fact]
    public void PlannedDay_KeepsItsPlan_OnlyFillsGapsFromTheOfp()
    {
        WritePlannedRotation(); // EGLL→EGCC BA123
        _ofp.Set(Ofp("EGLL", "EGBB", "BA999"));
        _service.TryStartDay("web", T0);

        var leg = _store.Snapshot().Legs.Single(l => l.Index == 1);
        Assert.Equal("EGCC", leg.To);      // the plan stands — a mismatch stays a detectable deviation
        Assert.Equal("BA123", leg.FlightNo);
    }

    [Fact]
    public void Restart_SeedsTheResumedOpenLegFromTheOfp()
    {
        _service.TryStartDay("web", T0);
        _ofp.Set(Ofp("EDDM", "LHDC", "DLH1687"));

        var second = Create();
        try
        {
            second.Start();
            Assert.Equal("EDDM", _store.Snapshot().View!.From);
            Assert.Equal("LHDC", _store.Snapshot().View!.To);
        }
        finally
        {
            second.Dispose();
        }
    }
}
