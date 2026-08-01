using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Ground equipment automation: places GPU + chocks (+ PCA when configured) once per ground
/// session when preparation begins, and removes them on the beacon-on edge before pushback.
/// Safety interlocks from the predecessors: chocks are never removed with the park brake off
/// (the aircraft would roll), and everything is decision-logged.
/// </summary>
public sealed class GsxGroundEquipmentService : IDisposable
{
    private readonly IProsimDataRefs _prosim;
    private readonly FlightStateEngine _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxGroundEquipmentService> _logger;
    private readonly IDataRefSubscription _beacon;
    private readonly IDataRefSubscription _parkBrake;
    private bool _placedThisSession;
    private bool _removedThisSession;
    private bool _beaconWasOn;

    public GsxGroundEquipmentService(
        IProsimDataRefs prosim,
        FlightStateEngine flightState,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxGroundEquipmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _beacon = prosim.Subscribe(ProsimDataRefNames.OhExtLtBeacon, DataRefTier.Normal);
        _parkBrake = prosim.Subscribe(ProsimDataRefNames.MipParkingBrake, DataRefTier.Normal);

        _flightState.PhaseChanged += OnPhaseChanged;
        _beacon.ValueChanged += OnBeaconChanged;
    }

    public void Dispose()
    {
        _flightState.PhaseChanged -= OnPhaseChanged;
        _beacon.ValueChanged -= OnBeaconChanged;
        _beacon.Dispose();
        _parkBrake.Dispose();
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.AutoGroundEquipment;

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        switch (e.Current)
        {
            case FlightPhase.Preflight when Enabled && !_placedThisSession:
                _placedThisSession = true;
                _ = PlaceEquipmentAsync();
                break;

            case FlightPhase.Shutdown:
                // Arrived: next ground session may place equipment again.
                _placedThisSession = false;
                _removedThisSession = false;
                break;
        }
    }

    private void OnBeaconChanged(object? sender, EventArgs e)
    {
        var beaconOn = _beacon.GetValue(0) != 0;
        var risingEdge = beaconOn && !_beaconWasOn;
        _beaconWasOn = beaconOn;

        if (!risingEdge || !Enabled || _removedThisSession)
        {
            return;
        }

        // Beacon-on before departure = crew signals readiness: clear the ground equipment.
        if (_flightState.CurrentPhase is FlightPhase.Preflight or FlightPhase.ColdAndDark or FlightPhase.PushbackAndStart)
        {
            _removedThisSession = true;
            _ = RemoveEquipmentAsync();
        }
    }

    private async Task PlaceEquipmentAsync()
    {
        try
        {
            RecordDecision("ground equipment", "placing GPU + chocks" + (_options.CurrentValue.AutoPca ? " + PCA" : ""));
            await _prosim.WriteAsync(ProsimDataRefNames.Chocks, true).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.GroundPower, true).ConfigureAwait(false);
            if (_options.CurrentValue.AutoPca)
            {
                await _prosim.WriteAsync(ProsimDataRefNames.GroundPreconditionedAir, true).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision("ground equipment", $"placement skipped: {ex.Message}");
        }
    }

    private async Task RemoveEquipmentAsync()
    {
        try
        {
            RecordDecision("ground equipment", "beacon on — removing PCA + GPU + chocks");
            await _prosim.WriteAsync(ProsimDataRefNames.GroundPreconditionedAir, false).ConfigureAwait(false);
            await _prosim.WriteAsync(ProsimDataRefNames.GroundPower, false).ConfigureAwait(false);

            // Interlock: never pull the chocks with the park brake off.
            if (_parkBrake.GetValue(0) != 0)
            {
                await _prosim.WriteAsync(ProsimDataRefNames.Chocks, false).ConfigureAwait(false);
            }
            else
            {
                _removedThisSession = false; // chocks still down — retry on the next beacon edge
                RecordDecision("ground equipment", "chocks kept — park brake is not set");
            }
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision("ground equipment", $"removal incomplete: {ex.Message}");
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
