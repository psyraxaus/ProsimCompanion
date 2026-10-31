using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;
using ProsimCompanion.Core.Persona;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Cabin;
using ProsimCompanion.Speech.Immersion;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Questions;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// Cruise small talk (issue #195): the shell around <see cref="SmallTalkCore"/>, the
/// <see cref="RegionFactService"/> shape. A 30-second tick asks the core whether a line is
/// due; when it is, the kind is claimed and delivered from one of two tiers — the model
/// (streamed through <see cref="StreamingNarrator"/> with the persona fragment, every number
/// checked against the fact sheet, the bank line as the takeover floor) or the bank line
/// restyled by the persona pipeline. Every line is Low-priority speech tagged
/// <c>smalltalk.{kind}</c>: the sterile rule drops it below the ceiling (braces; the core's
/// altitude gate is the belt), "quiet please" zeroes the chattiness and with it the count.
/// <para>An Offer is a captain's question (<see cref="SmallTalkOfferStore"/>): the one dialogue
/// voices it and listens; a yes has the purser send something up through the cabin's own call
/// flow (<c>cabin.*</c> tag, so a later sterile phase never blocks it). A Layover line listens
/// once on the borrowed mic; the reply is logged and, with the model on, answered with one
/// sentence — never more. Read-and-speak only.</para>
/// </summary>
public sealed class CruiseSmallTalkService : Core.Hosting.IStartupModule, IDisposable
{
    public const string SpokenEvent = "smalltalk.spoken";
    public const string ReplyEvent = "smalltalk.reply";
    public const string TagPrefix = "smalltalk.";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(10);

    private readonly IOptionsMonitor<SmallTalkOptions> _options;
    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly IFlightPhaseSource _flight;
    private readonly FlightProgressStore _progress;
    private readonly HeroWeatherStore _weather;
    private readonly TurbulenceStore _turbulence;
    private readonly IReadOnlyList<ICaptainsQuestionStore> _questions;
    private readonly SmallTalkOfferStore _offer;
    private readonly PersonaService _persona;
    private readonly FoFactSource _facts;
    private readonly OpenAiChatClient _llm;
    private readonly StreamingNarrator _narrator;
    private readonly LlmHealthStore _llmHealth;
    private readonly ISpeechArbiter _arbiter;
    private readonly StyledSpeechService _styled;
    private readonly IMicOwnership _mic;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CruiseSmallTalkService> _logger;
    private readonly OfpStore? _ofp;
    private readonly ISpokenText? _spokenText;
    private readonly IFoRosterService? _roster;
    private readonly IPlaceLookup? _places;
    private readonly ISimLocalTime? _clock;
    private readonly RegionFactService? _regionFacts;
    private readonly FailureMonitor? _failures;
    private readonly CabinCrewService? _cabin;
    private readonly SmallTalkCore _core = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _timer;
    private int _ticking;
    private Task? _inFlight;
    private DateTimeOffset? _lastSpeechUtc;
    private bool _disabledLogged;
    private string? _lastOfferLine;

    public CruiseSmallTalkService(
        IOptionsMonitor<SmallTalkOptions> options,
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<BriefingOptions> briefing,
        IFlightPhaseSource flight,
        FlightProgressStore progress,
        HeroWeatherStore weather,
        TurbulenceStore turbulence,
        IEnumerable<ICaptainsQuestionStore> questions,
        SmallTalkOfferStore offer,
        PersonaService persona,
        FoFactSource facts,
        OpenAiChatClient llm,
        StreamingNarrator narrator,
        LlmHealthStore llmHealth,
        ISpeechArbiter arbiter,
        StyledSpeechService styled,
        IMicOwnership mic,
        JsonlEventLog eventLog,
        ILogger<CruiseSmallTalkService> logger,
        OfpStore? ofp = null,
        ISpokenText? spokenText = null,
        IFoRosterService? roster = null,
        IPlaceLookup? places = null,
        ISimLocalTime? clock = null,
        RegionFactService? regionFacts = null,
        FailureMonitor? failures = null,
        CabinCrewService? cabin = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(turbulence);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(narrator);
        ArgumentNullException.ThrowIfNull(llmHealth);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(styled);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _speech = speech;
        _briefing = briefing;
        _flight = flight;
        _progress = progress;
        _weather = weather;
        _turbulence = turbulence;
        _questions = [.. questions];
        _offer = offer;
        _persona = persona;
        _facts = facts;
        _llm = llm;
        _narrator = narrator;
        _llmHealth = llmHealth;
        _arbiter = arbiter;
        _styled = styled;
        _mic = mic;
        _eventLog = eventLog;
        _logger = logger;
        _ofp = ofp;
        _spokenText = spokenText;
        _roster = roster;
        _places = places;
        _clock = clock;
        _regionFacts = regionFacts;
        _failures = failures;
        _cabin = cabin;
    }

    /// <summary>The line in progress, for tests to await. Null when idle.</summary>
    internal Task? InFlight => _inFlight;

    /// <summary>The purser's delayed "coffee's on its way", for tests to await.</summary>
    internal Task? CoffeeInFlight { get; private set; }

    /// <summary>The dice, injectable for deterministic tests.</summary>
    internal Func<double> Roll { get; init; } = () => Random.Shared.NextDouble();

    /// <summary>The model budget before the bank line speaks instead.</summary>
    internal TimeSpan ModelBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long the purser takes over the coffee (2–4 minutes by default).</summary>
    internal Func<TimeSpan> CoffeeDelay { get; init; } = () => TimeSpan.FromMinutes(2 + 2 * Random.Shared.NextDouble());

    public void Start()
    {
        _flight.PhaseChanged += OnPhaseChanged;
        _arbiter.Observed += OnSpeechObserved;
        _offer.Answered += OnOfferAnswered;
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Cruise small talk disabled (smallTalk.enabled)");
            _disabledLogged = true;
        }

        _timer = new Timer(_ => Tick(), null, Poll, Poll);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _arbiter.Observed -= OnSpeechObserved;
        _offer.Answered -= OnOfferAnswered;
        _timer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        lock (_gate)
        {
            _core.OnPhaseChanged(e.Previous, e.Current);
        }
    }

    /// <summary>Anything spoken resets the quiet clock — a checklist, a callout, a purser report
    /// — so small talk only ever follows a stretch of real silence.</summary>
    private void OnSpeechObserved(SpeechArbiterEvent e)
    {
        if (e.Kind is SpeechEventKind.Spoken or SpeechEventKind.Segment)
        {
            _lastSpeechUtc = DateTimeOffset.UtcNow;
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Small talk tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation with the clock supplied (tests step it): claims the due kind and
    /// starts its delivery. Returns the kind started, or null — for tests.</summary>
    public SmallTalkKind? ProcessTick(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            if (!_disabledLogged)
            {
                _logger.LogInformation("Cruise small talk disabled (smallTalk.enabled)");
                _disabledLogged = true;
            }

            return null;
        }

        _disabledLogged = false;
        var tick = BuildTick(nowUtc);
        var enabled = SmallTalkCore.EnabledKinds(options, _roster?.Current is not null);
        SmallTalkKind kind;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }
                || _core.Due(tick, options, _speech.CurrentValue.SterileCockpitCeilingFt, enabled, Roll) is not { } due)
            {
                return null;
            }

            kind = _core.Claim(options, nowUtc) ?? due;
            _inFlight = Task.Run(() => DeliverAsync(kind, nowUtc), CancellationToken.None);
        }

        return kind;
    }

    /// <summary>The tick as the core sees it — exposed for the tests' gate tables.</summary>
    public SmallTalkTick BuildTick(DateTimeOffset nowUtc)
    {
        var view = _flight.Snapshot();
        var data = view.Data;
        var context = new SpeechContext(view.Phase, data?.AltitudeFt ?? 0, data?.IsValid ?? false);
        var questionOpen = false;
        foreach (var store in _questions)
        {
            try
            {
                if (store.Asking.QuestionStands)
                {
                    questionOpen = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Captain's question store read failed");
            }
        }

        var abnormalOpen = false;
        try
        {
            abnormalOpen = _failures?.ActiveAbnormalTitles.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failure monitor read failed");
        }

        return new SmallTalkTick(
            view.Phase, _flight.IsLive, nowUtc,
            data?.AltitudeFt ?? 0, data?.IsValid ?? false,
            abnormalOpen, questionOpen,
            _turbulence.Snapshot().Level,
            _lastSpeechUtc,
            _regionFacts?.NextDueUtc,
            _persona.Chattiness,
            Sterile: SterileCockpitRule.IsSterile(_speech.CurrentValue, context));
    }

    private async Task DeliverAsync(SmallTalkKind kind, DateTimeOffset nowUtc)
    {
        try
        {
            var context = BuildContext();
            var bankLine = SmallTalkBank.Line(kind, context, Roll);
            if (string.IsNullOrWhiteSpace(bankLine))
            {
                Give(kind, nowUtc, "no-line");
                return;
            }

            var sheet = _facts.Build(nowUtc);
            var allowed = sheet.AllowedNumbers.Concat(SmallTalkBank.Numbers(kind, context)).Distinct().ToList();
            var llmUp = _llm.IsConfigured && _briefing.CurrentValue.LlmEnabled && !_llmHealth.Snapshot().IsUnhealthy;

            string? spoken;
            string source;
            if (kind == SmallTalkKind.Offer)
            {
                (spoken, source) = await ComposeOfferAsync(context, bankLine, sheet.Text, allowed, llmUp).ConfigureAwait(false);
                if (spoken is null || !_offer.Ask(spoken))
                {
                    Give(kind, nowUtc, spoken is null ? "no-line" : "question-busy");
                    return;
                }

                _lastOfferLine = spoken;
                Record(kind, source, spoken, context);
                return; // the dialogue voices it; the answer comes back through the store
            }

            (spoken, source) = await SpeakLineAsync(kind, context, bankLine, sheet.Text, allowed, llmUp).ConfigureAwait(false);
            if (spoken is null)
            {
                Give(kind, nowUtc, "not-spoken");
                return;
            }

            Record(kind, source, spoken, context);
            if (kind == SmallTalkKind.Layover)
            {
                await ListenOnceAsync(kind, spoken, allowed, llmUp).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Small talk ({Kind}) failed", kind);
            Give(kind, nowUtc, "error");
        }
    }

    /// <summary>The model tier (streamed, numbers verified, the bank line as the takeover
    /// floor) or, with the model down, the bank line restyled by the persona pipeline. The
    /// text spoken and its source come back for the event.</summary>
    private async Task<(string? Spoken, string Source)> SpeakLineAsync(
        SmallTalkKind kind, SmallTalkContext context, string bankLine, string facts, IReadOnlyList<double> allowed, bool llmUp)
    {
        var tag = TagPrefix + kind.ToString().ToLowerInvariant();
        if (llmUp)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            budget.CancelAfter(ModelBudget);
            var plan = new NarrationPlan(
                tag,
                SmallTalkBank.SystemPrompt(kind, _persona.SystemPromptFragment(PersonaStyleCategory.Advisory)),
                SmallTalkBank.UserPrompt(kind, context, bankLine, facts),
                allowed,
                [new NarrationSection("line", bankLine) { Numbers = SmallTalkBank.Numbers(kind, context) }],
                Header(tag),
                [])
            {
                Guard = FoQuestionCore.ChatSentenceAllowed,
                OnFirstSpeech = () =>
                {
                    try
                    {
                        budget.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
            };

            try
            {
                var result = await _narrator.RunAsync(plan, budget.Token).ConfigureAwait(false);
                if (result.Outcome == SpeechOutcome.Spoken)
                {
                    return (result.Text.Trim(), result.LlmSentences > 0 ? "llm" : "bank");
                }

                if (result.Preempted || result.Outcome != SpeechOutcome.Suppressed)
                {
                    return (null, "llm");
                }

                return (null, "llm"); // suppressed (sterile slipped in): nothing was heard
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                _logger.LogInformation("Small talk: the model gave nothing within {Budget} s — bank line", (int)ModelBudget.TotalSeconds);
            }
        }

        var styled = await _styled.StyleAsync(bankLine, PersonaStyleCategory.Advisory, "smalltalk:" + kind + ":" + bankLine.GetHashCode(StringComparison.Ordinal), _shutdown.Token)
            .ConfigureAwait(false);
        var text = string.IsNullOrWhiteSpace(styled) ? bankLine : styled;
        var outcome = await _arbiter.EnqueueAsync(Header(tag) with { Text = text }, _shutdown.Token).ConfigureAwait(false);
        return outcome == SpeechOutcome.Spoken ? (text, "bank") : (null, "bank");
    }

    /// <summary>The offer line is composed, never streamed: the captain's-question dialogue
    /// speaks it, so the text must exist before the store is raised. One model turn, every
    /// number checked; the bank line otherwise.</summary>
    private async Task<(string? Line, string Source)> ComposeOfferAsync(
        SmallTalkContext context, string bankLine, string facts, IReadOnlyList<double> allowed, bool llmUp)
    {
        if (!llmUp)
        {
            var styled = await _styled.StyleAsync(bankLine, PersonaStyleCategory.Advisory, "smalltalk:offer:" + bankLine.GetHashCode(StringComparison.Ordinal), _shutdown.Token)
                .ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(styled) ? bankLine : styled, "bank");
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            budget.CancelAfter(ModelBudget);
            var reply = await _llm.CompleteAsync(
                SmallTalkBank.SystemPrompt(SmallTalkKind.Offer, _persona.SystemPromptFragment(PersonaStyleCategory.Advisory)),
                SmallTalkBank.UserPrompt(SmallTalkKind.Offer, context, bankLine, facts),
                budget.Token).ConfigureAwait(false);
            var line = FirstSentence(reply);
            if (!string.IsNullOrWhiteSpace(line) && FoQuestionCore.ChatSentenceAllowed(line)
                && NumberVerifier.Check(SpokenNumberText.ToDigits(line), allowed).Ok)
            {
                return (line, "llm");
            }
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            _logger.LogInformation("Small talk offer: the model gave nothing in time — bank line");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk offer: model turn failed — bank line");
        }

        return (bankLine, "bank");
    }

    /// <summary>After a layover line: one listen on the borrowed mic, the reply logged, one
    /// model sentence back when the model is on (verified, the floor otherwise), never a second.</summary>
    private async Task ListenOnceAsync(SmallTalkKind kind, string line, IReadOnlyList<double> allowed, bool llmUp)
    {
        IDisposable borrow;
        try
        {
            borrow = _mic.Borrow("small-talk");
        }
        catch (InvalidOperationException)
        {
            _logger.LogInformation("Small talk: the microphone is borrowed — not listening for a reply");
            return;
        }

        string? heard;
        using (borrow)
        {
            heard = await _mic.ListenAsync([], ReplyWindow, _shutdown.Token).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(heard))
        {
            _eventLog.Record(ReplyEvent, new { kind = kind.ToString().ToLowerInvariant(), heard = (string?)null, followUp = (string?)null });
            return; // silence is fine and ends the exchange
        }

        bool allowedFollowUp;
        lock (_gate)
        {
            allowedFollowUp = _core.TryBeginFollowUp();
        }

        string? followUp = null;
        string? source = null;
        if (allowedFollowUp && llmUp)
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                budget.CancelAfter(ModelBudget);
                var reply = await _llm.CompleteAsync(
                    SmallTalkBank.FollowUpSystemPrompt(_persona.SystemPromptFragment(PersonaStyleCategory.Advisory)),
                    SmallTalkBank.FollowUpUserPrompt(line, heard), budget.Token).ConfigureAwait(false);
                var sentence = FirstSentence(reply);
                if (!string.IsNullOrWhiteSpace(sentence) && FoQuestionCore.ChatSentenceAllowed(sentence)
                    && NumberVerifier.Check(SpokenNumberText.ToDigits(sentence), allowed).Ok)
                {
                    followUp = sentence;
                    source = "llm";
                }
                else
                {
                    followUp = SmallTalkBank.FollowUpFloor;
                    source = "bank";
                }
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                followUp = SmallTalkBank.FollowUpFloor;
                source = "bank";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Small talk follow-up failed — floor line");
                followUp = SmallTalkBank.FollowUpFloor;
                source = "bank";
            }
        }

        _eventLog.Record(ReplyEvent, new { kind = kind.ToString().ToLowerInvariant(), heard, followUp, source });
        _logger.LogInformation("Small talk reply ({Kind}): \"{Heard}\" -> {FollowUp}", kind, heard, followUp ?? "(no follow-up)");
        if (followUp is not null)
        {
            await _arbiter.EnqueueAsync(Header(TagPrefix + kind.ToString().ToLowerInvariant() + ".reply") with { Text = followUp }, _shutdown.Token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The captain's answer to the offer: yes → the purser sends something up after a
    /// realistic wait, through the cabin's own call flow (cabin.* tag, never sterile-blocked).</summary>
    private void OnOfferAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        _eventLog.Record(ReplyEvent, new { kind = "offer", heard = answer.Accepted ? "yes" : "no", source = answer.Source, line = _lastOfferLine });
        if (!answer.Accepted || _cabin is null)
        {
            return;
        }

        var delay = CoffeeDelay();
        CoffeeInFlight = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
                var sent = await _cabin.AnnounceAsync("cabin.smalltalk.coffee", SmallTalkBank.PurserCoffeeLine, foAckText: "Lovely, thank you.").ConfigureAwait(false);
                if (!sent)
                {
                    _logger.LogInformation("Small talk: the cabin was busy — the coffee goes unannounced");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Small talk: the purser's coffee line failed");
            }
        });
    }

    private SmallTalkContext BuildContext()
    {
        var fo = _roster?.Current;
        var effective = _persona.Effective;
        string? destination = null;
        try
        {
            var ofp = _ofp?.Current;
            destination = !string.IsNullOrWhiteSpace(ofp?.DestinationName) ? ofp!.DestinationName
                : ofp?.DestinationIcao is { Length: 4 } icao ? _spokenText?.Airport(icao) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk: destination lookup failed");
        }

        string? town = null, townSide = null, sunSide = null;
        double? sunElevation = null;
        var morning = false;
        try
        {
            var progress = _progress.Snapshot();
            if (progress.Position is { } position)
            {
                var fix = _places?.Locate(position, progress.TrackTrueDeg);
                var near = fix?.Reference ?? fix?.Nearest;
                town = near?.Town.Name;
                townSide = near?.Side;
                if (_clock is { LocalDate: { } date, ZuluTimeOfDay: { } zulu })
                {
                    var sun = SolarPosition.At(position.LatitudeDeg, position.LongitudeDeg, date, zulu);
                    sunElevation = sun.ElevationDeg;
                    sunSide = progress.TrackTrueDeg is { } track ? SolarPosition.SideOf(sun.AzimuthDeg, track) : null;
                }
            }

            morning = _clock?.LocalTimeOfDay is { } local && local.TotalHours < 12;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk: place / sun lookup failed");
        }

        var sky = SkyCondition.Unknown;
        try
        {
            sky = _weather.Snapshot().Local.Sky;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Small talk: weather read failed");
        }

        return new SmallTalkContext(
            FoName: fo?.Name ?? (string.IsNullOrWhiteSpace(effective.Name) ? null : effective.Name),
            FoBase: fo?.Base is { Length: 4 } b ? (_spokenText?.Airport(b) ?? b) : null,
            FoHours: fo?.SpokenHours ?? 0,
            Confidence: effective.Confidence,
            DestinationCity: destination,
            TownName: town,
            TownSide: townSide,
            SunSide: sunSide,
            SunElevationDeg: sunElevation,
            Morning: morning,
            Sky: sky);
    }

    /// <summary>Low priority, tagged <c>smalltalk.{kind}</c>: the quiet rule may drop it, any
    /// callout pre-empts it, the sterile rule suppresses it below the ceiling; valid only while
    /// still in the cruise.</summary>
    private SpeechRequest Header(string tag) => new(
        "Small talk", SpeechPriority.Low, SpokenTtl,
        IsStillValid: () => _flight.CurrentPhase == FlightPhase.Cruise,
        Tag: tag);

    private void Give(SmallTalkKind kind, DateTimeOffset nowUtc, string reason)
    {
        lock (_gate)
        {
            _core.Release(kind, nowUtc);
        }

        _logger.LogDebug("Small talk ({Kind}) given back: {Reason}", kind, reason);
    }

    private void Record(SmallTalkKind kind, string source, string text, SmallTalkContext context)
    {
        var view = _flight.Snapshot();
        int count;
        lock (_gate)
        {
            count = _core.CountThisFlight;
        }

        _logger.LogInformation("Small talk ({Kind}, {Source}): \"{Text}\"", kind, source, text);
        _eventLog.Record(SpokenEvent, new
        {
            kind = kind.ToString().ToLowerInvariant(),
            source,
            foId = _roster?.Current?.Id,
            text,
            phase = view.Phase.ToString(),
            altitudeFt = view.Data is { IsValid: true } d ? Math.Round(d.AltitudeFt) : (double?)null,
            chattiness = _persona.Chattiness,
            countThisFlight = count,
            town = context.TownName,
            destination = context.DestinationCity,
        });
    }

    /// <summary>The first sentence of a model reply, quotes and whitespace trimmed.</summary>
    internal static string FirstSentence(string? reply)
    {
        var text = (reply ?? "").Trim().Trim('"', '\'', '`').Trim();
        if (text.Length == 0)
        {
            return "";
        }

        var end = text.IndexOfAny(['.', '!', '?']);
        return end > 0 && end < text.Length - 1 ? text[..(end + 1)].Trim() : text;
    }
}
