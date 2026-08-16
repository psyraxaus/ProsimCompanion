using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Logbook;
using ProsimCompanion.Speech.Persona;
using ProsimCompanion.Speech.Roles;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The airborne confirm handshake + FO-as-PF verbal replies (Prosim2FO port).</summary>
public sealed class RoleManagerHandshakeTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly RoleManager _roles;

    public RoleManagerHandshakeTests()
        => _roles = new RoleManager(
            _arbiter, _phase, SpeechTestSupport.TempEventLog(), NullLogger<RoleManager>.Instance);

    private IReadOnlyList<string> SpokenTexts => [.. _arbiter.Requests.Select(r => r.Text)];

    [Theory]
    [InlineData("you have control")]
    [InlineData("your aircraft")]
    [InlineData("you have the aircraft")]
    public void GroundHandover_IsInstant(string phrase)
    {
        _phase.SetPhase(FlightPhase.Preflight);

        Assert.True(_roles.TryHandle(phrase));
        Assert.True(_roles.IsFoPilotFlying);
        Assert.Contains("I have control.", SpokenTexts);
    }

    [Fact]
    public void AirborneHandover_WaitsForConfirmation()
    {
        _phase.SetPhase(FlightPhase.Cruise);

        Assert.True(_roles.TryHandle("you have control"));
        Assert.False(_roles.IsFoPilotFlying); // not yet — confirm-gated
        Assert.Contains("Confirm — I have control?", SpokenTexts);

        Assert.True(_roles.TryHandle("confirm control"));
        Assert.True(_roles.IsFoPilotFlying);
        Assert.Contains("I have control.", SpokenTexts);
    }

    [Theory]
    [InlineData("confirm control")]
    [InlineData("control confirmed")]
    [InlineData("confirm i have control")]
    public void Confirmation_WithoutPendingHandover_FallsThrough(string phrase)
    {
        _phase.SetPhase(FlightPhase.Cruise);

        Assert.False(_roles.TryHandle(phrase));
        Assert.False(_roles.IsFoPilotFlying);
    }

    [Fact]
    public void TakingControlBack_IsAlwaysInstant_EvenAirborne()
    {
        _phase.SetPhase(FlightPhase.Preflight);
        _roles.TryHandle("you have control");
        _phase.SetPhase(FlightPhase.Cruise);

        Assert.True(_roles.TryHandle("my aircraft"));
        Assert.False(_roles.IsFoPilotFlying);
        Assert.Contains("You have control.", SpokenTexts);
    }

    [Fact]
    public void TakingControlBack_AbandonsPendingConfirmation()
    {
        _phase.SetPhase(FlightPhase.Cruise);
        _roles.TryHandle("you have control"); // pending confirm
        _roles.TryHandle("my aircraft");      // human reclaims — pending cleared

        Assert.False(_roles.TryHandle("confirm control"));
        Assert.False(_roles.IsFoPilotFlying);
    }

    [Theory]
    [InlineData("one hundred knots", "Checked.")]
    [InlineData("hundred knots", "Checked.")]
    [InlineData("positive climb", "Gear up.")]
    [InlineData("positive rate", "Gear up.")]
    public void PmCalls_AnsweredOnlyWhileFoIsFlying(string call, string reply)
    {
        // User flying: the call is not ours — it falls through.
        Assert.False(_roles.TryHandle(call));

        _phase.SetPhase(FlightPhase.Preflight);
        _roles.TryHandle("you have control");

        Assert.True(_roles.TryHandle(call));
        Assert.Contains(reply, SpokenTexts);
        Assert.Equal(SpeechPriority.High, _arbiter.Requests[^1].Priority);
    }

    [Fact]
    public void Phrases_IncludeHandshakeAndPmCalls()
    {
        var phrases = _roles.Phrases.ToList();
        Assert.Contains("you have the aircraft", phrases);
        Assert.Contains("confirm control", phrases);
        Assert.Contains("control confirmed", phrases);
        Assert.Contains("confirm i have control", phrases);
        Assert.Contains("one hundred knots", phrases);
        Assert.Contains("positive rate", phrases);
    }
}

/// <summary>The minima recall query — answers only from the confirmed store.</summary>
public sealed class MinimaQueryVoiceFeatureTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly ArrivalMinimaStore _store = new();
    private readonly MinimaQueryVoiceFeature _feature;

    public MinimaQueryVoiceFeatureTests() => _feature = new MinimaQueryVoiceFeature(_store, _arbiter);

    [Fact]
    public void ComposeAnswer_Unset_SaysNotBriefed()
        => Assert.Equal("Minimums not briefed.", MinimaQueryVoiceFeature.ComposeAnswer(null));

    [Fact]
    public void ComposeAnswer_DecisionAltitude_SpeaksDigits()
        => Assert.Equal(
            "Minimums, decision altitude two zero zero feet.",
            MinimaQueryVoiceFeature.ComposeAnswer(
                new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 200)));

    [Fact]
    public void ComposeAnswer_MinimumDescentAltitude_SpeaksDigits()
        => Assert.Equal(
            "Minimums, minimum descent altitude five seven zero feet.",
            MinimaQueryVoiceFeature.ComposeAnswer(
                new ArrivalMinima(ArrivalMinimumKind.MinimumDescentAltitude, 570)));

    [Theory]
    [InlineData("what are our minimums")]
    [InlineData("what's our minimums")]
    [InlineData("say minimums")]
    [InlineData("minimums check")]
    public void QueryPhrases_AreHandled(string phrase)
    {
        _store.Set(new ArrivalMinima(ArrivalMinimumKind.DecisionHeight, 100));

        Assert.True(_feature.TryHandle(phrase));
        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("Minimums, decision height one zero zero feet.", request.Text);
        Assert.Equal(SpeechPriority.Normal, request.Priority);
    }

    [Fact]
    public void Unset_SpeaksNotBriefed()
    {
        Assert.True(_feature.TryHandle("say minimums"));
        Assert.Equal("Minimums not briefed.", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void UnrelatedUtterance_FallsThrough()
        => Assert.False(_feature.TryHandle("set minimums two hundred"));
}

/// <summary>Logbook voice queries — deterministic templates + the dynamic airport prefix.</summary>
public sealed class LogbookVoiceServiceTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly Mock<ILogbookService> _logbook = new();
    private readonly LogbookVoiceService _service;

    public LogbookVoiceServiceTests()
    {
        _logbook.SetupGet(l => l.Flights).Returns([]);
        _logbook.SetupGet(l => l.Days).Returns([]);
        _logbook.Setup(l => l.GetAggregates()).Returns(LogbookAggregates.Empty);
        _service = new LogbookVoiceService(
            _logbook.Object, _arbiter, NullLogger<LogbookVoiceService>.Instance);
    }

    private static LogbookAggregates SampleAggregates() => new(
        TotalFlights: 12,
        TotalBlockHours: 30.5,
        TotalFlightHours: 26.4,
        Landings: 10,
        StabilizedApproaches: 8,
        JudgedApproaches: 10,
        Airports: [new AirportStat("YSSY", 4, FastestTouchdownGsKt: 138, SlowestTouchdownGsKt: 121)]);

    [Theory]
    [InlineData("landing stats for yssy", true, "yssy")]
    [InlineData("landing stats for  egll ", true, "egll")]
    [InlineData("landing stats for", false, "")]
    [InlineData("logbook summary", false, "")]
    public void AirportPrefix_Parses(string text, bool expected, string icao)
    {
        Assert.Equal(expected, LogbookVoiceService.TryParseAirportQuery(text, out var parsed));
        if (expected)
        {
            Assert.Equal(icao, parsed);
        }
    }

    [Fact]
    public void EmptyLogbook_Summary()
        => Assert.Equal(
            "Your logbook is empty. No flights recorded yet.",
            LogbookVoiceService.SummaryText(LogbookAggregates.Empty));

    [Fact]
    public void Summary_IncludesStabilizedRate_OnlyWhenJudged()
    {
        Assert.Equal(
            "Logbook. 12 flights, 30.5 block hours, 10 landings, 80 percent stabilized approaches.",
            LogbookVoiceService.SummaryText(SampleAggregates()));

        var unjudged = SampleAggregates() with { StabilizedApproaches = 0, JudgedApproaches = 0 };
        Assert.Equal(
            "Logbook. 12 flights, 30.5 block hours, 10 landings.",
            LogbookVoiceService.SummaryText(unjudged));
    }

    [Fact]
    public void Hours_SpeaksFlightThenBlock()
        => Assert.Equal(
            "26.4 flight hours, and 30.5 block hours logged.",
            LogbookVoiceService.HoursText(SampleAggregates()));

    [Fact]
    public void AirportText_SpeaksRangeSlowToFast()
        => Assert.Equal(
            "4 landings into YSSY, touchdown ground speed from 121 to 138 knots.",
            LogbookVoiceService.AirportText(SampleAggregates(), "yssy"));

    [Fact]
    public void AirportText_UnknownAirport()
        => Assert.Equal(
            "No landings logged into EGLL yet.",
            LogbookVoiceService.AirportText(SampleAggregates(), "egll"));

    [Fact]
    public void AirportText_SpokenNameReplacesIcao_WhenProvided()
    {
        // Issue #70: the resolved name replaces the raw ICAO in both branches.
        Assert.StartsWith("4 landings into Sydney,",
            LogbookVoiceService.AirportText(SampleAggregates(), "yssy", "Sydney"),
            StringComparison.Ordinal);
        Assert.Equal("No landings logged into Heathrow yet.",
            LogbookVoiceService.AirportText(SampleAggregates(), "egll", "Heathrow"));
    }

    [Fact]
    public void DaySummary_NoDayRecorded()
        => Assert.Equal("No duty day recorded yet.", LogbookVoiceService.DaySummaryText(null));

    [Fact]
    public void DaySummary_ComposesClauses()
    {
        var day = new LogbookDay
        {
            DayId = "day-1",
            Legs = 2,
            Route = ["YSSY", "YMML", "YSSY"],
            BlockMinutes = 180,
            DutyMinutes = 360,
            StabilizedApproaches = 2,
            JudgedApproaches = 2,
        };

        Assert.Equal(
            "Last duty day. 2 sectors, YSSY to YMML to YSSY. 3.0 block hours, 6.0 duty hours."
            + " 2 of 2 approaches stabilized.",
            LogbookVoiceService.DaySummaryText(day));
    }

    [Fact]
    public void AirportQuery_SpeaksThroughArbiter()
    {
        _logbook.Setup(l => l.GetAggregates()).Returns(SampleAggregates());

        Assert.True(_service.TryHandle("Landing stats for YSSY"));
        var request = Assert.Single(_arbiter.Requests);
        Assert.Contains("YSSY", request.Text, StringComparison.Ordinal);
        Assert.Equal(SpeechPriority.Normal, request.Priority);
        Assert.Equal("logbook", request.Tag);
    }

    [Fact]
    public void Phrases_IncludePerAirportEntries()
    {
        _logbook.SetupGet(l => l.Flights).Returns(
        [
            new LogbookFlight { SessionId = "a", Landed = true, Destination = "YSSY" },
            new LogbookFlight { SessionId = "b", Landed = true, Destination = "YSSY" },
            new LogbookFlight { SessionId = "c", Landed = false, Destination = "YMML" },
        ]);

        var phrases = _service.Phrases.ToList();
        Assert.Contains("landing stats for yssy", phrases);
        Assert.DoesNotContain("landing stats for ymml", phrases); // never landed there
        Assert.Equal(phrases.Count, phrases.Distinct().Count());  // per-airport entries dedupe
        Assert.Contains("logbook day summary", phrases);
    }

    [Fact]
    public void UnrelatedUtterance_FallsThrough()
        => Assert.False(_service.TryHandle("read the loadsheet"));
}

/// <summary>Quiet-mode state + suppression rule + the "quiet please" command.</summary>
public sealed class SmallTalkQuietModeTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly QuietState _state = new();
    private readonly SmallTalkService _service;

    public SmallTalkQuietModeTests()
        => _service = new SmallTalkService(
            _state, _arbiter, SpeechTestSupport.TempEventLog(), NullLogger<SmallTalkService>.Instance);

    [Theory]
    [InlineData("quiet please")]
    [InlineData("quiet cockpit")]
    [InlineData("pipe down")]
    [InlineData("less chat")]
    [InlineData("keep it quiet")]
    public void QuietPhrase_EngagesAndAcknowledges(string phrase)
    {
        Assert.True(_service.TryHandle(phrase));
        Assert.True(_state.IsQuiet);

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("Righto, I'll keep it quiet.", request.Text);
        Assert.Equal(SpeechPriority.Normal, request.Priority);
    }

    [Fact]
    public void UnrelatedUtterance_FallsThrough()
    {
        Assert.False(_service.TryHandle("quiet"));
        Assert.False(_state.IsQuiet);
    }

    [Fact]
    public void RepeatedRequest_ReAcknowledges()
    {
        _service.TryHandle("quiet please");
        _service.TryHandle("pipe down");
        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public void Rule_SuppressesLowOnlyWhileQuiet()
    {
        var rule = new QuietCockpitRule(_state);
        var low = new SpeechRequest("chatter", SpeechPriority.Low);

        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(low, SpeechContext.Unknown));

        _state.Engage();
        Assert.Equal(SpeechSuppressionVerdict.Suppress, rule.Evaluate(low, SpeechContext.Unknown));
        Assert.Equal(
            SpeechSuppressionVerdict.Allow,
            rule.Evaluate(new SpeechRequest("checklist", SpeechPriority.Normal), SpeechContext.Unknown));
        Assert.Equal(
            SpeechSuppressionVerdict.Allow,
            rule.Evaluate(new SpeechRequest("positive climb", SpeechPriority.High), SpeechContext.Unknown));
        Assert.Equal(
            SpeechSuppressionVerdict.Allow,
            rule.Evaluate(new SpeechRequest("minimums", SpeechPriority.Critical), SpeechContext.Unknown));

        _state.Reset();
        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(low, SpeechContext.Unknown));
    }
}
