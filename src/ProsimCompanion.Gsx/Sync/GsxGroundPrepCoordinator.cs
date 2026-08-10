using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
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

    private enum Stage
    {
        Reposition,
        Settling,
        AnchorGate,
        GroundEquipment,
        JetwayStairs,
        Complete,
    }

    private readonly IGsxRemoteApi _api;
    private readonly GsxRepositionService _reposition;
    private readonly GsxGateAnchorService _gateAnchor;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly FlightStateEngine _flightState;
    private readonly SimSessionStore _simSession;
    private readonly GsxResyncState _resyncState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxGroundPrepCoordinator> _logger;
    private readonly Timer _timer;
    private Stage _stage = Stage.Reposition;
    private DateTimeOffset _settleUntil;
    private string? _sessionGateKey;
    private string? _holdReason;
    private int _running;

    public GsxGroundPrepCoordinator(
        IGsxRemoteApi api,
        GsxRepositionService reposition,
        GsxGateAnchorService gateAnchor,
        GsxGroundEquipmentService groundEquipment,
        GsxJetwayStairsService jetwayStairs,
        FlightStateEngine flightState,
        SimSessionStore simSession,
        GsxResyncState resyncState,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
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
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _reposition = reposition;
        _gateAnchor = gateAnchor;
        _groundEquipment = groundEquipment;
        _jetwayStairs = jetwayStairs;
        _flightState = flightState;
        _simSession = simSession;
        _resyncState = resyncState;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _api.Mirror.SidChanged += OnSidChanged;
        _simSession.PhaseChanged += OnSimSessionPhaseChanged;
        _timer = new Timer(_ => _ = CycleAsync(), null, CycleInterval, CycleInterval);
    }

    /// <summary>True once the whole preparation chain has run for this gate session — the
    /// departure service sequencer waits for this.</summary>
    public bool PrepComplete => _stage == Stage.Complete;

    public void Dispose()
    {
        _api.Mirror.SidChanged -= OnSidChanged;
        _simSession.PhaseChanged -= OnSimSessionPhaseChanged;
        _timer.Dispose();
    }

    private void OnSidChanged(string? oldSid, string? newSid) => Reset("Couatl engine restart");

    /// <summary>The pilot left the flight (back to the main menu / new flight loading): the
    /// next session starts the chain from the top. A Couatl restart usually resets us anyway,
    /// but a session change without one must not inherit a half-finished (or Complete) chain.</summary>
    private void OnSimSessionPhaseChanged(SimSessionPhase oldPhase, SimSessionPhase newPhase)
    {
        if (oldPhase is SimSessionPhase.InSession or SimSessionPhase.Walkaround
            && newPhase is SimSessionPhase.NotInSession or SimSessionPhase.Unknown)
        {
            Reset("sim session ended");
        }
    }

    /// <summary>Startup resync (issue #30): the tracking LVARs say this gate session's
    /// preparation already ran before the app restarted — jump straight to Complete so the
    /// reposition/GPU/jetway chain is not re-driven. A later gate change or Couatl restart
    /// still resets the chain normally.</summary>
    public void SeedComplete(string reason)
    {
        if (_stage == Stage.Complete)
        {
            return;
        }

        _sessionGateKey ??= _api.Mirror.GateContextKey;
        Advance(Stage.Complete, $"seeded by startup resync — {reason}");
    }

    private void Reset(string reason)
    {
        if (_stage != Stage.Reposition || _sessionGateKey is not null)
        {
            _logger.LogInformation("Ground preparation sequence reset ({Reason})", reason);
        }
        _stage = Stage.Reposition;
        _sessionGateKey = null;
    }

    private async Task CycleAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_options.CurrentValue.AutomationEnabled)
            {
                return;
            }

            // Predecessor-parity session gate: ProSim pushes plausible cold-and-dark data and
            // the Couatl socket answers while MSFS is still on the main menu or loading, so
            // every downstream precondition can pass with no pilot in the session — the old
            // Prosim2GSX held on camera state for exactly this reason. Unknown (SimConnect
            // absent) holds too: a reposition teleports the aircraft, and a signal we cannot
            // read is not a signal that passed. Walkaround holds — services must not be
            // driven while the pilot is outside the aircraft.
            var sessionPhase = _simSession.Phase;
            if (sessionPhase != SimSessionPhase.InSession)
            {
                Hold($"MSFS session not active ({sessionPhase})");
                return;
            }

            // Startup-resync ordering (issue #30): the assessment may be about to seed this
            // chain as already complete — the 2026-08-09 flight test caught the reposition
            // firing 150 ms before the seed landed. Prep holds for the verdict just like the
            // departure sequencer; the assessment self-times-out, so this cannot deadlock.
            if (!_resyncState.IsAssessed)
            {
                Hold("waiting for the startup resync assessment");
                return;
            }

            ReleaseHold();

            var phase = _flightState.CurrentPhase;
            if (phase is not (FlightPhase.Preflight or FlightPhase.ColdAndDark))
            {
                // Off the ground-prep window; a fresh Preflight after flight restarts the chain.
                if (_stage != Stage.Reposition
                    && phase is FlightPhase.TaxiIn or FlightPhase.Shutdown or FlightPhase.Cruise or FlightPhase.Climb)
                {
                    Reset($"phase {phase}");
                }
                return;
            }

            if (_api.Readiness != GsxReadiness.Ready)
            {
                return;
            }

            var gateKey = _api.Mirror.GateContextKey;
            if (gateKey is null)
            {
                return;
            }

            if (_sessionGateKey is not null
                && !string.Equals(gateKey, _sessionGateKey, StringComparison.Ordinal)
                && _stage is Stage.JetwayStairs or Stage.Complete)
            {
                // The gate genuinely changed mid/after prep (not the reposition itself settling).
                Reset($"gate changed to {gateKey}");
            }
            _sessionGateKey ??= gateKey;

            switch (_stage)
            {
                case Stage.Reposition:
                    var repositionStatus = await _reposition.RunStepAsync().ConfigureAwait(false);
                    if (repositionStatus == GsxPrepStatus.Done)
                    {
                        _settleUntil = DateTimeOffset.UtcNow + RepositionSettleTime;
                        _sessionGateKey = null; // reposition may refresh the gate context
                        Advance(Stage.Settling, "waiting for the position to settle");
                    }
                    break;

                case Stage.Settling:
                    if (DateTimeOffset.UtcNow >= _settleUntil)
                    {
                        Advance(Stage.AnchorGate, "re-anchoring GSX to the occupied stand");
                    }
                    break;

                case Stage.AnchorGate:
                    // Issue #44: GSX persists its assigned facility across sim sessions; a new
                    // flight at a different stand needs an explicit gate.select or every
                    // service trigger is silently dropped.
                    if (await _gateAnchor.RunStepAsync().ConfigureAwait(false) == GsxPrepStatus.Done)
                    {
                        Advance(Stage.GroundEquipment, "connecting GPU and placing chocks");
                    }
                    break;

                case Stage.GroundEquipment:
                    var equipmentStatus = await _groundEquipment.RunPlacementStepAsync().ConfigureAwait(false);
                    if (equipmentStatus == GsxPrepStatus.Done)
                    {
                        Advance(Stage.JetwayStairs, "connecting jetway or stairs");
                    }
                    break;

                case Stage.JetwayStairs:
                    if (_jetwayStairs.RunStep() == GsxPrepStatus.Done)
                    {
                        Advance(Stage.Complete, "ground preparation complete — departure services may run");
                    }
                    break;

                case Stage.Complete:
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

    /// <summary>Logs a prep hold once per distinct reason (the cycle runs every 5 s — a log
    /// line per tick would drown the file while the user sits on the main menu).</summary>
    private void Hold(string reason)
    {
        if (_holdReason == reason)
        {
            return;
        }

        _holdReason = reason;
        _logger.LogInformation("Ground preparation holding: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", $"holding: {reason}"));
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
    }

    private void Advance(Stage next, string detail)
    {
        _stage = next;
        _logger.LogInformation("Ground prep -> {Stage}: {Detail}", next, detail);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", $"{next}: {detail}"));
    }
}
