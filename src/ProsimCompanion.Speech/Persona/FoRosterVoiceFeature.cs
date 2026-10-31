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
/// (<see cref="FoRosterTriggerCore"/> over the duty day or the sim's calendar day), speaks the
/// once-per-day gate intro at the first Preflight, stamps <c>persona.foOnDuty</c> on every
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
    private readonly IOptionsMonitor<DayOptions> _day;
    private readonly IFlightPhaseSource _flight;
    private readonly DayStatusStore _dayStore;
    private readonly ISpeechArbiter _arbiter;
    private readonly StyledSpeechService _styled;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoRosterVoiceFeature> _logger;
    private readonly ISimLocalTime? _localTime;
    private readonly object _gate = new();
    private bool _fallbackDateLogged;
    private bool _started;

    public FoRosterVoiceFeature(
        IFoRosterService roster,
        FoRosterChangeStore change,
        IOptionsMonitor<PersonaOptions> options,
        IOptionsMonitor<DayOptions> day,
        IFlightPhaseSource flight,
        DayStatusStore dayStore,
        ISpeechArbiter arbiter,
        StyledSpeechService styled,
        JsonlEventLog eventLog,
        ILogger<FoRosterVoiceFeature> logger,
        ISimLocalTime? localTime = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(dayStore);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(styled);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _roster = roster;
        _change = change;
        _options = options;
        _day = day;
        _flight = flight;
        _dayStore = dayStore;
        _arbiter = arbiter;
        _styled = styled;
        _eventLog = eventLog;
        _logger = logger;
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
        _change.Answered += OnChangeAnswered;
        if (!_roster.IsActive)
        {
            _logger.LogInformation("FO roster off (persona.roster.mode) — the persona settings describe the First Officer");
        }

        // A start mid-day: the day may already be open and the phase already Preflight.
        Evaluate(preflightEdge: false);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _dayStore.Changed -= OnDayChanged;
        _change.Answered -= OnChangeAnswered;
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current == FlightPhase.Preflight && e.Previous != FlightPhase.Preflight)
        {
            HandlePreflightEdge();
        }
    }

    private void OnDayChanged(object? sender, EventArgs e) => Evaluate(preflightEdge: false);

    /// <summary>The committed edge into Preflight — exposed for tests.</summary>
    public void HandlePreflightEdge() => Evaluate(preflightEdge: true);

    /// <summary>One evaluation of the trigger rule: a new stamp draws (or, pinned, adopts),
    /// the Preflight edge stamps the FO on the leg, and the intro speaks when due.</summary>
    public void Evaluate(bool preflightEdge)
    {
        try
        {
            lock (_gate)
            {
                var decision = FoRosterTriggerCore.Evaluate(Inputs(preflightEdge));
                if (decision.NewStamp is { } stamp)
                {
                    if (_options.CurrentValue.Roster.IsPinned)
                    {
                        _roster.AdoptDay(stamp);
                    }
                    else
                    {
                        _roster.Draw(decision.Reason ?? "trigger", Now(), stamp);
                    }

                    // Re-read with the draw in place: the intro is due for the new stamp.
                    decision = FoRosterTriggerCore.Evaluate(Inputs(preflightEdge));
                }

                var current = _roster.Current;
                if (preflightEdge && current is not null)
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

    private FoRosterTriggerInputs Inputs(bool preflightEdge)
    {
        var options = _options.CurrentValue;
        var state = _roster.State;
        var simDate = _localTime?.LocalDate;
        var now = Now();
        if (simDate is null && preflightEdge && options.Roster.IsActive(options.Enabled) && !_day.CurrentValue.Enabled && !_fallbackDateLogged)
        {
            _fallbackDateLogged = true;
            _logger.LogInformation("FO roster: the sim's calendar day is not available — the UTC wall date stands in for the draw");
        }

        return new FoRosterTriggerInputs(
            RosterActive: options.Roster.IsActive(options.Enabled),
            Pinned: options.Roster.IsPinned,
            DayModeOn: _day.CurrentValue.Enabled,
            OpenDayId: _dayStore.Snapshot() is { View.Active: true } snapshot ? snapshot.DayId : null,
            PreflightEdge: preflightEdge,
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
                var styled = await _styled.StyleAsync(template, PersonaStyleCategory.Advisory, $"roster-intro:{fo.Id}:{word}").ConfigureAwait(false);
                await _arbiter.EnqueueAsync(new SpeechRequest(styled, SpeechPriority.Low, Ttl: IntroTtl, Tag: IntroTag)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FO gate intro failed to speak");
            }
        });
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
            return "Nobody on the roster yet, captain — the draw comes at the first preflight.";
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
