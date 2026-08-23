using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>Result of one prep-step attempt.</summary>
public enum GsxPrepStatus
{
    /// <summary>Preconditions not met yet — try again next cycle.</summary>
    Waiting,

    /// <summary>Started and awaiting an observable outcome.</summary>
    Pending,

    /// <summary>Finished (succeeded, skipped by config, or safe-failed and yielded).</summary>
    Done,
}

/// <summary>Read-only view of the ground-preparation progress — a narrow seam so consumers
/// (e.g. the on-demand service control) can test against a mock instead of constructing the
/// full coordinator.</summary>
public interface IGsxGroundPrepStatus
{
    /// <summary>True once the whole preparation chain has run for this gate session.</summary>
    bool PrepComplete { get; }
}

/// <summary>
/// Enforces the ground-preparation order (owner-specified): <b>reposition → settle → GPU +
/// chocks → jetway/stairs → departure services</b>. The individual modules keep their own
/// guards and safe-fail behaviour; this coordinator only decides <i>when</i> each may run, and
/// gates the departure sequencer until preparation is complete. The whole chain holds until
/// the pilot is actually in the MSFS session (<see cref="SimSessionStore"/>) and the startup
/// resync has assessed. Resets for a new session on gate change, Couatl restart, sim-session
/// end, or returning to preparation after flight.
/// </summary>
public sealed class GsxGroundPrepCoordinator : IDisposable, IGsxGroundPrepStatus
{
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RepositionSettleTime = TimeSpan.FromSeconds(12);

    private readonly IGsxRemoteApi _api;
    private readonly GsxRepositionService _reposition;
    private readonly GsxGateAnchorService _gateAnchor;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly IFlightPhaseSource _flightState;
    private readonly SimSessionStore _simSession;
    private readonly GsxResyncState _resyncState;
    private readonly DepartureCycleState _cycle;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxGroundPrepCoordinator> _logger;
    private readonly Timer _timer;
    private GsxPrepStage _stage = GsxPrepStage.Idle;
    private DateTimeOffset _settleUntil;
    private string? _sessionGateKey;
    private string? _holdReason;
    private bool _conflictPublished;
    private int _running;

    public GsxGroundPrepCoordinator(
        IGsxRemoteApi api,
        GsxRepositionService reposition,
        GsxGateAnchorService gateAnchor,
        GsxGroundEquipmentService groundEquipment,
        GsxJetwayStairsService jetwayStairs,
        IFlightPhaseSource flightState,
        SimSessionStore simSession,
        GsxResyncState resyncState,
        DepartureCycleState cycle,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxGroundPrepCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(reposition);
        ArgumentNullException.ThrowIfNull(gateAnchor);
        ArgumentNullException.ThrowIfNull(groundEquipment);
        ArgumentNullException.ThrowIfNull(jetwayStairs);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(simSession);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _reposition = reposition;
        _gateAnchor = gateAnchor;
        _groundEquipment = groundEquipment;
        _jetwayStairs = jetwayStairs;
        _flightState = flightState;
        _simSession = simSession;
        _resyncState = resyncState;
        _cycle = cycle;
        _options = options;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;

        _api.Mirror.SidChanged += OnSidChanged;
        _simSession.SessionEnded += OnSessionEnded;
        _timer = new Timer(_ => _ = CycleAsync(), null, CycleInterval, CycleInterval);
    }

    /// <summary>True once the whole preparation chain has run for this gate session — the
    /// departure service sequencer waits for this.</summary>
    public bool PrepComplete => _stage == GsxPrepStage.Complete;

    public void Dispose()
    {
        _api.Mirror.SidChanged -= OnSidChanged;
        _simSession.SessionEnded -= OnSessionEnded;
        _timer.Dispose();
    }

    private void OnSidChanged(string? oldSid, string? newSid) => Reset("Couatl engine restart");

    /// <summary>The pilot left the flight (back to the main menu / new flight loading): the
    /// next session starts the chain from the top. A Couatl restart usually resets us anyway,
    /// but a session change without one must not inherit a half-finished (or Complete) chain.
    /// The edge itself is the store's (campaign #79) — never re-derived here.</summary>
    private void OnSessionEnded() => Reset("sim session ended");

    /// <summary>Startup resync (issue #30): the tracking LVARs say this gate session's
    /// preparation already ran before the app restarted — jump straight to Complete so the
    /// reposition/GPU/jetway chain is not re-driven. A later gate change or Couatl restart
    /// still resets the chain normally.</summary>
    public void SeedComplete(string reason)
    {
        if (_stage == GsxPrepStage.Complete)
        {
            return;
        }

        _sessionGateKey ??= _api.Mirror.GateContextKey;
        Advance(GsxPrepStage.Complete, $"seeded by startup resync — {reason}");
    }

    private void Reset(string reason)
    {
        var wasProgressed = _stage != GsxPrepStage.Idle || _sessionGateKey is not null;
        _stage = GsxPrepStage.Idle;
        _sessionGateKey = null;
        _cycle.ResetPrep();
        if (wasProgressed)
        {
            _logger.LogInformation("Ground preparation sequence reset ({Reason})", reason);
            PublishStage($"reset: {reason}");
            _eventLog.Record("gsx-ground-prep-stage", new { stage = _stage.ToString(), detail = $"reset: {reason}" });
        }
    }

    private async Task CycleAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            // All hold/reset/ordering policy is the pure machine (campaign #78) — the 2026-08
            // review found four reset triggers and three hold gates scattered through this
            // method with zero tests; they now live where a table test pins them.
            var decision = PrepStageMachine.Next(
                new PrepStageMachine.PrepInputs(
                    AutomationEnabled: _options.CurrentValue.AutomationEnabled,
                    SessionPhase: _simSession.Phase,
                    ResyncAssessed: _resyncState.IsAssessed,
                    VoiceActivationMode: IsVoiceActivation(_options.CurrentValue.GroundPrepActivation),
                    CycleStarted: _cycle.Started,
                    FlightPhase: _flightState.CurrentPhase,
                    GsxReady: _api.Readiness == GsxReadiness.Ready,
                    GateKey: _api.Mirror.GateContextKey,
                    SessionGateKey: _sessionGateKey,
                    Stage: _stage,
                    SettleUntil: _settleUntil),
                DateTimeOffset.UtcNow);

            if (decision.ReleasesHold)
            {
                ReleaseHold();
            }

            switch (decision.Command)
            {
                case PrepCommand.None:
                    return;

                case PrepCommand.Hold:
                    Hold(decision.Reason!, decision.UnknownParking);
                    return;

                case PrepCommand.Reset:
                    Reset(decision.Reason!);
                    return;

                case PrepCommand.AdvanceFromSettling:
                    Advance(GsxPrepStage.AnchorGate, "re-anchoring GSX to the occupied stand");
                    return;
            }

            _sessionGateKey ??= _api.Mirror.GateContextKey;

            switch (_stage)
            {
                case GsxPrepStage.Idle:
                    // The machine only lets RunStage through when the whole window is open —
                    // this transition is what makes "Repositioning" mean an actual reposition
                    // (issue #98). The step itself runs next cycle.
                    Advance(GsxPrepStage.Reposition, "starting ground preparation");
                    break;

                case GsxPrepStage.Reposition:
                    var repositionStatus = await _reposition.RunStepAsync().ConfigureAwait(false);
                    if (repositionStatus == GsxPrepStatus.Done)
                    {
                        _settleUntil = DateTimeOffset.UtcNow + RepositionSettleTime;
                        _sessionGateKey = null; // reposition may refresh the gate context
                        Advance(GsxPrepStage.Settling, "waiting for the position to settle");
                    }
                    break;

                case GsxPrepStage.AnchorGate:
                    // Issue #44: GSX persists its assigned facility across sim sessions; a new
                    // flight at a different stand needs an explicit gate.select or every
                    // service trigger is silently dropped.
                    if (await _gateAnchor.RunStepAsync().ConfigureAwait(false) == GsxPrepStatus.Done)
                    {
                        Advance(GsxPrepStage.GroundEquipment, "connecting GPU and placing chocks");
                    }
                    break;

                case GsxPrepStage.GroundEquipment:
                    var equipmentStatus = await _groundEquipment.RunPlacementStepAsync().ConfigureAwait(false);
                    if (equipmentStatus == GsxPrepStatus.Done)
                    {
                        Advance(GsxPrepStage.JetwayStairs, "connecting jetway or stairs");
                    }
                    break;

                case GsxPrepStage.JetwayStairs:
                    if (_jetwayStairs.RunStep() == GsxPrepStatus.Done)
                    {
                        Advance(GsxPrepStage.Complete, "ground preparation complete — departure services may run");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ground preparation cycle failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Unknown values read as auto — a typo in settings.json must not silently park
    /// the whole prep chain.</summary>
    private static bool IsVoiceActivation(string value)
        => string.Equals(value, "voice", StringComparison.OrdinalIgnoreCase);

    /// <summary>Logs a prep hold once per distinct reason (the cycle runs every 5 s — a log
    /// line per tick would drown the file while the user sits on the main menu). An
    /// unknown-parking hold additionally publishes the parking conflict (issue #44): that is
    /// what drives the Flight Status row and the FO's spoken guidance — on the 2026-08-23
    /// flight the hold fired twice and stayed a log line the pilot never saw.</summary>
    private void Hold(string reason, bool unknownParking = false)
    {
        if (_holdReason == reason)
        {
            return;
        }

        _holdReason = reason;
        _logger.LogInformation("Ground preparation holding: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", $"holding: {reason}"));
        PublishStage($"holding: {reason}");

        if (unknownParking)
        {
            _conflictPublished = true;
            _diagnostics.UpdateParkingConflict(new GsxParkingConflictView(
                DateTimeOffset.UtcNow, FacilityFromMenu() ?? ""));
        }
    }

    private void ReleaseHold()
    {
        if (_holdReason is null)
        {
            return;
        }

        _holdReason = null;
        _logger.LogInformation("Ground preparation hold released");
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", "hold released"));
        if (_conflictPublished)
        {
            // Whatever unknown-parking state stood is over (gate identified, phase moved on).
            _conflictPublished = false;
            _diagnostics.UpdateParkingConflict(null);
        }

        // A parked chain releasing a hold is not "running" (issue #98: after a mid-flight
        // restart the startup holds release into descent — the row must read as idle).
        PublishStage(_stage == GsxPrepStage.Idle ? "standing by" : "running");
    }

    /// <summary>The facility GSX itself names, when a menu is up that names one — the
    /// "Change Facility [...]" entry of the parking-change menu. The Select Position menu
    /// lists candidate stands without naming an anchor, so this often stays null and the
    /// advisory speaks its facility-less form.</summary>
    private string? FacilityFromMenu()
    {
        var entry = _api.Mirror.MenuShown
            ? _api.Mirror.Menu?.Entries.FirstOrDefault(
                e => e.StartsWith("Change Facility", StringComparison.OrdinalIgnoreCase))
            : null;
        return entry is null ? null : Menu.GsxQuestionCatalog.ExtractFacility(entry);
    }

    private void Advance(GsxPrepStage next, string detail)
    {
        _stage = next;
        if (next == GsxPrepStage.Complete)
        {
            // The shared departure cycle is how the automation (and everything else) sees
            // prep completion — the coordinator never talks to the automation directly.
            _cycle.MarkPrepComplete();
        }
        _logger.LogInformation("Ground prep -> {Stage}: {Detail}", next, detail);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", $"{next}: {detail}"));
        PublishStage(detail);
        // Session record for future log analysis (issue #45: the 2026-08-15 flight logs could
        // not answer "which prep step was running when the gate anchor failed").
        _eventLog.Record("gsx-ground-prep-stage", new { stage = next.ToString(), detail });
    }

    /// <summary>Pushes the current stage + a short human reason to the diagnostics store —
    /// the Flight Status page's ground-prep row (issue #45; called only on transitions and
    /// hold changes, never per cycle tick).</summary>
    private void PublishStage(string detail)
        => _diagnostics.UpdateGroundPrep(new GsxGroundPrepView(_stage.ToString(), detail));
}
