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
    private readonly DayOptions _day = new() { Enabled = false };
    private readonly FakePhaseSource _phases = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly DayStatusStore _dayStore = new();
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

    private FoRosterVoiceFeature Create()
    {
        var persona = new Mock<IOptionsMonitor<PersonaOptions>>();
        persona.SetupGet(m => m.CurrentValue).Returns(() => _persona);
        var day = new Mock<IOptionsMonitor<DayOptions>>();
        day.SetupGet(m => m.CurrentValue).Returns(() => _day);
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
            _roster, _change, persona.Object, day.Object, _phases, _dayStore, _arbiter, styled, _eventLog,
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
    public async Task FirstPreflight_DrawsAndSpeaksTheIntroOnce_ThenNotAgainTheSameDay()
    {
        var feature = Create();

        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);

        Assert.NotNull(_roster.Current);
        Assert.Equal(1, Intros);
        var intro = _arbiter.Requests.Single(r => r.Tag == FoRosterVoiceFeature.IntroTag);
        Assert.Equal(SpeechPriority.Low, intro.Priority);
        Assert.StartsWith("Morning captain, Sam here", intro.Text, StringComparison.Ordinal);
        Assert.Contains("about 2400 hours", intro.Text, StringComparison.Ordinal);

        // A second leg the same sim day: no new draw, no second intro.
        _phases.SetPhase(FlightPhase.Shutdown);
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        Assert.Equal(1, Intros);
        Assert.Equal("sim:2026-11-01", _roster.State.DrawnFor);
    }

    [Fact]
    public async Task NewSimDay_DrawsAgain_AndGreetsAgain()
    {
        var feature = Create();
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        var first = _roster.Current!.Id;

        _clock.LocalDate = new DateOnly(2026, 11, 2);
        _phases.SetPhase(FlightPhase.Shutdown);
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);

        Assert.NotEqual(first, _roster.Current!.Id);
        Assert.Equal(2, Intros);
    }

    [Fact]
    public async Task RosterOff_NeverDraws_NeverGreets()
    {
        _persona.Roster.Mode = PersonaRosterOptions.ModeOff;
        var feature = Create();

        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);

        Assert.Null(_roster.Current);
        Assert.Null(_roster.State.CurrentId);
        Assert.Equal(0, Intros);
        Assert.False(((ProsimCompanion.Speech.Recognition.IVoiceFeature)feature).Enabled);
    }

    [Fact]
    public async Task DayMode_DrawsWhenTheDayStarts_AndTheRestartedDayKeepsTheFo()
    {
        _day.Enabled = true;
        var feature = Create();
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        Assert.Null(_roster.Current); // day mode on, no day open: nothing drawn

        _dayStore.Update(new DaySnapshot(new DayView(true, "OnLeg", 1, 1, 0, null, null, 0, 0, null), [], null, null, "day-1"));
        await AwaitIntro(feature);
        var drawn = _roster.Current!.Id;
        Assert.Equal("day:day-1", _roster.State.DrawnFor);
        Assert.Equal(1, Intros);

        // The same day published again (a restart resuming it): same FO, no second intro.
        _dayStore.Update(new DaySnapshot(new DayView(true, "Turnaround", 1, 1, 1, null, null, 60, 90, null), [], null, null, "day-1"));
        _phases.SetPhase(FlightPhase.Shutdown);
        _phases.SetPhase(FlightPhase.Preflight);
        await AwaitIntro(feature);
        Assert.Equal(drawn, _roster.Current!.Id);
        Assert.Equal(1, Intros);
    }

    [Fact]
    public async Task NewFirstOfficer_AsksFirst_AndYesRedraws()
    {
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
}
