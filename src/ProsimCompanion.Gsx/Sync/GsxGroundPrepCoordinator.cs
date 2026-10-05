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

    /// <summary>Throttle for the waiting/stalled diagnostics (2026-09-25 cold-and-dark GPU
    /// report): a chain that never reaches the equipment step was invisible on the Status
    /// page — one decision line per 30 s says where it sits without flooding the log.</summary>
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long the unknown-parking hold must stand before it is published as a
    /// parking conflict (issue #157, EFHK 2026-10-04 18:52:24Z: the hold was published on its
    /// first cycle and the FO spoke "GSX doesn't recognise our parking position" 7.5 s before
    /// GSX named Gate 35). Four cycles; the hold itself is logged and shown at once, only the
    /// advisory waits. 2026-10-05 (issue #141): the 7.5 s was the pilot's own GSX menu click,
    /// not GSX loading — the grace now also covers the parking wake's first attempt, and the
    /// conflict additionally waits for that attempt to fail (<see cref="ConflictDue"/>).</summary>
    internal static readonly TimeSpan UnknownParkingGrace = TimeSpan.FromSeconds(20);

    private readonly IGsxRemoteApi _api;
    private readonly GsxRepositionService _reposition;
    private readonly GsxGateAnchorService _gateAnchor;
    private readonly GsxParkingWakeService _parkingWake;
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
    private bool? _publishedMenuUnreachable;
    private DateTimeOffset? _unknownParkingSince;
    private int _running;
    private DateTimeOffset _stageEnteredAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastWaitingLog;
    private DateTimeOffset _lastStalledLog;

    public GsxGroundPrepCoordinator(
        IGsxRemoteApi api,
        GsxRepositionService reposition,
        GsxGateAnchorService gateAnchor,
        GsxParkingWakeService parkingWake,
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
        ArgumentNullException.ThrowIfNull(parkingWake);
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
        _parkingWake = parkingWake;
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
        Advance(GsxPrepStage.Complete, $"seeded by startup resync — {reason}; GPU/chocks placement skipped");
    }

    private void Reset(string reason)
    {
        var wasProgressed = _stage != GsxPrepStage.Idle || _sessionGateKey is not null;
        _stage = GsxPrepStage.Idle;
        _stageEnteredAt = DateTimeOffset.UtcNow;
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
            var now = DateTimeOffset.UtcNow;
            var readiness = _api.Readiness;
            var gateKey = _api.Mirror.GateContextKey;
            var decision = PrepStageMachine.Next(
                new PrepStageMachine.PrepInputs(
                    AutomationEnabled: _options.CurrentValue.AutomationEnabled,
                    SessionPhase: _simSession.Phase,
                    ResyncAssessed: _resyncState.IsAssessed,
                    VoiceActivationMode: IsVoiceActivation(_options.CurrentValue.GroundPrepActivation),
                    CycleStarted: _cycle.Started,
                    FlightPhase: _flightState.CurrentPhase,
                    GsxReady: readiness == GsxReadiness.Ready,
                    GateKey: gateKey,
                    SessionGateKey: _sessionGateKey,
                    Stage: _stage,
                    SettleUntil: _settleUntil),
                now);

            if (decision.ReleasesHold)
            {
                ReleaseHold();
            }

            NoteWaitingForGsx(decision, readiness, gateKey, now);

            switch (decision.Command)
            {
                case PrepCommand.None:
                    return;

                case PrepCommand.Hold:
                    await HoldAsync(decision, now).ConfigureAwait(false);
                    return;

                case PrepCommand.Reset:
                    Reset(decision.Reason!);
                    return;

                case PrepCommand.AdvanceFromSettling:
                    Advance(GsxPrepStage.AnchorGate, "re-anchoring GSX to the occupied stand");
                    return;
            }

            _sessionGateKey ??= _api.Mirror.GateContextKey;

            GsxPrepStatus? stepStatus = null;
            switch (_stage)
            {
                case GsxPrepStage.Idle:
                    // The machine only lets RunStage through when the whole window is open —
                    // this transition is what makes "Repositioning" mean an actual reposition
                    // (issue #98). The step itself runs next cycle.
                    if (_cycle.IsTurnaround)
                    {
                        // A turnaround never repositions: the aircraft is on the stand it
                        // taxied to, jetway attached. 2026-08-29 ESSA: the reposition fired
                        // under the passengers and disengaged the deboarding jetway.
                        Advance(GsxPrepStage.GroundEquipment, "turnaround — on stand already, skipping reposition");
                        break;
                    }

                    Advance(GsxPrepStage.Reposition, "starting ground preparation");
                    break;

                case GsxPrepStage.Reposition:
                    var repositionStatus = await _reposition.RunStepAsync().ConfigureAwait(false);
                    stepStatus = repositionStatus;
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
                    stepStatus = await _gateAnchor.RunStepAsync().ConfigureAwait(false);
                    if (stepStatus == GsxPrepStatus.Done)
                    {
                        Advance(GsxPrepStage.GroundEquipment, "connecting GPU and placing chocks");
                    }
                    break;

                case GsxPrepStage.GroundEquipment:
                    var equipmentStatus = await _groundEquipment.RunPlacementStepAsync().ConfigureAwait(false);
                    stepStatus = equipmentStatus;
                    if (equipmentStatus == GsxPrepStatus.Done)
                    {
                        Advance(GsxPrepStage.JetwayStairs, "connecting jetway or stairs");
                    }
                    break;

                case GsxPrepStage.JetwayStairs:
                    stepStatus = _jetwayStairs.RunStep();
                    if (stepStatus == GsxPrepStatus.Done)
                    {
                        Advance(GsxPrepStage.Complete, "ground preparation complete — departure services may run");
                    }
                    break;
            }

            NoteStalledStage(stepStatus, now);
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
    internal static bool IsVoiceActivation(string value)
        => string.Equals(value, "voice", StringComparison.OrdinalIgnoreCase);

    /// <summary>An unknown-parking hold first tries the remedy the pilot would: it asks GSX
    /// for its menu, which is what makes GSX look for the stand (issue #141 — see
    /// <see cref="GsxParkingWakeService"/>). Every other hold is only logged and shown.</summary>
    private async Task HoldAsync(PrepStageMachine.PrepDecision decision, DateTimeOffset now)
    {
        if (!decision.UnknownParking)
        {
            _unknownParkingSince = null;
            _parkingWake.Reset();
            Hold(decision.Reason!, wake: null, now);
            return;
        }

        _unknownParkingSince ??= now;
        var wake = await _parkingWake.RunStepAsync(_unknownParkingSince.Value, now).ConfigureAwait(false);
        if (wake == GsxParkingWakeStatus.Named)
        {
            // The next cycle reads the gate and releases the hold.
            return;
        }

        // The wake step can take two rung waits — the grace is judged against the clock now.
        Hold(UnknownParkingReason(decision.Reason!, wake), wake, DateTimeOffset.UtcNow);
    }

    /// <summary>Logs a prep hold once per distinct reason (the cycle runs every 5 s — a log
    /// line per tick would drown the file while the user sits on the main menu). An
    /// unknown-parking hold additionally publishes the parking conflict (issue #44): that is
    /// what drives the Flight Status row and the FO's spoken guidance — on the 2026-08-23
    /// flight the hold fired twice and stayed a log line the pilot never saw. The conflict
    /// waits for <see cref="UnknownParkingGrace"/> AND for the parking wake to have failed
    /// (issue #141): a parking GSX names once its menu is asked for was never a conflict.</summary>
    private void Hold(string reason, GsxParkingWakeStatus? wake, DateTimeOffset now)
    {
        if (_holdReason != reason)
        {
            _holdReason = reason;
            _logger.LogInformation("Ground preparation holding: {Reason}", reason);
            _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", $"holding: {reason}"));
            PublishStage($"holding: {reason}");
        }

        if (!ConflictDue(wake, _unknownParkingSince, now))
        {
            return;
        }

        // Republished when the guidance changes kind: "open the GSX menu" becomes "pick the
        // stand" once a menu is up and GSX still names no parking.
        var menuUnreachable = wake == GsxParkingWakeStatus.MenuUnreachable;
        if (_publishedMenuUnreachable != menuUnreachable)
        {
            _publishedMenuUnreachable = menuUnreachable;
            _diagnostics.UpdateParkingConflict(new GsxParkingConflictView(
                DateTimeOffset.UtcNow, FacilityFromMenu() ?? "")
            {
                MenuUnreachable = menuUnreachable,
            });
        }
    }

    /// <summary>True once the unknown-parking hold has stood for the whole grace.</summary>
    internal static bool UnknownParkingSettled(DateTimeOffset? since, DateTimeOffset now)
        => since is { } start && now - start >= UnknownParkingGrace;

    /// <summary>An unknown-parking hold is a conflict only when the wake could not resolve it
    /// and the grace has run. While the wake is still waiting for its first attempt nothing
    /// is published — 2026-10-05 LKPR: the FO spoke at a stand GSX named the moment its menu
    /// was opened.</summary>
    internal static bool ConflictDue(GsxParkingWakeStatus? wake, DateTimeOffset? since, DateTimeOffset now)
        => wake is GsxParkingWakeStatus.Unresolved or GsxParkingWakeStatus.MenuUnreachable
            && UnknownParkingSettled(since, now);

    /// <summary>The pilot-facing hold reason (Flight Status page) for the unknown-parking
    /// hold: what the app is doing about it, or the one action left to the pilot.</summary>
    internal static string UnknownParkingReason(string machineReason, GsxParkingWakeStatus wake) => wake switch
    {
        GsxParkingWakeStatus.Waiting
            => "GSX has not identified the parking — opening the GSX menu so it looks for the stand",
        GsxParkingWakeStatus.MenuUnreachable
            => "GSX has not identified the parking and its menu did not open — open the GSX menu once from the toolbar",
        _ => machineReason,
    };

    private void ReleaseHold()
    {
        _unknownParkingSince = null;
        _parkingWake.Reset();
        if (_holdReason is null)
        {
            return;
        }

        _holdReason = null;
        _logger.LogInformation("Ground preparation hold released");
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", "hold released"));
        if (_publishedMenuUnreachable is not null)
        {
            // Whatever unknown-parking state stood is over (gate identified, phase moved on).
            _publishedMenuUnreachable = null;
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

    /// <summary>The chain cannot run because GSX is not Ready or has no gate context (the
    /// machine answers None / the unknown-parking Hold): say so on the Status page once per
    /// <see cref="DiagnosticInterval"/> so a chain that never reaches the equipment step is
    /// distinguishable from one that placed the GPU and lost it. Only while at the gate with
    /// the chain unfinished — a parked or completed chain has nothing to wait for.</summary>
    private void NoteWaitingForGsx(
        PrepStageMachine.PrepDecision decision, GsxReadiness readiness, string? gateKey, DateTimeOffset now)
    {
        var blockedOnGsx = readiness != GsxReadiness.Ready || gateKey is null;
        var relevant = decision.Command == PrepCommand.None || decision.UnknownParking;
        if (!blockedOnGsx
            || !relevant
            || _stage == GsxPrepStage.Complete
            || !_options.CurrentValue.AutomationEnabled
            || !_flightState.CurrentPhase.IsAtGate()
            || now - _lastWaitingLog < DiagnosticInterval)
        {
            return;
        }

        _lastWaitingLog = now;
        RecordDecision($"waiting for GSX (readiness={readiness}, gate={gateKey ?? "none"})");
    }

    /// <summary>A stage step that keeps answering Waiting/Pending past
    /// <see cref="DiagnosticInterval"/> is reported once per interval with its age, so a
    /// reposition that never settles or a placement whose writes keep failing shows on the
    /// Status page instead of sitting silently behind the last "→ stage" line.</summary>
    private void NoteStalledStage(GsxPrepStatus? stepStatus, DateTimeOffset now)
    {
        if (stepStatus is null or GsxPrepStatus.Done)
        {
            return;
        }

        var age = now - _stageEnteredAt;
        if (age < DiagnosticInterval || now - _lastStalledLog < DiagnosticInterval)
        {
            return;
        }

        _lastStalledLog = now;
        RecordDecision($"{_stage} still waiting after {(int)age.TotalSeconds}s");
    }

    private void RecordDecision(string reason)
    {
        _logger.LogInformation("Ground prep: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", reason));
    }

    private void Advance(GsxPrepStage next, string detail)
    {
        _stage = next;
        _stageEnteredAt = DateTimeOffset.UtcNow;
        _lastStalledLog = default;
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
