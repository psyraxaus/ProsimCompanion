using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Shell around the pure <see cref="PushbackSequencer"/>: samples live inputs once per second
/// (beacon, APU, doors, GSX Pushback service state), executes the sequencer's actions through
/// the door / jetway / ground-equipment services, and decision-logs every transition. Also
/// monitors the pushback LVARs (<c>VEHICLE_PUSHBACK_STATE</c>, <c>PUSHBACK_STATUS</c>,
/// <c>BYPASS_PIN</c>) so tug progress is visible in the decision log, and answers the
/// state-12 "confirm good engine start" gate itself (issue #106 — the 2026-08-23 flight
/// left the tug waiting until the pilot opened the GSX menu by hand): once at least one
/// engine is stably running with the park brake set, the "Confirm good engine start" entry
/// on GSX's "Interrupt pushback?" menu is selected, Prosim2GSX-parity.
/// </summary>
public sealed class GsxPushbackSequenceService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // GSX names the pushback request "Departure" (issue #40: a locally-invented "Pushback" id
    // made the mirror lookup miss forever and the auto-call never fired — the issue-#31 lesson).
    private const string PushbackServiceId = GsxServiceIds.Departure;

    private readonly IGsxRemoteApi _api;
    private readonly IGsxTriggerSlot _slot;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxAutomationService _automation;
    private readonly GsxDoorService _doors;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly LoadsheetStore _loadsheets;
    private readonly IFlightPhaseSource _flight;
    private readonly GsxMenuIntentExecutor _menuExecutor;
    private readonly ILogger<GsxPushbackSequenceService> _logger;
    private readonly IDataRefSubscription<int> _beacon;
    private readonly IDataRefSubscription<bool> _apuRunning;
    private readonly IDataRefSubscription<double> _vehicleState;
    private readonly IDataRefSubscription<double> _pushbackStatus;
    private readonly IDataRefSubscription<double> _bypassPin;
    private readonly PushbackSequencer _sequencer;
    private readonly Timer _timer;
    private GsxAutomationPhase _lastResetPhase = GsxAutomationPhase.SessionStart;
    private int _lastVehicleState = -1;
    private double _lastBypassPin;
    private PushbackTickCore.HookState _hookState = PushbackTickCore.HookState.Initial;
    private int _ticking;

    // Good-engine-start confirmation (issue #106): once per push; a failed menu pick
    // retries every few ticks up to the cap, then leaves the menu to the pilot.
    private const int ConfirmRetryGapTicks = 5;
    private const int ConfirmMaxAttempts = 10;
    private bool _engineStartConfirmed;
    private int _confirmAttempts;
    private int _confirmCooldown;
    private int _confirming;

    public GsxPushbackSequenceService(
        IGsxRemoteApi api,
        IGsxTriggerSlot slot,
        GsxServiceLifecycleTracker lifecycle,
        GsxAutomationService automation,
        GsxDoorService doors,
        GsxJetwayStairsService jetwayStairs,
        GsxGroundEquipmentService groundEquipment,
        IProsimDataRefs prosim,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        LoadsheetStore loadsheets,
        IFlightPhaseSource flight,
        GsxMenuIntentExecutor menuExecutor,
        ILogger<GsxPushbackSequenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(loadsheets);
        _loadsheets = loadsheets;
        ArgumentNullException.ThrowIfNull(flight);
        _flight = flight;
        ArgumentNullException.ThrowIfNull(menuExecutor);
        _menuExecutor = menuExecutor;
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(slot);
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
        _slot = slot;
        _lifecycle = lifecycle;
        _automation = automation;
        _doors = doors;
        _jetwayStairs = jetwayStairs;
        _groundEquipment = groundEquipment;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _beacon = prosim.Subscribe(ProsimDataRefNames.OhExtLtBeacon);
        _apuRunning = prosim.Subscribe(ProsimDataRefNames.ApuRunning);
        _vehicleState = simVars.Subscribe(GsxLvarNames.VehiclePushbackState);
        _pushbackStatus = simVars.Subscribe(GsxLvarNames.PushbackStatus);
        _bypassPin = simVars.Subscribe(GsxLvarNames.BypassPin);

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
                _hookState = PushbackTickCore.HookState.Initial;
                _engineStartConfirmed = false;
                _confirmAttempts = 0;
                _confirmCooldown = 0;
                if (_sequencer.Step != PushbackSequenceStep.Idle)
                {
                    _sequencer.Reset();
                    RecordDecision("pushback sequence", "reset (new flight segment)");
                }
            }

            // Tug-attached-during-boarding rule runs independently of the beacon sequence
            // (predecessor semantics — it exists precisely for the early-pushback-call flow).
            // The policy is the pure core (campaign #78); this shell executes its intents.
            if (_options.CurrentValue.AutomationEnabled && _api.Readiness == GsxReadiness.Ready)
            {
                RunHooks();
                TryConfirmEngineStart();
            }

            // Gradual equipment removal belongs to the NON-sequence flow (the beacon sequence
            // owns its own timed removal step) and needs a ticker — this shell provides it.
            if (!_options.CurrentValue.BeaconPushbackSequenceEnabled
                && _options.CurrentValue.AutomationEnabled
                && phase is GsxAutomationPhase.Preparation or GsxAutomationPhase.PushBack)
            {
                _ = _groundEquipment.TickGradualRemovalAsync();
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
                BeaconOn: _beacon.Value != 0,
                ApuRunning: _apuRunning.Value,
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
                    // Retry-once (campaign #77): pushback was the only sender with NO retry —
                    // a silently dropped call was unrecoverable without pilot action. The slot
                    // marks the cycle called on confirmation, and a failure un-latches the tug
                    // path so the next tick re-offers.
                    var dispatch = await _slot
                        .TryDispatchAsync(new GsxTriggerRequest(PushbackServiceId, "pushback sequence")
                        {
                            RetryOnce = true,
                            OnResolved = resolution =>
                            {
                                if (resolution != GsxTriggerResolution.Confirmed)
                                {
                                    _hookState = _hookState with { TugPushbackCalled = false };
                                }
                            },
                        })
                        .ConfigureAwait(false);
                    if (dispatch.Status != GsxTriggerDispatchStatus.Dispatched)
                    {
                        _hookState = _hookState with { TugPushbackCalled = false };
                        RecordDecision(
                            "pushback sequence",
                            dispatch.Status == GsxTriggerDispatchStatus.Busy
                                ? $"pushback call waiting — trigger slot busy with {dispatch.BusyServiceId ?? "another service"}"
                                : $"pushback trigger rejected ({dispatch.RejectCode})");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pushback sequence action {Action} failed", action);
        }
    }

    /// <summary>Feeds the pure hook core (campaign #78) and executes its intents: the
    /// Prosim2GSX departure phase-point hooks (#9) and the tug-attached auto-call, driven by
    /// this shell's 1 Hz tick because they need a ticker and this shell already samples
    /// everything involved.</summary>
    private void RunHooks()
    {
        var options = _options.CurrentValue;
        var boarding = _api.Mirror.Services.GetValueOrDefault("Boarding");
        var pushback = _api.Mirror.Services.GetValueOrDefault(PushbackServiceId);

        var intents = PushbackTickCore.Evaluate(_hookState, new PushbackTickCore.HookInputs(
            DepartureStarted: _automation.DepartureStarted,
            DepartureComplete: _automation.DepartureComplete,
            FinalLoadsheetSent: _loadsheets.Snapshot().Final.Status == LoadsheetSlotStatus.Sent,
            BoardingCompleted: _lifecycle.IsCompleted("Boarding"),
            PushbackStatusRaised: _pushbackStatus.Value > 0,
            BoardingRequestedOrActive: boarding is { State: GsxServiceState.Requested or GsxServiceState.Active },
            PushbackCallable: pushback is { State: GsxServiceState.Callable, CanTrigger: true },
            PushbackPending: _lifecycle.IsPending(PushbackServiceId),
            PushbackAlreadyDone: _lifecycle.IsCompleted(PushbackServiceId),
            CallJetwayStairsDuringDeparture: options.CallJetwayStairsDuringDeparture,
            RemoveStairsAfterDepartureMode: options.RemoveStairsAfterDeparture,
            BeaconSequenceEnabled: options.BeaconPushbackSequenceEnabled,
            CloseDoorsOnFinal: options.CloseDoorsOnFinal,
            RemoveJetwayStairsOnFinal: options.RemoveJetwayStairsOnFinal,
            CallPushbackWhenTugAttachedMode: options.CallPushbackWhenTugAttached));
        _hookState = intents.State;

        if (intents.TugLatched)
        {
            RecordDecision("pushback tug", "tug attached during boarding — pushback auto-call armed");
        }
        if (intents.CallTugPushback)
        {
            RecordDecision(
                "pushback tug",
                $"calling pushback — tug attached and {options.CallPushbackWhenTugAttached} condition met");
            _ = ExecuteAsync(PushbackAction.CallPushback);
        }
        if (intents.RunDepartureJetwayStep)
        {
            _ = _jetwayStairs.RunDepartureStep();
        }
        if (intents.RemoveStairsAfterDeparture)
        {
            _ = _jetwayStairs.RemoveStairsAfterDepartureAsync(options.RemoveStairsAfterDeparture);
        }
        if (intents.CloseDoorsOnFinal)
        {
            RecordDecision("departure", "final loadsheet sent — closing doors");
            _ = _doors.CloseAllDoorsAsync();
        }
        if (intents.RemoveJetwayStairsOnFinal)
        {
            RecordDecision("departure", "final loadsheet sent — removing jetway/stairs");
            _ = _jetwayStairs.RequestRemovalAsync();
        }
    }

    /// <summary>The state-12 gate (issue #106): GSX finishes the physical push and waits for
    /// the good-engine-start confirmation on its "Interrupt pushback?" menu — the 2026-08-23
    /// flight sat there until the pilot answered by hand. Prosim2GSX-parity conditions: park
    /// brake set, an engine stably running (any, not both — single-engine taxi is a real
    /// procedure), none mid-start. Fails safe: an unmatched menu retries a few times, then
    /// the menu stays with the pilot.</summary>
    private void TryConfirmEngineStart()
    {
        if (_engineStartConfirmed || (int)_vehicleState.Value != 12)
        {
            return;
        }

        if (_confirmCooldown > 0)
        {
            _confirmCooldown--;
            return;
        }

        var data = _flight.Snapshot().Data;
        if (data is null || !data.AnyEngineRunning || data.EngineStarting || !data.ParkBrakeSet)
        {
            return;
        }

        if (Interlocked.Exchange(ref _confirming, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _menuExecutor.ExecuteAsync(
                    new GsxMenuIntent
                    {
                        Name = "confirm good engine start",
                        TitlePrefixes = ["Interrupt pushback"],
                        // Prosim2GSX's proven entry pattern — only the engine-start variant
                        // of the interrupt menu matches; other variants fail safe.
                        EntryPattern = new Regex("^confirm good engine", RegexOptions.IgnoreCase),
                    },
                    CancellationToken.None).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    _engineStartConfirmed = true;
                    RecordDecision("pushback tug", "confirmed good engine start — tug clear to disconnect");
                    return;
                }

                _confirmAttempts++;
                if (_confirmAttempts >= ConfirmMaxAttempts)
                {
                    _engineStartConfirmed = true; // give up quietly — the menu stays with the pilot
                    RecordDecision(
                        "pushback tug",
                        $"good-engine-start confirm failed {ConfirmMaxAttempts} times ({result.Outcome}: {result.Detail}) — leaving the menu to the pilot");
                    return;
                }

                _confirmCooldown = ConfirmRetryGapTicks;
                _logger.LogDebug(
                    "Good-engine-start confirm attempt {Attempt} did not land ({Outcome}: {Detail}) — retrying",
                    _confirmAttempts, result.Outcome, result.Detail);
            }
            catch (Exception ex)
            {
                _confirmCooldown = ConfirmRetryGapTicks;
                _logger.LogError(ex, "Good-engine-start confirmation failed");
            }
            finally
            {
                Interlocked.Exchange(ref _confirming, 0);
            }
        });
    }

    /// <summary>Surfaces raw tug/pin progress in the decision log — this is how the state-12
    /// engine-start-confirmation gate and pin removal become diagnosable from logs alone.</summary>
    private void MonitorPushbackLvars()
    {
        var vehicleState = (int)_vehicleState.Value;
        if (vehicleState != _lastVehicleState)
        {
            if (_lastVehicleState != -1 || vehicleState != 0)
            {
                RecordDecision(
                    "pushback tug",
                    $"vehicle state {vehicleState} ({DescribeVehicleState(vehicleState)}); status={_pushbackStatus.Value:F0}");
            }
            _lastVehicleState = vehicleState;
        }

        var pin = _bypassPin.Value;
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
