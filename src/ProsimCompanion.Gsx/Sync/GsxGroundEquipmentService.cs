using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>Outcome of a manual ground-power write (issue #170).</summary>
public enum GroundPowerResult
{
    /// <summary>The dataref write went out.</summary>
    Written,

    /// <summary>The requested state already holds.</summary>
    AlreadyThere,

    /// <summary>Refused by an interlock — see the detail.</summary>
    Interlocked,

    /// <summary>The write failed (ProSim not connected / not writable).</summary>
    Failed,
}

/// <summary>The manual ground-power seam (issue #170): the crew's "connect ground power" when
/// GSX offers no GPU service at the stand, and "disconnect ground power" at any time — the
/// same ProSim dataref the ground-preparation chain and the beacon-edge removal write.</summary>
public interface IGroundPowerEquipment
{
    /// <summary>Connects the ProSim ground power unit (the allow-listed dataref write the
    /// prep chain makes). No automation option gates it — it is an explicit crew request.</summary>
    Task<(GroundPowerResult Result, string Detail)> ConnectGpuAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes the ground power unit away. Interlock: never while external power is
    /// still feeding the buses — the crew switches EXT PWR off first, as on the beacon-edge
    /// removal the APU is already carrying the load.</summary>
    Task<(GroundPowerResult Result, string Detail)> DisconnectGpuAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Ground equipment automation: places GPU + chocks (+ PCA per the tri-state) once per ground
/// session when preparation begins, and removes them on the beacon-on edge before pushback.
/// Predecessor extras (#10): GPU is skipped at placement when the APU already runs and
/// gsx.connectGpuWithApuRunning is off; PCA is a Never/Always/OnlyJetway tri-state (jetway
/// stands detected from L:FSDT_GSX_JETWAY ≠ 2) with an override that disconnects a
/// not-allowed PCA; gradual removal clears the GPU on external-power-off and the chocks on
/// park-brake-set during the pushback phase. Safety interlocks from the predecessors: chocks
/// are never removed with the park brake off, and everything is decision-logged.
/// </summary>
public sealed class GsxGroundEquipmentService : IDisposable, IGroundPowerEquipment
{
    /// <summary>L:FSDT_GSX_JETWAY value meaning "no jetway at this stand" (GsxServiceState
    /// NotAvailable — the same predicate Prosim2GSX used for "only on jetway stand").</summary>
    private const int JetwayNotAvailable = 2;

    private readonly GsxProsimWriter _writer;
    private readonly IFlightPhaseSource _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxGroundEquipmentService> _logger;
    private readonly IDataRefSubscription<int> _beacon;
    private readonly IDataRefSubscription<int> _parkBrake;
    private readonly IDataRefSubscription<bool> _apuRunning;
    private readonly IDataRefSubscription<bool> _externalPowerConnected;
    private readonly IDataRefSubscription<bool> _pcaConnected;
    private readonly IDataRefSubscription<bool> _gpuConnected;
    private readonly IDataRefSubscription<bool> _chocksPlaced;
    private readonly IDataRefSubscription<double> _jetwayState;
    private bool _placedThisSession;
    private bool _removedThisSession;
    private bool _beaconWasOn;

    // Gradual-removal GPU latch (2026-09-25 cold-and-dark report): the GPU rule is a
    // falling edge on external power — it must have been SEEN feeding the buses before
    // "external power off" means the crew let go of it. At cold and dark the dataref is
    // false the instant the GPU is placed (nobody pressed EXT PWR yet), and the plain
    // "GPU on && ext power off" test pulled the unit within a second of placement.
    private bool _externalPowerSeen;
    private bool _gpuRemovalWritten;

    public GsxGroundEquipmentService(
        IProsimDataRefs prosim,
        ISimVars simVars,
        GsxProsimWriter writer,
        IFlightPhaseSource flightState,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxGroundEquipmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _beacon = prosim.Subscribe(ProsimDataRefNames.OhExtLtBeacon);
        _parkBrake = prosim.Subscribe(ProsimDataRefNames.MipParkingBrake);
        _apuRunning = prosim.Subscribe(ProsimDataRefNames.ApuRunning);
        _externalPowerConnected = prosim.Subscribe(ProsimDataRefNames.ElecExternalConnect);
        _pcaConnected = prosim.Subscribe(ProsimDataRefNames.GroundPreconditionedAir);
        _gpuConnected = prosim.Subscribe(ProsimDataRefNames.GroundPower);
        _chocksPlaced = prosim.Subscribe(ProsimDataRefNames.Chocks);
        _jetwayState = simVars.Subscribe(GsxLvarNames.Jetway);

        _flightState.PhaseChanged += OnPhaseChanged;
        _beacon.ValueChanged += OnBeaconChanged;
        _gpuConnected.ValueChanged += OnGpuConnectedChanged;
    }

    /// <summary>Surfaces ProSim's ground-power state to the Flight Status page (issue #33):
    /// the dataref is the attachment truth — GSX's GPU service flips back to "available"
    /// while the unit stays physically connected.</summary>
    private void OnGpuConnectedChanged(object? sender, EventArgs e)
        => _diagnostics.UpdateGroundPower(
            _gpuConnected.RawValue is null ? null : _gpuConnected.Value);

    public void Dispose()
    {
        _flightState.PhaseChanged -= OnPhaseChanged;
        _beacon.ValueChanged -= OnBeaconChanged;
        _gpuConnected.ValueChanged -= OnGpuConnectedChanged;
        _beacon.Dispose();
        _parkBrake.Dispose();
        _apuRunning.Dispose();
        _externalPowerConnected.Dispose();
        _pcaConnected.Dispose();
        _gpuConnected.Dispose();
        _chocksPlaced.Dispose();
        _jetwayState.Dispose();
    }

    /// <summary>Effective PCA tri-state: pcaMode when set, else the legacy autoPca bool.</summary>
    private string EffectivePcaMode
    {
        get
        {
            var options = _options.CurrentValue;
            return !string.IsNullOrWhiteSpace(options.PcaMode)
                ? options.PcaMode
                : options.AutoPca ? "always" : "never";
        }
    }

    /// <summary>True when PCA placement is allowed here — "always", or "onlyJetway" at a
    /// stand whose jetway GSX reports as existing.</summary>
    public bool PcaAllowedHere => EffectivePcaMode.ToLowerInvariant() switch
    {
        "always" => true,
        "onlyjetway" => (int)_jetwayState.Value != JetwayNotAvailable,
        _ => false,
    };

    /// <summary>One coordinator-driven placement attempt — step 3 of ground prep (after
    /// reposition settles). Returns Done when placed, already placed, or disabled; Waiting
    /// when the writes failed (retried next cycle).</summary>
    public async Task<GsxPrepStatus> RunPlacementStepAsync()
    {
        if (!Enabled || _placedThisSession)
        {
            return GsxPrepStatus.Done;
        }

        _placedThisSession = true;
        await PlaceEquipmentAsync().ConfigureAwait(false);
        return _placedThisSession ? GsxPrepStatus.Done : GsxPrepStatus.Waiting;
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.AutoGroundEquipment;

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // Placement is driven by the ground-prep coordinator (after reposition); this handler
        // only resets the session on arrival so the next turnaround places again.
        if (e.Current == FlightPhase.Shutdown)
        {
            _placedThisSession = false;
            _removedThisSession = false;
            ResetGpuLatch();
        }
    }

    private void OnBeaconChanged(object? sender, EventArgs e)
    {
        var beaconOn = _beacon.Value != 0;
        var risingEdge = beaconOn && !_beaconWasOn;
        _beaconWasOn = beaconOn;

        if (!risingEdge || !Enabled || _removedThisSession)
        {
            return;
        }

        // When the beacon-orchestrated pushback sequence is on, IT owns removal timing
        // (doors → jetway → equipment with crew-realism delays) via RemoveForDepartureAsync.
        if (_options.CurrentValue.BeaconPushbackSequenceEnabled)
        {
            return;
        }

        // Beacon-on before departure = crew signals readiness: clear the ground equipment.
        if (_flightState.CurrentPhase.IsBeforeTaxiOut())
        {
            _removedThisSession = true;
            _ = RemoveEquipmentAsync();
        }
    }

    /// <summary>Departure-sequence equipment step: clears PCA + GPU + chocks (park-brake
    /// interlocked) once; safe to call repeatedly.</summary>
    public async Task RemoveForDepartureAsync()
    {
        if (!Enabled || _removedThisSession)
        {
            return;
        }
        _removedThisSession = true;
        await RemoveEquipmentAsync().ConfigureAwait(false);
    }

    /// <summary>Condition-driven gradual removal (predecessor GradualGroundEquipRemoval),
    /// called at 1 Hz from the pushback shell while in a departure ground phase with
    /// departure services complete and the beacon sequence off: the GPU clears once external
    /// power has been seen feeding the buses and then stops (a falling edge — a GPU that was
    /// never switched on is left alone), the chocks once the park brake is set AND the GPU is
    /// already gone. Cached reads only; the GPU-off decision is logged and written once per
    /// removal (the ProSim echo lags the write by a tick or two, so the raw condition alone
    /// repeated the line every second); chock writes to an already-false dataref are skipped
    /// via the mirror reads.</summary>
    public async Task TickGradualRemovalAsync()
    {
        if (!Enabled || _removedThisSession || !_options.CurrentValue.GradualGroundEquipRemoval)
        {
            return;
        }

        if (_externalPowerConnected.Value)
        {
            _externalPowerSeen = true;
        }

        if (_externalPowerSeen && _gpuConnected.Value && !_externalPowerConnected.Value && !_gpuRemovalWritten)
        {
            _gpuRemovalWritten = true;
            RecordDecision("ground equipment", "gradual removal — GPU off (external power disconnected)");
            await _writer.WriteAsync(ProsimDataRefNames.GroundPower.Name, false).ConfigureAwait(false);
        }

        if (_chocksPlaced.Value && _parkBrake.Value != 0 && !_gpuConnected.Value)
        {
            RecordDecision("ground equipment", "gradual removal — chocks out (park brake set, GPU gone)");
            await _writer.WriteAsync(ProsimDataRefNames.Chocks.Name, false).ConfigureAwait(false);
        }
    }

    /// <summary>Places chocks (+ PCA when allowed) on arrival — called by the arrival service
    /// after its randomized chock delay elapses.</summary>
    public async Task PlaceArrivalEquipmentAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var withPca = PcaAllowedHere;
        RecordDecision("ground equipment", "arrival — placing chocks" + (withPca ? " + PCA" : ""));
        await _writer.WriteAsync(ProsimDataRefNames.Chocks.Name, true).ConfigureAwait(false);
        if (withPca)
        {
            await _writer.WriteAsync(ProsimDataRefNames.GroundPreconditionedAir.Name, true).ConfigureAwait(false);
        }
    }

    private async Task PlaceEquipmentAsync()
    {
        var options = _options.CurrentValue;

        // GPU-with-APU rule: the crew already runs the APU — skip the GPU when configured.
        var skipGpuForApu = !options.ConnectGpuWithApuRunning && _apuRunning.Value;
        var withPca = PcaAllowedHere;

        RecordDecision(
            "ground equipment",
            "placing chocks"
                + (skipGpuForApu ? " (GPU skipped — APU running)" : " + GPU")
                + (withPca ? " + PCA" : $" (PCA {EffectivePcaMode})"));

        var ok = await _writer.WriteAsync(ProsimDataRefNames.Chocks.Name, true).ConfigureAwait(false);
        if (!skipGpuForApu)
        {
            var gpuOk = await _writer.WriteAsync(ProsimDataRefNames.GroundPower.Name, true).ConfigureAwait(false);
            ok &= gpuOk;
            if (gpuOk)
            {
                // A re-placement after removal starts the falling-edge latch fresh.
                ResetGpuLatch();
            }
        }

        if (withPca)
        {
            ok &= await _writer.WriteAsync(ProsimDataRefNames.GroundPreconditionedAir.Name, true).ConfigureAwait(false);
        }
        else if (options.PcaOverride && _pcaConnected.Value)
        {
            // Predecessor PcaOverride: a PCA connected by a saved panel state gets removed
            // when the configuration does not allow it here.
            RecordDecision("ground equipment", "disconnecting PCA (connected but not allowed here)");
            ok &= await _writer.WriteAsync(ProsimDataRefNames.GroundPreconditionedAir.Name, false).ConfigureAwait(false);
        }

        if (!ok)
        {
            _placedThisSession = false; // failures are logged by the writer; retry on the next check
        }
    }

    private async Task RemoveEquipmentAsync()
    {
        RecordDecision("ground equipment", "beacon on — removing PCA + GPU + chocks");
        await _writer.WriteAsync(ProsimDataRefNames.GroundPreconditionedAir.Name, false).ConfigureAwait(false);
        await _writer.WriteAsync(ProsimDataRefNames.GroundPower.Name, false).ConfigureAwait(false);

        // Interlock: never pull the chocks with the park brake off.
        if (_parkBrake.Value != 0)
        {
            await _writer.WriteAsync(ProsimDataRefNames.Chocks.Name, false).ConfigureAwait(false);
        }
        else
        {
            _removedThisSession = false; // chocks still down — retry on the next beacon edge
            RecordDecision("ground equipment", "chocks kept — park brake is not set");
        }
    }

    /// <inheritdoc />
    public async Task<(GroundPowerResult Result, string Detail)> ConnectGpuAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_gpuConnected.Value)
        {
            return (GroundPowerResult.AlreadyThere, "Ground power is already connected.");
        }

        RecordDecision("ground equipment", "crew request — connecting the GPU (ProSim ground power)");
        var ok = await _writer.WriteAsync(ProsimDataRefNames.GroundPower.Name, true).ConfigureAwait(false);
        if (!ok)
        {
            return (GroundPowerResult.Failed, "The ground-power write to ProSim failed — see the log.");
        }

        // A manual placement starts the gradual-removal falling-edge latch fresh.
        ResetGpuLatch();
        return (GroundPowerResult.Written, "Ground power connected.");
    }

    /// <inheritdoc />
    public async Task<(GroundPowerResult Result, string Detail)> DisconnectGpuAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gpuConnected.Value)
        {
            return (GroundPowerResult.AlreadyThere, "Ground power is not connected — nothing to remove.");
        }

        if (_externalPowerConnected.Value)
        {
            RecordDecision("ground equipment", "crew request — GPU kept, external power is still feeding the aircraft");
            return (GroundPowerResult.Interlocked,
                "External power is still feeding the aircraft — switch EXT PWR off (APU or engines on the buses) first.");
        }

        RecordDecision("ground equipment", "crew request — removing the GPU (ProSim ground power)");
        var ok = await _writer.WriteAsync(ProsimDataRefNames.GroundPower.Name, false).ConfigureAwait(false);
        return ok
            ? (GroundPowerResult.Written, "Ground power disconnected.")
            : (GroundPowerResult.Failed, "The ground-power write to ProSim failed — see the log.");
    }

    /// <summary>Forgets that external power was ever seen — the next gradual-removal pass
    /// must observe a fresh rise before a fall may pull the GPU.</summary>
    private void ResetGpuLatch()
    {
        _externalPowerSeen = false;
        _gpuRemovalWritten = false;
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
