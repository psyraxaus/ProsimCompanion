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

/// <summary>
/// Enforces the ground-preparation order (owner-specified): <b>reposition → settle → GPU +
/// chocks → jetway/stairs → departure services</b>. The individual modules keep their own
/// guards and safe-fail behaviour; this coordinator only decides <i>when</i> each may run, and
/// gates the departure sequencer until preparation is complete. Resets for a new session on
/// gate change, Couatl restart, or returning to preparation after flight.
/// </summary>
public sealed class GsxGroundPrepCoordinator : IDisposable
{
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RepositionSettleTime = TimeSpan.FromSeconds(12);

    private enum Stage
    {
        Reposition,
        Settling,
        GroundEquipment,
        JetwayStairs,
        Complete,
    }

    private readonly IGsxRemoteApi _api;
    private readonly GsxRepositionService _reposition;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly FlightStateEngine _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxGroundPrepCoordinator> _logger;
    private readonly Timer _timer;
    private Stage _stage = Stage.Reposition;
    private DateTimeOffset _settleUntil;
    private string? _sessionGateKey;
    private int _running;

    public GsxGroundPrepCoordinator(
        IGsxRemoteApi api,
        GsxRepositionService reposition,
        GsxGroundEquipmentService groundEquipment,
        GsxJetwayStairsService jetwayStairs,
        FlightStateEngine flightState,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxGroundPrepCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(reposition);
        ArgumentNullException.ThrowIfNull(groundEquipment);
        ArgumentNullException.ThrowIfNull(jetwayStairs);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _reposition = reposition;
        _groundEquipment = groundEquipment;
        _jetwayStairs = jetwayStairs;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _api.Mirror.SidChanged += OnSidChanged;
        _timer = new Timer(_ => _ = CycleAsync(), null, CycleInterval, CycleInterval);
    }

    /// <summary>True once the whole preparation chain has run for this gate session — the
    /// departure service sequencer waits for this.</summary>
    public bool PrepComplete => _stage == Stage.Complete;

    public void Dispose()
    {
        _api.Mirror.SidChanged -= OnSidChanged;
        _timer.Dispose();
    }

    private void OnSidChanged(string? oldSid, string? newSid) => Reset("Couatl engine restart");

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

    private void Advance(Stage next, string detail)
    {
        _stage = next;
        _logger.LogInformation("Ground prep -> {Stage}: {Detail}", next, detail);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "ground prep", $"{next}: {detail}"));
    }
}
