using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.Persona;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// The roster's Speech-side shell (issue #194): decides WHEN a First Officer is drawn
/// (<see cref="FoRosterTriggerCore"/>: one FO per sim calendar day, on the leg-began edge or
/// the Preflight with that leg's plan in, never mid-duty-day — ADR-0019 §8 / issue #219),
/// speaks the once-per-draw gate intro in Preflight, stamps <c>persona.foOnDuty</c> on every
/// leg's session, and answers "who's flying with me today" / "new first officer" (the latter
/// behind the <see cref="FoRosterChangeStore"/> captain's question). Read-and-speak only.
/// </summary>
public sealed class FoRosterVoiceFeature : IVoiceFeature, IStartupModule, IDisposable
{
    public const string IntroTag = "persona.gateIntro";
    private static readonly TimeSpan IntroTtl = TimeSpan.FromMinutes(5);

    private static readonly string[] WhoPhrases =
        ["who's flying with me today", "who is flying with me today", "who's my first officer", "who is my first officer"];

    private static readonly string[] NewPhrases = ["new first officer", "change first officer"];

    private static readonly string[] NormalizedWho = [.. WhoPhrases.Select(CommandMatcher.Normalize)];
    private static readonly string[] NormalizedNew = [.. NewPhrases.Select(CommandMatcher.Normalize)];

    private readonly IFoRosterService _roster;
    private readonly FoRosterChangeStore _change;
    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly IFlightPhaseSource _flight;
    private readonly DayStatusStore _dayStore;
    private readonly ILegBoundaryEvents _legEvents;
    private readonly LegBoundaryStore _legStore;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ISpeechArbiter _arbiter;
    private readonly StyledSpeechService _styled;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoRosterVoiceFeature> _logger;
    private readonly ISimLocalTime? _localTime;
    private readonly ISimClock? _clock;
    private readonly object _gate = new();
    private bool _fallbackDateLogged;
    private bool _started;

    public FoRosterVoiceFeature(
        IFoRosterService roster,
        FoRosterChangeStore change,
        IOptionsMonitor<PersonaOptions> options,
        IFlightPhaseSource flight,
        DayStatusStore dayStore,
        ILegBoundaryEvents legEvents,
        LegBoundaryStore legStore,
        ISpeechArbiter arbiter,
        StyledSpeechService styled,
        JsonlEventLog eventLog,
        ILogger<FoRosterVoiceFeature> logger,
        ISimLocalTime? localTime = null,
        ISimClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(dayStore);
        ArgumentNullException.ThrowIfNull(legEvents);
        ArgumentNullException.ThrowIfNull(legStore);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(styled);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _roster = roster;
        _change = change;
        _options = options;
        _flight = flight;
        _dayStore = dayStore;
        _legEvents = legEvents;
        _legStore = legStore;
        _arbiter = arbiter;
        _styled = styled;
        _eventLog = eventLog;
        _logger = logger;
        _clock = clock;
        _localTime = localTime;
    }

    /// <summary>Roster declaration: after small talk (300) — a crew question, never ahead of
    /// an operational phrase.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 305,
        Gate = VoiceFeatureGate.ByOption("persona.roster.mode", VoiceReferenceTextIds.OffSpeechFo),
        Description = new DescribedVoiceReference("roster", "voiceReference.group.roster.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["who's flying with me today", "who's my first officer"], "voiceReference.group.roster.who", "Sam, captain — about 2400 hours on the type."),
            new(["new first officer", "change first officer"], "voiceReference.group.roster.new", "Change First Officer now, captain — are you sure?", [VoiceBadges.Dialogue]),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _roster.IsActive;

    public IEnumerable<string> Phrases => WhoPhrases.Concat(NewPhrases);

    public bool ValueParse => false;

    public bool AcknowledgeOnly => false;

    /// <summary>The clock, replaceable by tests.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The gate intro in progress, for tests to await; null when idle.</summary>
    internal Task? IntroInFlight { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _flight.PhaseChanged += OnPhaseChanged;
        _dayStore.Changed += OnDayChanged;
        _legEvents.LegBegan += OnLegBegan;
        _change.Answered += OnChangeAnswered;
        if (!_roster.IsActive)
        {
            _logger.LogInformation("FO roster off (persona.roster.mode) — the persona settings describe the First Officer");
        }

        // A start mid-day: the plan may already be loaded and the phase already Preflight.
        Evaluate(preflightEdge: false, legBegan: false);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _dayStore.Changed -= OnDayChanged;
        _legEvents.LegBegan -= OnLegBegan;
        _change.Answered -= OnChangeAnswered;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current == FlightPhase.Preflight && e.Previous != FlightPhase.Preflight)
        {
            HandlePreflightEdge();
        }
    }

    private void OnDayChanged(object? sender, EventArgs e)
    {
        BindOpenDay();
        Evaluate(preflightEdge: false, legBegan: false);
    }

    /// <summary>A day that opened after the draw, on the sim date the FO was drawn for, is that
    /// FO's day (ADR-0019 §8: a plan loaded after the end of duty starts a new day with the same
    /// FO) — bind it so the FO stays pinned while the day is open. A day opened on another date
    /// is not bound: the evaluation that follows draws for it.</summary>
    private void BindOpenDay()
    {
        try
        {
            lock (_gate)
            {
                var day = _dayStore.Snapshot();
                if (day is not { View: { Active: true }, DayId: { } dayId } || _roster.Current is null)
                {
                    return;
                }

                var state = _roster.State;
                var today = FoRosterTriggerCore.Stamp(SimLocalDate(_clock, _localTime) ?? DateOnly.FromDateTime(Now().UtcDateTime));
                if (!string.Equals(state.DrawnDayId, dayId, StringComparison.Ordinal) && string.Equals(state.DrawnFor, today, StringComparison.Ordinal))
                {
                    _roster.BindDay(dayId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FO roster: binding the open day failed");
        }
    }

    private void OnLegBegan(object? sender, LegBeganEventArgs e) => HandleLegBegan();

    /// <summary>The committed edge into Preflight — exposed for tests.</summary>
    public void HandlePreflightEdge() => Evaluate(preflightEdge: true, legBegan: false);

    /// <summary>A different OFP landed (ADR-0019 §1): the draw opportunity — exposed for tests.</summary>
    public void HandleLegBegan() => Evaluate(preflightEdge: false, legBegan: true);

    /// <summary>One evaluation of the trigger rule: a new stamp draws (or, pinned, adopts),
    /// the Preflight edge and the leg-began edge stamp the FO on the leg, and the intro speaks
    /// when due.</summary>
    public void Evaluate(bool preflightEdge, bool legBegan)
    {
        try
        {
            lock (_gate)
            {
                var decision = FoRosterTriggerCore.Evaluate(Inputs(legBegan));
                if (decision.NewStamp is { } stamp)
                {
                    var openDayId = _dayStore.Snapshot() is { View: { Active: true }, DayId: { } id } ? id : null;
                    if (_options.CurrentValue.Roster.IsPinned)
                    {
                        _roster.AdoptDay(stamp, openDayId);
                    }
                    else
                    {
                        _roster.Draw(decision.Reason ?? "trigger", Now(), stamp, openDayId);
                    }

                    // Re-read with the draw in place: the intro is due for the new stamp.
                    decision = FoRosterTriggerCore.Evaluate(Inputs(legBegan));
                }

                var current = _roster.Current;
                if ((preflightEdge || legBegan) && current is not null)
                {
                    _eventLog.Record("persona.foOnDuty", new { id = current.Id, name = current.Name, drawnFor = _roster.State.DrawnFor });
                }

                if (decision.Intro && current is not null)
                {
                    SpeakIntro(current, "gate");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO roster evaluation failed");
        }
    }

    /// <summary>The sim's local calendar day for the draw stamp: the ProSim clock's UTC date
    /// shifted by the aircraft's local offset — the same clock the flight times, the logbook
    /// and the duty day use (issue #194, EGLL 2026-11-01: MSFS's LOCAL YEAR / DAY OF YEAR
    /// SimVars said 10-30 while every flight-time stamp said 10-31, so no FO was drawn). The
    /// SimVar date is the fallback when ProSim's clock is not live. Pure, for tests.</summary>
    public static DateOnly? SimLocalDate(ISimClock? clock, ISimLocalTime? localTime)
    {
        if (clock?.SimUtcNow is { } utc)
        {
            var offset = TimeSpan.Zero;
            if (localTime is { LocalTimeOfDay: { } local, ZuluTimeOfDay: { } zulu })
            {
                offset = local - zulu;
                if (offset > TimeSpan.FromHours(12))
                {
                    offset -= TimeSpan.FromHours(24);
                }
                else if (offset < TimeSpan.FromHours(-12))
                {
                    offset += TimeSpan.FromHours(24);
                }
            }

            return DateOnly.FromDateTime((utc + offset).UtcDateTime);
        }

        return localTime?.LocalDate;
    }

    private FoRosterTriggerInputs Inputs(bool legBegan)
    {
        var options = _options.CurrentValue;
        var state = _roster.State;
        var simDate = SimLocalDate(_clock, _localTime);
        var now = Now();
        var leg = _legStore.Snapshot();
        if (simDate is null && legBegan && options.Roster.IsActive(options.Enabled) && !_fallbackDateLogged)
        {
            _fallbackDateLogged = true;
            _logger.LogInformation("FO roster: the sim's calendar day is not available — the UTC wall date stands in for the draw");
        }

        var day = _dayStore.Snapshot();
        var openDay = day is { View: { Active: true } } ? day : null;
        return new FoRosterTriggerInputs(
            RosterActive: options.Roster.IsActive(options.Enabled),
            Pinned: options.Roster.IsPinned,
            OpenDayLegsCompleted: openDay?.View?.LegsCompleted,
            OpenDayId: openDay?.DayId,
            DrawnDayId: state.DrawnDayId,
            LegBegan: legBegan,
            LegPlanned: leg.PlanId is not null && leg.State == LegBoundaryState.None,
            InPreflight: _flight.CurrentPhase == FlightPhase.Preflight,
            SimLocalDate: simDate,
            FallbackDate: DateOnly.FromDateTime(now.UtcDateTime),
            DrawnFor: state.DrawnFor,
            HasCurrent: _roster.Current is not null,
            IntroDone: state.IntroDone,
            IntroEnabled: options.Roster.GateIntro);
    }

    private void SpeakIntro(FoRosterEntry fo, string trigger)
    {
        _roster.MarkIntroSpoken();
        var word = _localTime?.LocalTimeOfDay is { } t ? TimeOfDayGreeting.Word(t) : null;
        var template = IntroText(fo, word);
        _eventLog.Record("persona.gateIntro", new { id = fo.Id, name = fo.Name, text = template, timeOfDay = word, trigger });
        IntroInFlight = Task.Run(async () =>
        {
            try
            {
                // The shutdown token rides along (review 2026-11-05): a styling call in flight
                // at app exit ends with the service instead of outliving it.
                var styled = await _styled.StyleAsync(template, PersonaStyleCategory.Advisory, $"roster-intro:{fo.Id}:{word}", _shutdown.Token).ConfigureAwait(false);
                await _arbiter.EnqueueAsync(new SpeechRequest(styled, SpeechPriority.Low, Ttl: IntroTtl, Tag: IntroTag), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FO gate intro failed to speak");
            }
        }, CancellationToken.None);
    }

    /// <summary>The deterministic gate intro — pure, for tests. The time-of-day word is the
    /// rc.23 sim-clock helper's ("morning"); null = no greeting word. Hours to the nearest
    /// hundred, omitted at 0; nervous adds "Looking forward to it.", assured adds nothing.</summary>
    public static string IntroText(FoRosterEntry fo, string? timeOfDayWord)
    {
        ArgumentNullException.ThrowIfNull(fo);
        var opening = timeOfDayWord is { Length: > 0 } w
            ? char.ToUpperInvariant(w[0]) + w[1..] + " captain"
            : "Captain";
        var hours = fo.SpokenHours;
        var text = hours > 0
            ? $"{opening}, {fo.Name} here, I'll be your First Officer today — about {hours.ToString(CultureInfo.InvariantCulture)} hours on the type."
            : $"{opening}, {fo.Name} here, I'll be your First Officer today.";
        if (string.Equals(fo.Confidence, "nervous", StringComparison.OrdinalIgnoreCase))
        {
            text += " Looking forward to it.";
        }

        return text;
    }

    /// <summary>The answer to "who's flying with me today" — pure, for tests.</summary>
    public static string WhoText(FoRosterEntry? fo)
    {
        if (fo is null)
        {
            return "Nobody on the roster yet, captain — the draw comes when the first plan of the day is loaded.";
        }

        var experience = fo.Experience switch
        {
            "junior" => "fairly new on the type",
            "senior" => "a senior First Officer",
            _ => "your First Officer",
        };
        var hours = fo.SpokenHours;
        var text = hours > 0
            ? $"{fo.Name}, captain — {experience}, about {hours.ToString(CultureInfo.InvariantCulture)} hours on the type."
            : $"{fo.Name}, captain — {experience}.";
        if (!string.IsNullOrWhiteSpace(fo.Base))
        {
            text += $" Based at {fo.Base}.";
        }

        return text;
    }

    public bool TryHandle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (NormalizedWho.Contains(text))
        {
            Speak(WhoText(_roster.Current), SpeechPriority.Normal, "persona.roster");
            return true;
        }

        if (NormalizedNew.Contains(text))
        {
            if (_roster.Current is null)
            {
                // Nobody to swap: draw outright, no question needed.
                var drawn = _roster.Draw("voice", Now(), _roster.State.DrawnFor);
                if (drawn is not null)
                {
                    SpeakIntro(drawn, "voice");
                }
                else
                {
                    Speak("The roster is empty, captain.", SpeechPriority.Normal, "persona.roster");
                }

                return true;
            }

            if (_options.CurrentValue.Roster.IsPinned)
            {
                Speak($"{_roster.Current.Name} is pinned, captain — release the pin on the Roster card first.", SpeechPriority.Normal, "persona.roster");
                return true;
            }

            if (!_change.Ask())
            {
                Speak("Still waiting on your answer, captain.", SpeechPriority.Normal, "persona.roster");
            }

            return true;
        }

        return false;
    }

    private void OnChangeAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        if (!answer.Accepted)
        {
            return;
        }

        try
        {
            FoRosterEntry? drawn;
            lock (_gate)
            {
                drawn = _roster.Draw("voice", Now(), _roster.State.DrawnFor);
                if (drawn is not null)
                {
                    _eventLog.Record("persona.foOnDuty", new { id = drawn.Id, name = drawn.Name, drawnFor = _roster.State.DrawnFor });
                    SpeakIntro(drawn, "change");
                }
            }

            if (drawn is null)
            {
                Speak("The roster is empty, captain.", SpeechPriority.Normal, "persona.roster");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO roster re-draw failed");
        }
    }

    private void Speak(string text, SpeechPriority priority, string tag)
        => _arbiter.EnqueueAsync(new SpeechRequest(text, priority, Tag: tag)).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
}
