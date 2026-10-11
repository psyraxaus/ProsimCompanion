using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The de-icing policy's shell (2026-10-09): gathers the ProSim OAT (one subscription, cached
/// reads), the departure airport's observation from the Flight Status weather cards (the
/// same figures the pilot sees — no second fetch) and the <c>gsx.deice.*</c> options, runs
/// the pure <see cref="DeiceRequestPolicy"/> and publishes the verdict on the
/// <see cref="DeiceRequestStore"/>. Every verdict change is a <c>gsx-decision</c> and a
/// <c>gsx-deice-policy</c> session event. An <c>ask</c> verdict raises the captain's question
/// (the FO voices it, the Status board shows it).
/// <para>Timing (issue #166, EFHK 2026-10-10): the policy no longer runs on the cycle-started
/// edge — it fired one millisecond after "departure sequence started", before ground prep,
/// before the OFP, sixteen minutes before boarding. It is ARMED by the departure sequencer
/// reporting the DeIce step's turn has come (<see cref="DeiceRequestStore.MarkStepNext"/>:
/// every earlier service done, the plan gate passed) or, with
/// <c>gsx.deice.askWhen = afterBoardingComplete</c>, by the boarding-completed edge; until
/// then the verdict stays Pending and the step waits last on the board. Once armed it
/// re-evaluates on every input change until the cycle completes (a question withdrawn when
/// the OAT climbs, raised when it drops).</para>
/// <para>Silence never declines: the FO's listen window leaves the question standing; only
/// the TTL sweep here declines it (source <c>timeout</c>, <c>gsx.deice.questionTimeoutMinutes</c>).
/// A de-ice the crew calls directly (voice, web, GSX menu) while the question stands counts
/// as "yes". Startup module: construction wires the events; the host calls <see cref="Start"/>.</para>
/// </summary>
public sealed class GsxDeiceRequestService : Core.Hosting.IStartupModule, IDisposable
{
    private static readonly TimeSpan TtlSweep = TimeSpan.FromSeconds(15);

    private readonly DeiceRequestStore _store;
    private readonly DepartureCycleState _cycle;
    private readonly OfpStore _ofpStore;
    private readonly HeroWeatherStore _weather;
    private readonly IFlightPhaseSource _flightState;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly IGsxServiceControl _serviceControl;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly GroundOpsSignals _signals;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxDeiceRequestService> _logger;
    private readonly IDataRefSubscription<double> _oat;
    private readonly Timer _ttlTimer;
    private readonly object _gate = new();
    private IDisposable? _weatherObserver;
    private DeicePolicyVerdict _lastLoggedVerdict = DeicePolicyVerdict.Pending;
    private string? _lastLoggedReason;

    public GsxDeiceRequestService(
        DeiceRequestStore store,
        DepartureCycleState cycle,
        OfpStore ofpStore,
        HeroWeatherStore weather,
        IFlightPhaseSource flightState,
        GsxServiceLifecycleTracker lifecycle,
        IGsxServiceControl serviceControl,
        IProsimDataRefs prosim,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        GroundOpsSignals signals,
        JsonlEventLog eventLog,
        ILogger<GsxDeiceRequestService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(serviceControl);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _cycle = cycle;
        _ofpStore = ofpStore;
        _weather = weather;
        _flightState = flightState;
        _lifecycle = lifecycle;
        _serviceControl = serviceControl;
        _options = options;
        _diagnostics = diagnostics;
        _signals = signals;
        _eventLog = eventLog;
        _logger = logger;

        // The same OAT dataref the flight-data source feeds the FO's icing advisory from —
        // registered once here, read from the cache (never polled).
        _oat = prosim.Subscribe(ProsimDataRefNames.TemperatureOat);
        _ttlTimer = new Timer(_ => SweepQuestionTtl(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        _cycle.Changed += Evaluate;
        _ofpStore.Changed += OnOfpChanged;
        _oat.ValueChanged += OnOatChanged;
        _weatherObserver = _weather.Observe(_ => Evaluate());
        _lifecycle.ServiceEvent += OnServiceEvent;
        _store.Answered += OnAnswered;
        _store.StepBecameNext += OnStepBecameNext;
        _signals.FlightCycleReset += OnFlightCycleReset;
        _ttlTimer.Change(TtlSweep, TtlSweep);
        Evaluate();
    }

    public void Dispose()
    {
        _cycle.Changed -= Evaluate;
        _ofpStore.Changed -= OnOfpChanged;
        _oat.ValueChanged -= OnOatChanged;
        _weatherObserver?.Dispose();
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _store.Answered -= OnAnswered;
        _store.StepBecameNext -= OnStepBecameNext;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _ttlTimer.Dispose();
        _oat.Dispose();
    }

    private void OnOfpChanged(object? sender, EventArgs e) => Evaluate();

    private void OnOatChanged(object? sender, EventArgs e) => Evaluate();

    private void OnStepBecameNext(object? sender, EventArgs e) => Evaluate();

    /// <summary>The store is back to Pending for the new cycle — the "Off" verdict (or the
    /// per-cycle log line) must be published again, and the log dedupe starts over.</summary>
    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _lastLoggedVerdict = DeicePolicyVerdict.Pending;
            _lastLoggedReason = null;
        }

        Evaluate();
    }

    /// <summary>A de-ice the crew called themselves while the question stood is the
    /// captain's answer — withdraw the question and let the sequencer treat the step as
    /// running (it is). Boarding completing is the <c>afterBoardingComplete</c> trigger.</summary>
    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (serviceId.Replace("-", "").Contains("deic", StringComparison.OrdinalIgnoreCase))
        {
            if (_store.Snapshot().Question is not null)
            {
                _store.Accept("called");
            }

            return;
        }

        if (serviceId.Equals(GsxServiceIds.Boarding, StringComparison.OrdinalIgnoreCase)
            && lifecycleEvent == GsxServiceLifecycleEvent.Completed)
        {
            Evaluate();
        }
    }

    /// <summary>The captain answered (voice, web, direct call, or the TTL sweep). "Yes"
    /// after the departure sequence already finished cannot ride the sequencer any more —
    /// call de-icing directly through the same single-writer path.</summary>
    private void OnAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        RecordDecision(
            "de-ice question",
            answer.Accepted ? $"answered yes ({answer.Source})" : $"declined ({answer.Source})");
        _eventLog.Record("gsx-deice-policy", new
        {
            verdict = answer.Accepted ? "accepted" : "declined",
            source = answer.Source,
            reason = _store.Snapshot().Declined,
            askWhen = AskWhen(_options.CurrentValue),
        });

        if (answer.Accepted && _cycle.Complete && answer.Source != "called")
        {
            _ = RequestDirectlyAsync(answer.Source);
        }
    }

    private async Task RequestDirectlyAsync(string source)
    {
        try
        {
            var outcome = await _serviceControl.TryCallAsync(GsxServiceAction.RequestDeice).ConfigureAwait(false);
            RecordDecision("de-ice request", $"departure sequence already complete — called directly ({source}): {outcome.Status}: {outcome.Detail}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct de-ice request after the captain's answer failed");
        }
    }

    /// <summary>True once the policy may decide this cycle: the sequencer reported the DeIce
    /// step's turn, or (afterBoardingComplete) boarding is done. Pure, for tests.</summary>
    public static bool Armed(DeiceRequestSnapshot snapshot, string? askWhen, bool boardingCompleted)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.StepNext || (GsxDeiceOptions.AsksAfterBoarding(askWhen) && boardingCompleted);
    }

    /// <summary>One evaluation. Serialized; cheap when nothing changed (the store notifies
    /// only on a real change and the log dedupes on verdict + reason).</summary>
    private void Evaluate()
    {
        try
        {
            lock (_gate)
            {
                var options = _options.CurrentValue;
                var ofp = _ofpStore.Current;
                var origin = string.IsNullOrWhiteSpace(ofp?.OriginIcao) ? null : ofp!.OriginIcao;
                var weather = DepartureWeather(origin);
                var modeOff = !string.Equals(options.Deice.AutoRequest, GsxDeiceOptions.AutoRequestAsk, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(options.Deice.AutoRequest, GsxDeiceOptions.AutoRequestAuto, StringComparison.OrdinalIgnoreCase);

                // Off is published whenever it is the mode (no step on the board, ever). For
                // ask/auto the policy runs for a departure in preparation, and only once armed:
                // the step's turn, or boarding complete — never the cycle-started edge (#166).
                // Once the cycle is complete the decision stands (a late "yes" is handled by
                // OnAnswered); airborne or arriving it is meaningless.
                if (modeOff)
                {
                    // Already said so this cycle: a re-publish would only stamp a new
                    // DecidedAtUtc and wake every observer on each OAT tick.
                    if (_store.Snapshot().Verdict == DeicePolicyVerdict.Off)
                    {
                        return;
                    }
                }
                else
                {
                    if (!_cycle.Started || _cycle.Complete || !_flightState.CurrentPhase.IsAtGate())
                    {
                        return;
                    }

                    if (!Armed(_store.Snapshot(), options.Deice.AskWhen, BoardingCompletedThisCycle()))
                    {
                        return;
                    }
                }

                var result = DeiceRequestPolicy.Evaluate(new DeiceRequestPolicy.Inputs(
                    Mode: options.Deice.AutoRequest,
                    OatThresholdC: options.Deice.OatThresholdC,
                    RequirePrecipitation: options.Deice.RequirePrecipitation,
                    ProsimOatC: _oat.RawValue is null ? null : _oat.Value,
                    Weather: weather,
                    Icao: origin));

                _store.Publish(result.Verdict, result.Reason, result.OatC, result.Precip, origin, DeiceRequestPolicy.Prompt(result), options.Deice.QuestionTtl);

                if (result.Verdict != _lastLoggedVerdict || !string.Equals(result.Reason, _lastLoggedReason, StringComparison.Ordinal))
                {
                    _lastLoggedVerdict = result.Verdict;
                    _lastLoggedReason = result.Reason;
                    RecordDecision("de-ice policy", $"{result.Verdict}: {result.Reason}");
                    _eventLog.Record("gsx-deice-policy", new
                    {
                        verdict = result.Verdict.ToString(),
                        reason = result.Reason,
                        oat = result.OatC,
                        precip = result.Precip,
                        icao = origin,
                        mode = options.Deice.AutoRequest,
                        thresholdC = options.Deice.OatThresholdC,
                        requirePrecipitation = options.Deice.RequirePrecipitation,
                        askWhen = AskWhen(options),
                        questionTimeoutMinutes = (int)options.Deice.QuestionTtl.TotalMinutes,
                        stepNext = _store.Snapshot().StepNext,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "De-ice policy evaluation failed");
        }
    }

    private bool BoardingCompletedThisCycle()
        => _lifecycle.SnapshotCycles().TryGetValue(GsxServiceIds.Boarding, out var boarding) && boarding.Completed;

    private static string AskWhen(GsxOptions options)
        => GsxDeiceOptions.AsksAfterBoarding(options.Deice.AskWhen)
            ? GsxDeiceOptions.AskWhenAfterBoardingComplete
            : GsxDeiceOptions.AskWhenStepIsNext;

    /// <summary>The departure airport's observation as the Flight Status local card holds it
    /// — only while that card IS the departure (it moves to the destination from descent).</summary>
    private WxFacts? DepartureWeather(string? origin)
    {
        if (origin is null)
        {
            return null;
        }

        var card = _weather.Snapshot().Local;
        return card.Role == WeatherCardRole.Local
            && string.Equals(card.Icao, origin, StringComparison.OrdinalIgnoreCase)
            && card.Status == WxProbeStatus.Found
            ? card.Facts
            : null;
    }

    /// <summary>An unanswered question past its TTL is declined here (degrade, not fail):
    /// the step leaves the sequence and the departure can complete; "request de-icing" still
    /// works afterwards. This is the ONLY path that declines on silence (#166).</summary>
    private void SweepQuestionTtl()
    {
        try
        {
            var snapshot = _store.Snapshot();
            if (snapshot.Question is { } question && !snapshot.QuestionOpen(DateTimeOffset.UtcNow))
            {
                _store.Decline("timeout", $"no answer within {question.Ttl.TotalMinutes:0} minutes (gsx.deice.questionTimeoutMinutes)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "De-ice question TTL sweep failed");
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX automation: {Action} — {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
        _eventLog.Record("gsx-decision", new { action, reason });
    }
}
