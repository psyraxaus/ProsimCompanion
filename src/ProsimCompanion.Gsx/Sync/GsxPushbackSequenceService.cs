using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Shell around the pure <see cref="PushbackSequencer"/>: samples live inputs once per second
/// (beacon, APU, doors, GSX Pushback service state), executes the sequencer's actions through
/// the door / jetway / ground-equipment services, and decision-logs every transition. Also
/// monitors the pushback LVARs (<c>VEHICLE_PUSHBACK_STATE</c>, <c>PUSHBACK_STATUS</c>,
/// <c>BYPASS_PIN</c>) so tug progress — including the state-12 "confirm good engine start"
/// gate answered by the question dispatcher — is visible in the decision log.
/// </summary>
public sealed class GsxPushbackSequenceService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const string PushbackServiceId = "Pushback";

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxAutomationService _automation;
    private readonly GsxDoorService _doors;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxPushbackSequenceService> _logger;
    private readonly IDataRefSubscription _beacon;
    private readonly IDataRefSubscription _apuRunning;
    private readonly IDataRefSubscription _vehicleState;
    private readonly IDataRefSubscription _pushbackStatus;
    private readonly IDataRefSubscription _bypassPin;
    private readonly PushbackSequencer _sequencer;
    private readonly Timer _timer;
    private GsxAutomationPhase _lastResetPhase = GsxAutomationPhase.SessionStart;
    private int _lastVehicleState = -1;
    private double _lastBypassPin;
    private int _ticking;

    public GsxPushbackSequenceService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        GsxAutomationService automation,
        GsxDoorService doors,
        GsxJetwayStairsService jetwayStairs,
        GsxGroundEquipmentService groundEquipment,
        IProsimDataRefs prosim,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxPushbackSequenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(jetwayStairs);
        ArgumentNullException.ThrowIfNull(groundEquipment);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _automation = automation;
        _doors = doors;
        _jetwayStairs = jetwayStairs;
        _groundEquipment = groundEquipment;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _beacon = prosim.Subscribe(ProsimDataRefNames.OhExtLtBeacon, DataRefTier.Normal);
        _apuRunning = prosim.Subscribe(ProsimDataRefNames.ApuRunning, DataRefTier.Normal);
        _vehicleState = simVars.Subscribe(GsxLvarNames.VehiclePushbackState, "number", DataRefTier.Normal);
        _pushbackStatus = simVars.Subscribe(GsxLvarNames.PushbackStatus, "number", DataRefTier.Normal);
        _bypassPin = simVars.Subscribe(GsxLvarNames.BypassPin, "number", DataRefTier.Normal);

        _sequencer = new PushbackSequencer(Random.Shared.Next);
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _beacon.Dispose();
        _apuRunning.Dispose();
        _vehicleState.Dispose();
        _pushbackStatus.Dispose();
        _bypassPin.Dispose();
    }

    private bool Enabled =>
        _options.CurrentValue.AutomationEnabled && _options.CurrentValue.BeaconPushbackSequenceEnabled;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            MonitorPushbackLvars();

            var phase = _automation.Phase;

            // A new turnaround (or getting airborne) resets the sequence.
            if (phase is GsxAutomationPhase.Flight or GsxAutomationPhase.Arrival
                && _lastResetPhase != phase)
            {
                _lastResetPhase = phase;
                if (_sequencer.Step != PushbackSequenceStep.Idle)
                {
                    _sequencer.Reset();
                    RecordDecision("pushback sequence", "reset (new flight segment)");
                }
            }

            if (!Enabled || _api.Readiness != GsxReadiness.Ready)
            {
                return;
            }

            var options = _options.CurrentValue;
            var pushback = _api.Mirror.Services.GetValueOrDefault(PushbackServiceId);
            var inputs = new PushbackSequencer.Inputs(
                Armed: _automation.DepartureComplete
                    && phase is GsxAutomationPhase.Preparation or GsxAutomationPhase.PushBack,
                BeaconOn: _beacon.GetValue(0) != 0,
                ApuRunning: _apuRunning.GetValue(false),
                AnyDoorOpen: _doors.AnyDoorOpen,
                CallPushback: options.CallPushbackOnBeacon,
                PushbackCallable: pushback is { State: GsxServiceState.Callable, CanTrigger: true });
            var delays = new PushbackSequencer.Delays(
                options.SeqDoorsCloseDelayMinSec, options.SeqDoorsCloseDelayMaxSec,
                options.SeqJetwayRetractDelayMinSec, options.SeqJetwayRetractDelayMaxSec,
                options.SeqGpuDisconnectDelayMinSec, options.SeqGpuDisconnectDelayMaxSec);

            var result = _sequencer.Tick(inputs, delays);
            if (result.Transition is not null)
            {
                RecordDecision("pushback sequence", result.Transition);
            }
            if (result.Action != PushbackAction.None)
            {
                _ = ExecuteAsync(result.Action);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pushback sequence tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task ExecuteAsync(PushbackAction action)
    {
        try
        {
            switch (action)
            {
                case PushbackAction.CloseDoors:
                    await _doors.CloseAllDoorsAsync().ConfigureAwait(false);
                    break;

                case PushbackAction.RetractJetwayStairs:
                    await _jetwayStairs.RequestRemovalAsync().ConfigureAwait(false);
                    break;

                case PushbackAction.RemoveGroundEquipment:
                    await _groundEquipment.RemoveForDepartureAsync().ConfigureAwait(false);
                    break;

                case PushbackAction.CallPushback:
                    _lifecycle.MarkCalled(PushbackServiceId);
                    var result = await _api.SendCommandAsync(
                        "service.trigger",
                        new JsonObject { ["service"] = PushbackServiceId }).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        RecordDecision("pushback sequence", $"pushback trigger rejected ({result.Code})");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pushback sequence action {Action} failed", action);
        }
    }

    /// <summary>Surfaces raw tug/pin progress in the decision log — this is how the state-12
    /// engine-start-confirmation gate and pin removal become diagnosable from logs alone.</summary>
    private void MonitorPushbackLvars()
    {
        var vehicleState = (int)_vehicleState.GetValue(0.0);
        if (vehicleState != _lastVehicleState)
        {
            if (_lastVehicleState != -1 || vehicleState != 0)
            {
                RecordDecision(
                    "pushback tug",
                    $"vehicle state {vehicleState} ({DescribeVehicleState(vehicleState)}); status={_pushbackStatus.GetValue(0.0):F0}");
            }
            _lastVehicleState = vehicleState;
        }

        var pin = _bypassPin.GetValue(0.0);
        if (Math.Abs(pin - _lastBypassPin) > 0.5)
        {
            _lastBypassPin = pin;
            RecordDecision("pushback tug", pin != 0 ? "bypass pin inserted" : "bypass pin removed");
        }
    }

    private static string DescribeVehicleState(int state) => state switch
    {
        0 => "idle",
        1 => "at gate",
        8 => "pushing back",
        11 => "waiting for engine shutdown",
        12 => "awaiting good-engine-start confirmation",
        13 => "disconnecting",
        14 => "clear to start",
        _ => "unknown",
    };

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
