using ProsimCompanion.Core.Radios;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>Where the beacon-orchestrated pushback sequence stands.</summary>
public enum PushbackSequenceStep
{
    Idle,
    WaitingForApu,
    DoorsStep,
    JetwayStep,
    GpuStep,
    ReadyForPush,

    /// <summary>Everything is ready; the FO has asked ATC for push and start and the sequence
    /// waits for the approval (or its timeout) before it calls the GSX pushback (2026-11-07).</summary>
    AwaitingPushClearance,
    PushbackCalled,
}

/// <summary>What the shell should do this tick.</summary>
public enum PushbackAction
{
    None,
    CloseDoors,
    RetractJetwayStairs,
    RemoveGroundEquipment,

    /// <summary>Transmit "request push and start" to ATC (the FO on COM1).</summary>
    RequestPushClearance,
    CallPushback,
}

public readonly record struct PushbackTickResult(PushbackAction Action, string? Transition);

/// <summary>
/// Pure, timer-free core of the beacon-orchestrated pushback sequence — the shell calls
/// <see cref="Tick"/> once per second with live inputs and executes the returned action.
/// Beacon off (or APU stopping) pauses mid-sequence exactly like the predecessor; each
/// delayed step gets a randomized crew-realism delay from the injected picker.
/// <para>Since 2026-11-07 (owner request) the sequence asks ATC for push and start before it
/// calls the GSX pushback when the radio is there: ready-for-push → request → approval (or
/// the timeout) → pushback. A clearance the captain already obtained by voice is honoured
/// (state Approved on arrival skips the request); a hold ("expect a five minute delay") is
/// waited through; a silent ATC never blocks the push past the timeout.</para>
/// </summary>
public sealed class PushbackSequencer
{
    private readonly Func<int, int, int> _pickDelaySeconds;
    private int _remainingTicks;
    private bool _readyAnnounced;
    private bool _waitingAnnounced;
    private bool _holdAnnounced;
    private bool _clearanceSettled;

    /// <param name="pickDelaySeconds">Returns a delay in seconds within [min, max] — inject a
    /// seeded picker in tests.</param>
    public PushbackSequencer(Func<int, int, int> pickDelaySeconds)
    {
        ArgumentNullException.ThrowIfNull(pickDelaySeconds);
        _pickDelaySeconds = pickDelaySeconds;
    }

    public PushbackSequenceStep Step { get; private set; } = PushbackSequenceStep.Idle;

    /// <summary>Live conditions sampled by the shell each tick.</summary>
    /// <param name="Armed">Departure services complete and the aircraft is in a ground phase —
    /// the sequence never starts otherwise.</param>
    /// <param name="BeaconOn">Beacon light switch — off pauses the sequence.</param>
    /// <param name="ApuRunning">APU running gate — the hard block before doors close.</param>
    /// <param name="AnyDoorOpen">Any entry/cargo door open in ProSim.</param>
    /// <param name="CallPushback">Option: call the GSX Pushback service at ready-for-push.</param>
    /// <param name="PushbackCallable">The GSX Pushback service exists and is callable.</param>
    /// <param name="RequestClearance">Option on AND the push-clearance radio is available —
    /// ask ATC before the pushback call.</param>
    /// <param name="Clearance">The push clearance as the radio knows it this cycle.</param>
    /// <param name="ClearanceWaitSec">How long to wait for ATC's approval before pushing anyway.</param>
    public readonly record struct Inputs(
        bool Armed,
        bool BeaconOn,
        bool ApuRunning,
        bool AnyDoorOpen,
        bool CallPushback,
        bool PushbackCallable,
        bool RequestClearance = false,
        PushClearanceState Clearance = PushClearanceState.None,
        int ClearanceWaitSec = 120);

    /// <summary>Delay bounds for the three timed steps, in seconds.</summary>
    public readonly record struct Delays(
        int DoorsMin, int DoorsMax,
        int JetwayMin, int JetwayMax,
        int GpuMin, int GpuMax);

    public PushbackTickResult Tick(Inputs inputs, Delays delays)
    {
        switch (Step)
        {
            case PushbackSequenceStep.Idle:
                if (inputs.Armed && inputs.BeaconOn)
                {
                    Step = PushbackSequenceStep.WaitingForApu;
                    return new(PushbackAction.None, "beacon on — waiting for APU");
                }
                return default;

            case PushbackSequenceStep.WaitingForApu:
                if (!inputs.BeaconOn)
                {
                    return default; // pause
                }
                if (inputs.ApuRunning)
                {
                    var delay = StartDelay(delays.DoorsMin, delays.DoorsMax);
                    return new(PushbackAction.None, $"APU running — closing doors in {delay}s");
                }
                return default;

            case PushbackSequenceStep.DoorsStep:
                if (!CanAdvance(inputs))
                {
                    return default;
                }
                if (--_remainingTicks > 0)
                {
                    return default;
                }
                {
                    var delay = StartDelay(delays.JetwayMin, delays.JetwayMax, PushbackSequenceStep.JetwayStep);
                    return inputs.AnyDoorOpen
                        ? new(PushbackAction.CloseDoors, $"closing doors; jetway/stairs retract in {delay}s")
                        : new(PushbackAction.None, $"doors already closed; jetway/stairs retract in {delay}s");
                }

            case PushbackSequenceStep.JetwayStep:
                if (!CanAdvance(inputs))
                {
                    return default;
                }
                if (--_remainingTicks > 0)
                {
                    return default;
                }
                {
                    var delay = StartDelay(delays.GpuMin, delays.GpuMax, PushbackSequenceStep.GpuStep);
                    return new(PushbackAction.RetractJetwayStairs, $"retracting jetway/stairs; ground equipment clears in {delay}s");
                }

            case PushbackSequenceStep.GpuStep:
                if (!CanAdvance(inputs))
                {
                    return default;
                }
                if (--_remainingTicks > 0)
                {
                    return default;
                }
                Step = PushbackSequenceStep.ReadyForPush;
                return new(PushbackAction.RemoveGroundEquipment, "clearing ground equipment — ready for push");

            case PushbackSequenceStep.ReadyForPush:
                if (!inputs.BeaconOn)
                {
                    return default; // pause until the beacon comes back
                }
                if (!inputs.CallPushback)
                {
                    if (_readyAnnounced)
                    {
                        return default;
                    }
                    _readyAnnounced = true;
                    return new(PushbackAction.None, "ready for push — pushback call left to the pilot (gsx.callPushbackOnBeacon off)");
                }
                if (inputs.RequestClearance && !_clearanceSettled)
                {
                    // ATC first (owner request 2026-11-07). An approval already in hand — the
                    // captain asked by voice — is honoured without a second call.
                    if (inputs.Clearance == PushClearanceState.Approved)
                    {
                        _clearanceSettled = true;
                        return new(PushbackAction.None, "push and start already approved by ATC — calling pushback");
                    }

                    Step = PushbackSequenceStep.AwaitingPushClearance;
                    _remainingTicks = Math.Max(15, inputs.ClearanceWaitSec);
                    return inputs.Clearance == PushClearanceState.Requested
                        ? new(PushbackAction.None, $"push and start already requested by the pilot — waiting up to {_remainingTicks}s for ATC")
                        : new(PushbackAction.RequestPushClearance, $"ready for push — requesting push and start from ATC (waiting up to {_remainingTicks}s)");
                }
                if (inputs.PushbackCallable)
                {
                    Step = PushbackSequenceStep.PushbackCalled;
                    return new(PushbackAction.CallPushback, "calling pushback");
                }
                if (!_waitingAnnounced)
                {
                    // Announced once so a service-id/state mismatch shows up in the decision log
                    // instead of stalling silently (issue #40: 3m46s of invisible waiting).
                    _waitingAnnounced = true;
                    return new(PushbackAction.None,
                        "ready for push — waiting for the GSX Departure service to become callable");
                }
                return default;

            case PushbackSequenceStep.AwaitingPushClearance:
                if (!inputs.BeaconOn)
                {
                    return default; // pause; the clearance keeps
                }
                if (inputs.Clearance == PushClearanceState.Approved)
                {
                    _clearanceSettled = true;
                    Step = PushbackSequenceStep.ReadyForPush;
                    return new(PushbackAction.None, "ATC approved push and start — calling pushback");
                }
                if (inputs.Clearance == PushClearanceState.Held && !_holdAnnounced)
                {
                    _holdAnnounced = true;
                    return new(PushbackAction.None, "ATC holds the push — waiting for the approval");
                }
                if (--_remainingTicks > 0)
                {
                    return default;
                }
                _clearanceSettled = true;
                Step = PushbackSequenceStep.ReadyForPush;
                return new(PushbackAction.None, "no push approval from ATC in time — calling pushback anyway");

            default: // PushbackCalled — GSX's own flow owns it from here
                return default;
        }
    }

    /// <summary>Back to Idle (new turnaround).</summary>
    public void Reset()
    {
        Step = PushbackSequenceStep.Idle;
        _remainingTicks = 0;
        _readyAnnounced = false;
        _waitingAnnounced = false;
        _holdAnnounced = false;
        _clearanceSettled = false;
    }

    private static bool CanAdvance(Inputs inputs) => inputs.BeaconOn && inputs.ApuRunning;

    private int StartDelay(int minSec, int maxSec, PushbackSequenceStep? next = null)
    {
        Step = next ?? Step switch
        {
            PushbackSequenceStep.WaitingForApu => PushbackSequenceStep.DoorsStep,
            _ => Step,
        };
        var lo = Math.Max(1, minSec);
        var hi = Math.Max(lo, maxSec);
        var delay = _pickDelaySeconds(lo, hi);
        _remainingTicks = Math.Max(1, delay);
        return delay;
    }
}
