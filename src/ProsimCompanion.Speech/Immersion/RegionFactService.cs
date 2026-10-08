using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Persona;
using ProsimCompanion.Speech.Questions;

namespace ProsimCompanion.Speech.Immersion;

/// <summary>
/// Ambient region facts in the cruise (issue #122): the shell around <see cref="RegionFactCore"/>.
/// A 30-second tick asks the core whether a fact is due; when it is, the aircraft position is
/// resolved against the offline atlas (<see cref="IPlaceLookup"/>, the same lookup as "what
/// are we flying over?", #153), the region is claimed once per flight, and the fact comes
/// from one of two tiers:
/// <list type="bullet">
/// <item>the model, when <see cref="RegionFactsOptions.LlmFacts"/> is on and the LLM is
/// configured, enabled and healthy — streamed through <see cref="StreamingNarrator"/> under
/// the place-facts guard (a sentence about this flight with a figure in it is refused), the
/// number verifier off as for every trivia path (#152/#153);</item>
/// <item>else the curated <c>region-facts.json</c> (user-editable, ADR-0007), one random
/// entry for the region, restyled by the persona pipeline with every number locked.</item>
/// </list>
/// The fact is Low-priority speech tagged <c>fo.region-fact</c> — "quiet please" drops it, a
/// callout pre-empts it — valid only while still in the cruise. Every attempt leaves one
/// <c>fo.region-fact</c> session event; disabled, the service logs one line at startup and
/// then does nothing at all.
/// </summary>
public sealed class RegionFactService : Core.Hosting.IStartupModule, IDisposable
{
    public const string EventType = "fo.region-fact";
    public const string Tag = "fo.region-fact";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromSeconds(90);

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly IFlightPhaseSource _flight;
    private readonly FlightProgressStore _progress;
    private readonly RegionFactBank _bank;
    private readonly OpenAiChatClient _llm;
    private readonly StreamingNarrator _narrator;
    private readonly LlmHealthStore _llmHealth;
    private readonly ISpeechArbiter _arbiter;
    private readonly StyledSpeechService _styled;
    private readonly SpeechStatusStore _status;
    private readonly QuietState _quiet;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<RegionFactService> _logger;
    private readonly IPlaceLookup? _places;
    private readonly PersonaService? _persona;
    private readonly RegionFactCore _core = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _timer;
    private int _ticking;
    private Task? _inFlight;
    private bool _disabledLogged;

    public RegionFactService(
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<BriefingOptions> briefing,
        IFlightPhaseSource flight,
        FlightProgressStore progress,
        RegionFactBank bank,
        OpenAiChatClient llm,
        StreamingNarrator narrator,
        LlmHealthStore llmHealth,
        ISpeechArbiter arbiter,
        StyledSpeechService styled,
        SpeechStatusStore status,
        QuietState quiet,
        JsonlEventLog eventLog,
        ILogger<RegionFactService> logger,
        IPlaceLookup? places = null,
        PersonaService? persona = null)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(narrator);
        ArgumentNullException.ThrowIfNull(llmHealth);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(styled);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _speech = speech;
        _briefing = briefing;
        _flight = flight;
        _progress = progress;
        _bank = bank;
        _llm = llm;
        _narrator = narrator;
        _llmHealth = llmHealth;
        _arbiter = arbiter;
        _styled = styled;
        _status = status;
        _quiet = quiet;
        _eventLog = eventLog;
        _logger = logger;
        _places = places;
        _persona = persona;
    }

    private RegionFactsOptions Options => _speech.CurrentValue.RegionFacts;

    /// <summary>The fact in progress, for tests to await. Null when idle.</summary>
    internal Task? InFlight => _inFlight;

    /// <summary>Seconds from the model request to the first spoken word before the curated
    /// tier takes over; internal so tests can shorten it.</summary>
    internal TimeSpan ModelBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The dice, injectable for deterministic tests.</summary>
    internal Func<double> Roll { get; init; } = () => Random.Shared.NextDouble();

    public void Start()
    {
        _flight.PhaseChanged += OnPhaseChanged;
        if (!Options.Enabled)
        {
            _logger.LogInformation("Region facts disabled (speech.regionFacts.enabled)");
            _disabledLogged = true;
        }

        // The timer runs regardless: the switch is hot-reloadable and the tick is a few
        // comparisons while off.
        _timer = new Timer(_ => Tick(), null, Poll, Poll);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
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
            _logger.LogError(ex, "Region fact tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>The scheduling rule with its clock supplied (tests step it): resolves the
    /// region when the core says a fact is due and starts the speech. Returns the region key
    /// it started on, or null — for tests.</summary>
    public string? ProcessTick(DateTimeOffset nowUtc)
    {
        var options = Options;
        if (!options.Enabled)
        {
            if (!_disabledLogged)
            {
                _logger.LogInformation("Region facts disabled (speech.regionFacts.enabled)");
                _disabledLogged = true;
            }

            return null;
        }

        _disabledLogged = false;
        var view = _flight.Snapshot();
        var context = new SpeechContext(view.Phase, view.Data?.AltitudeFt ?? 0, view.Data?.IsValid ?? false);
        var tick = new RegionFactTick(
            view.Phase, _flight.IsLive, nowUtc,
            VoicePaused: _status.Snapshot().ListeningPaused,
            Sterile: SterileCockpitRule.IsSterile(_speech.CurrentValue, context),
            Quiet: _quiet.IsQuiet);

        lock (_gate)
        {
            if (!_core.IsDue(tick, options, Roll) || (_inFlight is { IsCompleted: false }))
            {
                return null;
            }

            var progress = _progress.Snapshot();
            if (progress.Position is not { } position || _places is null)
            {
                _core.Defer(nowUtc);
                RecordSkip(RegionFactSkip.NoPosition, null, null, null);
                return null;
            }

            var fix = _places.Locate(position, progress.TrackTrueDeg);
            var region = RegionFactCore.RegionOf(fix);
            if (region is not { } resolved)
            {
                _core.Defer(nowUtc);
                RecordSkip(RegionFactSkip.Unresolved, null, null, position);
                return null;
            }

            if (!_core.TryClaim(resolved.Key, options, nowUtc, Roll))
            {
                RecordSkip(RegionFactSkip.Repeated, resolved.Key, resolved.Name, position);
                return null;
            }

            _inFlight = Task.Run(() => OfferAsync(resolved.Key, resolved.Name, fix, position), CancellationToken.None);
            return resolved.Key;
        }
    }

    private async Task OfferAsync(string key, string name, PlaceFix fix, GeoPoint position)
    {
        try
        {
            var options = Options;
            var llmUp = options.LlmFacts && _llm.IsConfigured && _briefing.CurrentValue.LlmEnabled && !_llmHealth.Snapshot().IsUnhealthy;
            string? spoken = null;
            var source = "model";
            var outcome = "spoken";

            if (llmUp)
            {
                var result = await ModelFactAsync(name, fix).ConfigureAwait(false);
                if (result.Preempted)
                {
                    Record(key, name, position, "model", "preempted", result.Text);
                    return;
                }

                spoken = result.Text;
            }

            if (string.IsNullOrWhiteSpace(spoken))
            {
                source = "curated";
                spoken = await CuratedFactAsync(key).ConfigureAwait(false);
                if (spoken is null)
                {
                    outcome = llmUp ? "no-fact" : "no-curated-fact";
                    source = llmUp ? "model" : "curated";
                }
            }

            if (spoken is null)
            {
                lock (_gate)
                {
                    _core.Release(key);
                }

                Record(key, name, position, source, outcome, null);
                return;
            }

            lock (_gate)
            {
                _core.Remember(spoken);
            }

            Record(key, name, position, source, outcome, spoken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Region fact for {Region} failed", name);
            lock (_gate)
            {
                _core.Release(key);
            }
        }
    }

    /// <summary>The model tier: streamed, every sentence through the place-facts guard,
    /// nothing said when the budget runs out before the first word (the curated tier then
    /// speaks instead). The text spoken comes back for the event and the no-repeat list.</summary>
    private async Task<(string Text, bool Preempted)> ModelFactAsync(string name, PlaceFix fix)
    {
        var personaFragment = _persona?.SystemPromptFragment(PersonaStyleCategory.Advisory) ?? "";
        IReadOnlyList<string> told;
        lock (_gate)
        {
            told = [.. _core.Told];
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        budget.CancelAfter(ModelBudget);
        var plan = new NarrationPlan(
            "fo.region-fact", RegionFactCore.SystemPrompt(personaFragment),
            RegionFactCore.UserPrompt(name, PlaceFixText.PlaceNames(fix), told), [], [],
            Header(), [])
        {
            VerifyNumbers = false,
            Guard = FoQuestionCore.ChatSentenceAllowed,
            OnFirstSpeech = () =>
            {
                try
                {
                    budget.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Already finished.
                }
            },
        };

        try
        {
            var result = await _narrator.RunAsync(plan, budget.Token).ConfigureAwait(false);
            // Anything released was heard: a guard refusal after the first sentence still
            // leaves a spoken fact, and the curated tier must not add a second one.
            var text = result.LlmSentences > 0 ? result.Text.Trim() : "";
            return (text, result.Preempted);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            _logger.LogInformation("Region fact: the model gave nothing within {Budget} s — curated tier", (int)ModelBudget.TotalSeconds);
            return ("", false);
        }
    }

    /// <summary>The curated tier: a random entry for the region from region-facts.json,
    /// restyled by the persona pipeline (numbers locked, deterministic text as the floor).</summary>
    private async Task<string?> CuratedFactAsync(string key)
    {
        var facts = _bank.FactsFor(key);
        if (facts.Count == 0)
        {
            return null;
        }

        var index = Math.Min(facts.Count - 1, (int)(Math.Clamp(Roll(), 0, 0.999999) * facts.Count));
        var pick = facts[index];
        var styled = await _styled.StyleAsync(pick, PersonaStyleCategory.Advisory, "region:" + key + ":" + index, _shutdown.Token)
            .ConfigureAwait(false);
        var text = string.IsNullOrWhiteSpace(styled) ? pick : styled;
        await _arbiter.EnqueueAsync(Header() with { Text = text }, _shutdown.Token).ConfigureAwait(false);
        return text;
    }

    /// <summary>Low priority: chatter the quiet rule may drop and any callout pre-empts;
    /// valid only while still in the cruise with the pilot's ear on.</summary>
    private SpeechRequest Header() => new(
        "Region fact", SpeechPriority.Low, SpokenTtl,
        IsStillValid: () => _flight.CurrentPhase == FlightPhase.Cruise && !_status.Snapshot().ListeningPaused,
        Tag: Tag);

    private void RecordSkip(RegionFactSkip skip, string? key, string? name, GeoPoint? position)
    {
        var outcome = skip switch
        {
            RegionFactSkip.NoPosition => "no-position",
            RegionFactSkip.Unresolved => "unresolved",
            _ => "repeated",
        };
        _logger.LogDebug("Region fact skipped: {Outcome} ({Region})", outcome, name ?? "-");
        Record(key, name, position, null, outcome, null);
    }

    private void Record(string? key, string? name, GeoPoint? position, string? source, string outcome, string? text)
    {
        if (text is not null)
        {
            _logger.LogInformation("Region fact ({Source}) for {Region}: \"{Text}\"", source, name, text);
        }

        int count;
        DateTimeOffset? next;
        lock (_gate)
        {
            count = _core.CountThisFlight;
            next = _core.NextDueUtc;
        }

        _eventLog.Record(EventType, new
        {
            regionKey = key,
            region = name,
            source,
            outcome,
            text,
            lat = position is { } p ? Math.Round(p.LatitudeDeg, 3) : (double?)null,
            lon = position is { } q ? Math.Round(q.LongitudeDeg, 3) : (double?)null,
            phase = _flight.CurrentPhase.ToString(),
            countThisFlight = count,
            nextDueUtc = next,
        });
    }
}
