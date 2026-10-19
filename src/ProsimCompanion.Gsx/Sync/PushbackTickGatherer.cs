using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>What the state-12 engine-start gate reads from the flight data (issue #106).</summary>
internal readonly record struct EngineStartEvidence(bool AnyEngineRunning, bool EngineStarting, bool ParkBrakeSet);

/// <summary>
/// Everything one pushback tick read, in one record: the two pure cores' inputs (the hook
/// core's and the beacon sequencer's), the raw tug LVARs for the decision log, the engine
/// evidence for the confirmation gate and the option switches the planner gates on.
/// </summary>
internal sealed record PushbackTickReading(
    GsxAutomationPhase Phase,
    bool AutomationEnabled,
    bool GsxReady,
    bool BeaconSequenceEnabled,
    bool DepartureComplete,
    int VehicleState,
    double PushbackStatus,
    double BypassPin,
    PushbackTickCore.HookInputs HookInputs,
    PushbackSequencer.Inputs SequenceInputs,
    PushbackSequencer.Delays Delays,
    bool ConfirmEngineStartOption,
    EngineStartEvidence? Engines)
{
    /// <summary>The beacon sequence ticks only with the automation and the sequence switched
    /// on and GSX ready.</summary>
    public bool SequenceRuns => AutomationEnabled && BeaconSequenceEnabled && GsxReady;
}

/// <summary>
/// The pushback tick's inputs gatherer: samples the live collaborators once per second
/// (the committed beacon fact, the engine's APU sample, doors, the GSX Pushback and Boarding
/// services, the departure cycle, the final loadsheet, the tug LVARs, the engine data) into
/// <see cref="PushbackTickReading"/>. No effects, no state — the host's latches arrive as a
/// snapshot where a rule needs them. The beacon and the APU are the flight-state engine's
/// (C4, 2026-10-19), not a ProSim subscription of this module's own: the sequencer's steps
/// run on crew-realism delays of seconds, so the engine's 250 ms tick costs it nothing.
/// </summary>
public sealed class PushbackTickGatherer : IDisposable
{
    // GSX names the pushback request "Departure" (issue #40: a locally-invented "Pushback" id
    // made the mirror lookup miss forever and the auto-call never fired — the issue-#31 lesson).
    internal const string PushbackServiceId = GsxServiceIds.Departure;

    private readonly IGsxRemoteApi _api;
    private readonly IGsxServiceLifecycleView _lifecycle;
    private readonly IGsxAutomationPhaseView _automation;
    private readonly IPushbackGroundActions _ground;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly LoadsheetStore _loadsheets;
    private readonly IFlightPhaseSource _flight;
    private readonly IGroundFactsSource _groundFacts;
    private readonly IDataRefSubscription<double> _vehicleState;
    private readonly IDataRefSubscription<double> _pushbackStatus;
    private readonly IDataRefSubscription<double> _bypassPin;

    public PushbackTickGatherer(
        IGsxRemoteApi api,
        IGsxServiceLifecycleView lifecycle,
        IGsxAutomationPhaseView automation,
        IPushbackGroundActions ground,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        LoadsheetStore loadsheets,
        IFlightPhaseSource flight,
        IGroundFactsSource groundFacts)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loadsheets);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(groundFacts);
        _api = api;
        _lifecycle = lifecycle;
        _automation = automation;
        _ground = ground;
        _options = options;
        _loadsheets = loadsheets;
        _flight = flight;
        _groundFacts = groundFacts;

        _vehicleState = simVars.Subscribe(GsxLvarNames.VehiclePushbackState);
        _pushbackStatus = simVars.Subscribe(GsxLvarNames.PushbackStatus);
        _bypassPin = simVars.Subscribe(GsxLvarNames.BypassPin);
    }

    public void Dispose()
    {
        _vehicleState.Dispose();
        _pushbackStatus.Dispose();
        _bypassPin.Dispose();
    }

    /// <summary>Samples every input for one tick.</summary>
    internal PushbackTickReading Read()
    {
        var options = _options.CurrentValue;
        var phase = _automation.Phase;
        var departureStarted = _automation.DepartureStarted;
        var departureComplete = _automation.DepartureComplete;
        var services = _api.Mirror.Services;
        var boarding = services.GetValueOrDefault("Boarding");
        var pushback = services.GetValueOrDefault(PushbackServiceId);
        var pushbackCallable = pushback is { State: GsxServiceState.Callable, CanTrigger: true };
        var pushbackStatus = _pushbackStatus.Value;
        var data = _flight.Snapshot().Data;

        return new PushbackTickReading(
            Phase: phase,
            AutomationEnabled: options.AutomationEnabled,
            GsxReady: _api.Readiness == GsxReadiness.Ready,
            BeaconSequenceEnabled: options.BeaconPushbackSequenceEnabled,
            DepartureComplete: departureComplete,
            VehicleState: (int)_vehicleState.Value,
            PushbackStatus: pushbackStatus,
            BypassPin: _bypassPin.Value,
            HookInputs: new PushbackTickCore.HookInputs(
                DepartureStarted: departureStarted,
                DepartureComplete: departureComplete,
                FinalLoadsheetSent: _loadsheets.Snapshot().Final.Status == LoadsheetSlotStatus.Sent,
                BoardingCompleted: _lifecycle.IsCompleted("Boarding"),
                PushbackStatusRaised: pushbackStatus > 0,
                BoardingRequestedOrActive: boarding is { State: GsxServiceState.Requested or GsxServiceState.Active },
                PushbackCallable: pushbackCallable,
                PushbackPending: _lifecycle.IsPending(PushbackServiceId),
                PushbackAlreadyDone: _lifecycle.IsCompleted(PushbackServiceId),
                CallJetwayStairsDuringDeparture: options.CallJetwayStairsDuringDeparture,
                RemoveStairsAfterDepartureMode: options.RemoveStairsAfterDeparture,
                BeaconSequenceEnabled: options.BeaconPushbackSequenceEnabled,
                CloseDoorsOnFinal: options.CloseDoorsOnFinal,
                RemoveJetwayStairsOnFinal: options.RemoveJetwayStairsOnFinal,
                CallPushbackWhenTugAttachedMode: options.CallPushbackWhenTugAttached),
            SequenceInputs: new PushbackSequencer.Inputs(
                Armed: departureComplete && phase is GsxAutomationPhase.Preparation or GsxAutomationPhase.PushBack,
                BeaconOn: _groundFacts.Facts.BeaconOn,
                ApuRunning: data?.ApuRunning == true,
                AnyDoorOpen: _ground.AnyDoorOpen,
                CallPushback: options.CallPushbackOnBeacon,
                PushbackCallable: pushbackCallable),
            Delays: new PushbackSequencer.Delays(
                options.SeqDoorsCloseDelayMinSec, options.SeqDoorsCloseDelayMaxSec,
                options.SeqJetwayRetractDelayMinSec, options.SeqJetwayRetractDelayMaxSec,
                options.SeqGpuDisconnectDelayMinSec, options.SeqGpuDisconnectDelayMaxSec),
            ConfirmEngineStartOption: options.ConfirmEngineStart,
            Engines: data is null ? null : new EngineStartEvidence(data.AnyEngineRunning, data.EngineStarting, data.ParkBrakeSet));
    }
}
