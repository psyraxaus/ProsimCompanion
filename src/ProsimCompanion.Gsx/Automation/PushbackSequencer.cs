namespace ProsimCompanion.Gsx.Automation;

/// <summary>Steps of the beacon-orchestrated departure sequence (the predecessor's proven
/// order): beacon on → wait for APU → close doors → retract jetway/stairs → clear ground
/// equipment → call pushback.</summary>
public enum PushbackSequenceStep
{
    Idle,
    WaitingForApu,
    DoorsStep,
    JetwayStep,
    GpuStep,
    ReadyForPush,
    PushbackCalled,
}

/// <summary>What the shell should do this tick.</summary>
public enum PushbackAction
{
    None,
    CloseDoors,
    RetractJetwayStairs,
    RemoveGroundEquipment,
    CallPushback,
}

/// <summary>One tick's outcome: the action to execute (at most one per tick) and an optional
/// human-readable transition to decision-log.</summary>
public readonly record struct PushbackTickResult(PushbackAction Action, string? Transition);

/// <summary>
/// Pure, timer-free core of the beacon-orchestrated pushback sequence — the shell calls
/// <see cref="Tick"/> once per second with live inputs and executes the returned action.
/// Beacon off (or APU stopping) pauses mid-sequence exactly like the predecessor; each
/// delayed step gets a randomized crew-realism delay from the injected picker.
/// </summary>
public sealed class PushbackSequencer
{
    private readonly Func<int, int, int> _pickDelaySeconds;
    private int _remainingTicks;
    private bool _readyAnnounced;

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
    public readonly record struct Inputs(
        bool Armed,
        bool BeaconOn,
        bool ApuRunning,
        bool AnyDoorOpen,
        bool CallPushback,
        bool PushbackCallable);

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
                if (inputs.PushbackCallable)
                {
                    Step = PushbackSequenceStep.PushbackCalled;
                    return new(PushbackAction.CallPushback, "calling pushback");
                }
                return default;

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
