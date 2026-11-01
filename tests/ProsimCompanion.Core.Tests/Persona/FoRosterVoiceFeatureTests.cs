using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Persona;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Persona;
using Xunit;

namespace ProsimCompanion.Core.Tests.Persona;

/// <summary>The roster's Speech shell (issue #194): the gate intro once per day, nothing with
/// the roster off, the re-draw behind the captain's question, and the FO voice seam.</summary>
public sealed class FoRosterVoiceFeatureTests : IDisposable
{
    private sealed class FakeClock : ISimLocalTime
    {
        public TimeSpan? LocalTimeOfDay { get; set; } = new TimeSpan(7, 30, 0);

        public DateOnly? LocalDate { get; set; } = new DateOnly(2026, 11, 1);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("pc-roster-feature-").FullName;
    private readonly PersonaOptions _persona = new() { Enabled = true, Roster = { Mode = PersonaRosterOptions.ModeDaily } };
    private readonly FakePhaseSource _phases = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly DayStatusStore _dayStore = new();
    private readonly FakeLegBoundaryEvents _legEvents = new();
    private readonly LegBoundaryStore _legStore = new();
    private readonly FakeClock _clock = new();
    private readonly JsonlEventLog _eventLog;
    private readonly FoRosterChangeStore _change = new(new GroundOpsSignals());
    private FoRosterService _roster = default!;

    public FoRosterVoiceFeatureTests()
    {
        _eventLog = new JsonlEventLog(Path.Combine(_dir, "sessions"), NullLogger<JsonlEventLog>.Instance);
        File.WriteAllText(Path.Combine(_dir, FoRosterService.FileName), """
            { "officers": [
              { "id": "sam", "name": "Sam", "hoursOnType": 2400, "confidence": "steady", "voice": null },
              { "id": "tom", "name": "Tom", "hoursOnType": 320, "confidence": "nervous", "voice": "kokoro:am_adam" }
            ] }
            """);
    }

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

    /// <summary>The leg boundary says a plan is loaded and the leg is being prepared (state
    /// None) — the Preflight edge is then a draw opportunity (ADR-0019 §8).</summary>
    private void PlanLoaded(string planId)
        => _legStore.Update(_ => new LegBoundarySnapshot(LegBoundaryState.None, planId, false, false, DateTimeOffset.UtcNow, "test"));

    /// <summary>Deboarding complete: the plan in the store is the arrival's.</summary>
    private void Turnaround(string planId)
        => _legStore.Update(_ => new LegBoundarySnapshot(LegBoundaryState.Turnaround, planId, true, false, DateTimeOffset.UtcNow, "test"));

    private void OpenDay(int legsCompleted, string dayId = "day-1")
        => _dayStore.Update(new DaySnapshot(new DayView(true, legsCompleted > 0 ? "Turnaround" : "OnLeg", legsCompleted + 1, legsCompleted + 1, legsCompleted, null, null, 0, 0, null), [], null, null, dayId));

    private void NoDay()
        => _dayStore.Update(new DaySnapshot(new DayView(false, "Ended", 1, 1, 1, null, null, 0, 0, null), [], null, null, "day-1"));

    private FoRosterVoiceFeature Create()
    {
        var persona = new Mock<IOptionsMonitor<PersonaOptions>>();
        persona.SetupGet(m => m.CurrentValue).Returns(() => _persona);
        _roster = new FoRosterService(
            persona.Object,
            new FoRosterStateFile(NullLogger<FoRosterStateFile>.Instance, Path.Combine(_dir, "state.json")),
            _eventLog,
            NullLogger<FoRosterService>.Instance,
            configDirectory: _dir)
        {
            Next = _ => 0,
        };
        var styled = new StyledSpeechService(
            SpeechTestSupport.Persona(_persona), persona.Object, SpeechTestSupport.BriefingMonitor(new BriefingOptions()),
            _eventLog, NullLogger<StyledSpeechService>.Instance);
        var feature = new FoRosterVoiceFeature(
            _roster, _change, persona.Object, _phases, _dayStore, _legEvents, _legStore, _arbiter, styled, _eventLog,
            NullLogger<FoRosterVoiceFeature>.Instance, _clock);
        feature.Start();
        return feature;
    }

    private static async Task AwaitIntro(FoRosterVoiceFeature feature)
    {
        if (feature.IntroInFlight is { } task)
        {
            await task;
        }
    }

    private int Intros => _arbiter.Tags.Count(t => t == FoRosterVoiceFeature.IntroTag);

    [Fact]
    public async Task FirstPlanInPreflight_DrawsAndSpeaksTheIntroOnce_ThenNotAgainTheSameDay()
    {
        var feature = Create();

        // Powered, parked, no plan yet: nothing to draw for.
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        Assert.Null(_roster.Current);

        // The day's first plan lands: the draw, and the intro at once (already in Preflight).
        PlanLoaded("req:1");
        _legEvents.RaiseLegBegan("req:1", null, LegBoundaryState.None);
        await AwaitIntro(feature);

        Assert.NotNull(_roster.Current);
        Assert.Equal(1, Intros);
        var intro = _arbiter.Requests.Single(r => r.Tag == FoRosterVoiceFeature.IntroTag);
        Assert.Equal(SpeechPriority.Low, intro.Priority);
        Assert.StartsWith("Morning captain, Sam here", intro.Text, StringComparison.Ordinal);
        Assert.Contains("about 2400 hours", intro.Text, StringComparison.Ordinal);
        Assert.Equal("sim:2026-11-01", _roster.State.DrawnFor);

        // A second leg the same sim day (deboarded, then the return plan): no new draw, no second intro.
        _phases.SetPhase(FlightPhase.Shutdown);
        Turnaround("req:1");
        _phases.SetPhase(FlightPhase.Preflight);
        PlanLoaded("req:2");
        _legEvents.RaiseLegBegan("req:2", "req:1");
        await AwaitIntro(feature);
        Assert.Equal(1, Intros);
        Assert.Equal("sim:2026-11-01", _roster.State.DrawnFor);
    }

    /// <summary>The app started after the plan was loaded (no leg-began edge to hear): the
    /// Preflight with that leg's plan is the opportunity. The arrival's Preflight edge, with
    /// the flown plan still in the store, is not.</summary>
    [Fact]
    public async Task PreflightWithThePlan_Draws_TheArrivalsPreflightDoesNot()
    {
        PlanLoaded("req:1");
        var feature = Create();
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        var first = _roster.Current!.Id;
        Assert.Equal(1, Intros);

        // EHAM 2026-11-01: the sim date rolled over in flight; deboarding complete, Preflight
        // edge, old plan in the store — no draw (the boundary is Turnaround, not a planned leg).
        _clock.LocalDate = new DateOnly(2026, 11, 2);
        _phases.SetPhase(FlightPhase.Shutdown);
        Turnaround("req:1");
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        Assert.Equal(first, _roster.Current!.Id);
        Assert.Equal(1, Intros);
    }

    [Fact]
    public async Task NewSimDay_NewPlan_NoOpenDay_DrawsAgainAndGreetsAgain()
    {
        var feature = Create();
        _phases.SetPhase(FlightPhase.Preflight);
        PlanLoaded("req:1");
        _legEvents.RaiseLegBegan("req:1", null, LegBoundaryState.None);
        await AwaitIntro(feature);
        var first = _roster.Current!.Id;

        _clock.LocalDate = new DateOnly(2026, 11, 2);
        _phases.SetPhase(FlightPhase.Shutdown);
        _phases.SetPhase(FlightPhase.Preflight);
        PlanLoaded("req:2");
        _legEvents.RaiseLegBegan("req:2", "req:1");
        await AwaitIntro(feature);

        Assert.NotEqual(first, _roster.Current!.Id);
        Assert.Equal(2, Intros);
        Assert.Equal("sim:2026-11-02", _roster.State.DrawnFor);
    }

    [Fact]
    public async Task RosterOff_NeverDraws_NeverGreets()
    {
        _persona.Roster.Mode = PersonaRosterOptions.ModeOff;
        var feature = Create();

        PlanLoaded("req:1");
        _phases.SetPhase(FlightPhase.Preflight);
        _legEvents.RaiseLegBegan("req:1", null, LegBoundaryState.None);
        await AwaitIntro(feature);

        Assert.Null(_roster.Current);
        Assert.Null(_roster.State.CurrentId);
        Assert.Equal(0, Intros);
        Assert.False(((ProsimCompanion.Speech.Recognition.IVoiceFeature)feature).Enabled);
    }

    /// <summary>ADR-0019 §8 / issue #219: an open duty day past its first leg pins its FO
    /// whatever the calendar does (EHAM 2026-11-01: Hanna became Sam in the turnaround); after
    /// the end of duty a new plan on the same sim date keeps the FO, a new sim date draws.</summary>
    [Fact]
    public async Task OpenDutyDay_PinsTheFoAcrossASimDateRollover_EndOfDutyReleasesPerSimDate()
    {
        var feature = Create();
        OpenDay(legsCompleted: 0); // the day auto-started at the first Preflight, before the plan
        _phases.SetPhase(FlightPhase.Preflight);
        PlanLoaded("req:1");
        _legEvents.RaiseLegBegan("req:1", null, LegBoundaryState.None);
        await AwaitIntro(feature);
        var drawn = _roster.Current!.Id;
        Assert.Equal("sim:2026-11-01", _roster.State.DrawnFor);
        Assert.Equal(1, Intros);

        // Leg 1 flown, the sim date rolls over in the turnaround, the return plan lands: same FO.
        OpenDay(legsCompleted: 1);
        _clock.LocalDate = new DateOnly(2026, 11, 2);
        _phases.SetPhase(FlightPhase.Shutdown);
        Turnaround("req:1");
        _phases.SetPhase(FlightPhase.Preflight);
        PlanLoaded("req:2");
        _legEvents.RaiseLegBegan("req:2", "req:1");
        await AwaitIntro(feature);
        Assert.Equal(drawn, _roster.Current!.Id);
        Assert.Equal(1, Intros);

        // A restart publishing the same open day: same FO, no second intro.
        OpenDay(legsCompleted: 1);
        await AwaitIntro(feature);
        Assert.Equal(drawn, _roster.Current!.Id);
        Assert.Equal(1, Intros);

        // End of duty, then another plan the same sim date (2026-11-01 stamp vs 11-02 now):
        // the stamp differs, so this IS a new sim day for the roster — a draw is correct here.
        // Same-date case first: roll the clock back to the stamped date.
        _clock.LocalDate = new DateOnly(2026, 11, 1);
        NoDay();
        PlanLoaded("req:3");
        _legEvents.RaiseLegBegan("req:3", "req:2", LegBoundaryState.None);
        await AwaitIntro(feature);
        Assert.Equal(drawn, _roster.Current!.Id); // same sim date: same FO

        // New sim date, new plan, no open day: a new FO.
        _clock.LocalDate = new DateOnly(2026, 11, 2);
        PlanLoaded("req:4");
        _legEvents.RaiseLegBegan("req:4", "req:3", LegBoundaryState.None);
        await AwaitIntro(feature);
        Assert.NotEqual(drawn, _roster.Current!.Id);
        Assert.Equal(2, Intros);
    }

    [Fact]
    public async Task NewFirstOfficer_AsksFirst_AndYesRedraws()
    {
        PlanLoaded("req:1");
        var feature = Create();
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        var before = _roster.Current!.Id;

        Assert.True(((ProsimCompanion.Speech.Recognition.IVoiceFeature)feature).TryHandle("new first officer"));
        Assert.True(_change.Asking.QuestionStands);

        _change.Accept("test");
        await AwaitIntro(feature);

        Assert.NotEqual(before, _roster.Current!.Id);
        Assert.Equal(2, Intros);
    }

    [Fact]
    public async Task WhoIsFlying_NamesTheOfficer()
    {
        PlanLoaded("req:1");
        var feature = Create();
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);

        Assert.True(((ProsimCompanion.Speech.Recognition.IVoiceFeature)feature).TryHandle("who's flying with me today"));

        var answer = _arbiter.Requests.Last(r => r.Tag == "persona.roster");
        Assert.StartsWith("Sam, captain", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void IntroText_FollowsTheConfidenceAndTheClock()
    {
        var nervous = new FoRosterEntry("tom", "Tom", "junior", "casual", 2, "nervous", 320, "EDDF", null);
        Assert.Equal("Evening captain, Tom here, I'll be your First Officer today — about 300 hours on the type. Looking forward to it.", FoRosterVoiceFeature.IntroText(nervous, "evening"));

        var fresh = new FoRosterEntry("x", "Ada", "standard", "standard", 1, "assured", 0, null, null);
        Assert.Equal("Captain, Ada here, I'll be your First Officer today.", FoRosterVoiceFeature.IntroText(fresh, null));
    }

    [Fact]
    public void RoleVoice_FirstOfficer_UsesTheRosterVoice_AndNullWithoutOne()
    {
        var voices = new VoicesOptions();
        Assert.Null(RoleVoiceResolver.Resolve(SpeechRole.FirstOfficer, voices, null).VoiceOverride);
        Assert.Null(RoleVoiceResolver.Resolve(SpeechRole.FirstOfficer, voices, "  ").VoiceOverride);
        Assert.Equal("kokoro:am_adam", RoleVoiceResolver.Resolve(SpeechRole.FirstOfficer, voices, "kokoro:am_adam").VoiceOverride);
        // Only the FO role reads it.
        Assert.Equal(voices.Purser, RoleVoiceResolver.Resolve(SpeechRole.Purser, voices, "kokoro:am_adam").VoiceOverride);
    }

    // Issue #194 (EGLL 2026-11-01): the draw stamp follows the ProSim clock the flight times
    // use, shifted to the aircraft's local day; the SimVar date is the fallback.
    [Fact]
    public void SimLocalDate_PrefersTheProsimClock_ShiftedToLocal_ThenTheSimVarDate()
    {
        var clock = new Moq.Mock<ProsimCompanion.Core.Aircraft.ISimClock>();
        var local = new Moq.Mock<ProsimCompanion.Core.Aircraft.ISimLocalTime>();

        // 23:30Z on the 31st at UTC+2 is already the 1st locally.
        clock.Setup(c => c.SimUtcNow).Returns(new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero));
        local.Setup(l => l.LocalTimeOfDay).Returns(TimeSpan.FromHours(1.5));
        local.Setup(l => l.ZuluTimeOfDay).Returns(TimeSpan.FromHours(23.5));
        local.Setup(l => l.LocalDate).Returns(new DateOnly(2026, 10, 30));
        Assert.Equal(new DateOnly(2026, 11, 1), FoRosterVoiceFeature.SimLocalDate(clock.Object, local.Object));

        // 07:00Z at EGLL (UTC+0): the 31st — never the SimVar's stale 30th.
        clock.Setup(c => c.SimUtcNow).Returns(new DateTimeOffset(2026, 10, 31, 7, 0, 0, TimeSpan.Zero));
        local.Setup(l => l.LocalTimeOfDay).Returns(TimeSpan.FromHours(7));
        local.Setup(l => l.ZuluTimeOfDay).Returns(TimeSpan.FromHours(7));
        Assert.Equal(new DateOnly(2026, 10, 31), FoRosterVoiceFeature.SimLocalDate(clock.Object, local.Object));

        // No ProSim clock: the SimVar date; nothing at all: null.
        clock.Setup(c => c.SimUtcNow).Returns((DateTimeOffset?)null);
        Assert.Equal(new DateOnly(2026, 10, 30), FoRosterVoiceFeature.SimLocalDate(clock.Object, local.Object));
        Assert.Null(FoRosterVoiceFeature.SimLocalDate(null, null));
    }
}
