using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.AircraftState;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>Everything the aircraft state check can observe, gathered by the service so the
/// verdict logic stays pure and testable (issue #63, same split as the startup resync).
/// <paramref name="Read"/> returns null for a dataref that has not reported (or is stale) —
/// the assessor skips that item instead of fail-closing it into a false mismatch.</summary>
public sealed record AircraftStateCheckContext(
    bool Enabled,
    bool AnnounceMismatches,
    bool HasBeenAirborne,
    bool TurnaroundDetected,
    FlightPhase Phase,
    AircraftStateDefinition? Definition,
    Func<string, double?> Read);

/// <summary>
/// The pure cold-and-dark verdict (issue #63). Policy: the check only judges a FRESH departure
/// start — a turnaround leg or a mid-flight app restart is legitimately powered and is Skipped,
/// never nagged. Per-item evaluation is fail-open on missing data (an unreported dataref lists
/// the item as unchecked rather than mismatched: ProSim registers subscriptions over several
/// seconds, and a truly unpowered aircraft may serve defaults) but the check as a whole is
/// honest: if NOTHING reported, the verdict is Skipped, not a hollow Pass.
/// </summary>
public static class AircraftStateCheck
{
    /// <summary>How many mismatch phrases the FO advisory reads out before summarizing the
    /// remainder — a full 15-item recital would be noise, not information.</summary>
    public const int SpokenMismatchLimit = 3;

    public static AircraftStateCheckView Assess(AircraftStateCheckContext context, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);

        AircraftStateCheckView Skipped(string reason)
            => new(nowUtc, AircraftStateCheckStatus.Skipped, [], [], reason, Announce: false);

        if (!context.Enabled)
        {
            return Skipped("check disabled");
        }

        if (context.HasBeenAirborne)
        {
            return Skipped("already airborne this session");
        }

        if (context.TurnaroundDetected)
        {
            return Skipped("turnaround leg — aircraft legitimately powered");
        }

        if (context.Definition is null)
        {
            return Skipped("no cold-and-dark definition loaded");
        }

        if (context.Phase is not (FlightPhase.ColdAndDark or FlightPhase.Preflight))
        {
            return Skipped($"phase {context.Phase} is not a pre-departure start");
        }

        var mismatches = new List<AircraftStateMismatchView>();
        var unchecked_ = new List<string>();
        var evaluated = 0;

        foreach (var item in context.Definition.AllItems())
        {
            if (item.Verify is null)
            {
                continue; // display furniture — never a gate
            }

            var refs = item.Verify.ReferencedDatarefs().Distinct(StringComparer.Ordinal).ToList();
            if (refs.Count == 0 || refs.Any(name => context.Read(name) is null))
            {
                unchecked_.Add(item.Label);
                continue;
            }

            evaluated++;
            if (!ConditionEvaluator.Evaluate(item.Verify, name => context.Read(name) ?? 0))
            {
                mismatches.Add(new AircraftStateMismatchView(
                    item.Label,
                    string.IsNullOrWhiteSpace(item.MismatchPhrase)
                        ? $"{item.Label} is not satisfied"
                        : item.MismatchPhrase));
            }
        }

        if (evaluated == 0)
        {
            return Skipped("switch datarefs unavailable — nothing could be checked");
        }

        if (mismatches.Count == 0)
        {
            return new AircraftStateCheckView(
                nowUtc, AircraftStateCheckStatus.Pass, [], unchecked_, null, Announce: false);
        }

        // Every mismatch verdict is a fresh departure by construction (turnaround/airborne
        // already Skipped above), so the announce decision reduces to the option.
        return new AircraftStateCheckView(
            nowUtc, AircraftStateCheckStatus.Mismatch, mismatches, unchecked_,
            null, Announce: context.AnnounceMismatches);
    }
}

/// <summary>
/// Runs the cold-and-dark check once per MSFS session, at session start on a fresh departure
/// (issue #63 — the 2026-08-16 flight booted against an aircraft nothing had validated).
/// Sits beside <see cref="GsxStartupResyncService"/> and deliberately runs AFTER it: the
/// resync's verdict is what distinguishes a fresh departure from a turnaround. Subscribes the
/// definition's datarefs ONCE at construction and reads cached values (never polls). The
/// verdict is published to <see cref="GsxDiagnosticsStore"/> (web Flight Status page + the
/// Speech-side advisory subscriber) and the session event log; the check re-arms when the
/// pilot leaves the flight session. Degrades on every absence: no definition, no ProSim, or
/// no session simply produce a Skipped verdict or a quiet wait — never a fault.
/// </summary>
public sealed class AircraftStateCheckService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long after session entry the check waits for the phase engine and the
    /// switch datarefs to settle before giving up with a Skipped verdict. Mirrors the startup
    /// resync's window — the surfaces consuming the verdict must not wait forever.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);

    private readonly SimSessionStore _simSession;
    private readonly FlightStateEngine _flightState;
    private readonly GsxResyncState _resyncState;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<AircraftStateCheckService> _logger;

    private readonly AircraftStateDefinition? _definition;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private long _settleWindowStartTicks = Environment.TickCount64;
    private bool _assessed;

    public AircraftStateCheckService(
        IProsimDataRefs prosim,
        SimSessionStore simSession,
        FlightStateEngine flightState,
        GsxResyncState resyncState,
        GsxDiagnosticsStore diagnostics,
        ConfigProblemStore configProblems,
        JsonlEventLog eventLog,
        IOptionsMonitor<GsxOptions> options,
        ILogger<AircraftStateCheckService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simSession);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(configProblems);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _simSession = simSession;
        _flightState = flightState;
        _resyncState = resyncState;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;

        // Loaded once at startup: the check runs once per session against a file the user
        // edits between flights, so hot-reload machinery would be complexity without a payoff.
        _definition = AircraftStateDefinitionLoader.TryLoad(
            Path.Combine(UserConfigPaths.AircraftStates, "cold-and-dark.json"), logger, configProblems);

        if (_definition is not null)
        {
            // ProSim read model: register once, read cached — never poll ReadDataRef.
            var names = _definition.AllItems()
                .Where(item => item.Verify is not null)
                .SelectMany(item => item.Verify!.ReferencedDatarefs())
                .Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                _reads[name] = prosim.Subscribe(name, DataRefTier.Infrequent);
            }
        }

        _simSession.PhaseChanged += OnSimSessionPhaseChanged;
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _simSession.PhaseChanged -= OnSimSessionPhaseChanged;
        foreach (var subscription in _reads.Values)
        {
            subscription.Dispose();
        }
    }

    /// <summary>Re-arms the check when the pilot leaves the flight session (menu/world map):
    /// the next session gets a fresh verdict, and the stale one is withdrawn so the status
    /// page never shows last flight's aircraft state against a new spawn.</summary>
    private void OnSimSessionPhaseChanged(SimSessionPhase oldPhase, SimSessionPhase newPhase)
    {
        if (oldPhase is SimSessionPhase.InSession or SimSessionPhase.Walkaround
            && newPhase is SimSessionPhase.NotInSession or SimSessionPhase.Unknown)
        {
            _assessed = false;
            _settleWindowStartTicks = Environment.TickCount64;
            _diagnostics.UpdateAircraftStateCheck(null);
        }
    }

    private void Tick()
    {
        try
        {
            if (_assessed)
            {
                return;
            }

            // The switch positions only mean something inside the flight session; keep the
            // settle window anchored to session entry (same rationale as the startup resync).
            if (!_simSession.Snapshot().InSession)
            {
                _settleWindowStartTicks = Environment.TickCount64;
                return;
            }

            // The resync verdict is what tells a fresh departure from a turnaround; it always
            // resolves (its own 90 s timeout), so waiting on it cannot deadlock the check.
            if (!_resyncState.IsAssessed)
            {
                return;
            }

            var options = _options.CurrentValue;
            var timedOut = TimeSpan.FromMilliseconds(Environment.TickCount64 - _settleWindowStartTicks)
                >= SettleTimeout;
            if (!timedOut && options.AircraftStateCheckEnabled)
            {
                // Give the phase engine and the subscriptions time to settle: classifying
                // gates on session + dataref readiness (issue #59), and a definition dataref
                // that has not pushed yet would only inflate the unchecked list.
                if (_flightState.LastSnapshot?.IsReady != true
                    || _flightState.CurrentPhase == FlightPhase.Unknown
                    || (_reads.Count > 0 && _reads.Values.All(read => read.RawValue is null)))
                {
                    return;
                }
            }

            var context = new AircraftStateCheckContext(
                Enabled: options.AircraftStateCheckEnabled,
                AnnounceMismatches: options.AircraftStateCheckAnnounce,
                HasBeenAirborne: _flightState.HasBeenAirborneThisSession,
                TurnaroundDetected: _resyncState.TurnaroundDetected
                    || _resyncState.LoadsheetPrelimEdition > 0
                    || _resyncState.LoadsheetFinalSent,
                Phase: _flightState.CurrentPhase,
                Definition: _definition,
                Read: ReadCached);

            var verdict = AircraftStateCheck.Assess(context, DateTimeOffset.UtcNow);
            _assessed = true;
            _diagnostics.UpdateAircraftStateCheck(verdict);
            _eventLog.Record("aircraft-state-check", new
            {
                status = verdict.Status.ToString(),
                reason = verdict.Reason,
                mismatches = verdict.Mismatches.Select(mismatch => mismatch.Label).ToArray(),
                uncheckedItems = verdict.UncheckedLabels,
                announce = verdict.Announce,
            });

            switch (verdict.Status)
            {
                case AircraftStateCheckStatus.Pass:
                    _logger.LogInformation(
                        "Aircraft state check: cold and dark confirmed ({Unchecked} item(s) unchecked)",
                        verdict.UncheckedLabels.Count);
                    break;
                case AircraftStateCheckStatus.Mismatch:
                    _logger.LogWarning(
                        "Aircraft state check: {Count} mismatch(es): {Labels}",
                        verdict.Mismatches.Count,
                        string.Join(", ", verdict.Mismatches.Select(mismatch => mismatch.Label)));
                    break;
                default:
                    _logger.LogInformation("Aircraft state check skipped: {Reason}", verdict.Reason);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aircraft state check tick failed");
        }
    }

    /// <summary>Cached value or null when the subscription never reported / went stale —
    /// stale values are last flight's positions, not this spawn's.</summary>
    private double? ReadCached(string name)
        => _reads.TryGetValue(name, out var read) && read.RawValue is not null && !read.IsStale
            ? read.GetValue(0.0)
            : null;
}
