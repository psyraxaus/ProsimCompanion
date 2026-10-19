using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>An immutable view of the pushback tick's latches, taken once per tick so the
/// effect planner is a pure function of (reading, latches, sequencer result).</summary>
/// <param name="LastResetPhase">The automation phase the sequence was last reset for.</param>
/// <param name="LastVehicleState">Last <c>VEHICLE_PUSHBACK_STATE</c> logged; -1 = never read.</param>
/// <param name="LastBypassPin">Last <c>BYPASS_PIN</c> logged.</param>
/// <param name="HookState">The hook core's per-segment latches (tug attached, stairs removed, …).</param>
/// <param name="EngineStartConfirmed">The good-engine-start confirmation is done (or given up) this push.</param>
/// <param name="ConfirmAttempts">Failed confirmation picks so far.</param>
/// <param name="ConfirmCooldown">Ticks left before the next confirmation attempt.</param>
internal sealed record PushbackLatchSnapshot(
    GsxAutomationPhase LastResetPhase,
    int LastVehicleState,
    double LastBypassPin,
    PushbackTickCore.HookState HookState,
    bool EngineStartConfirmed,
    int ConfirmAttempts,
    int ConfirmCooldown)
{
    internal static PushbackLatchSnapshot Initial { get; } = new(
        LastResetPhase: GsxAutomationPhase.SessionStart,
        LastVehicleState: -1,
        LastBypassPin: 0,
        HookState: PushbackTickCore.HookState.Initial,
        EngineStartConfirmed: false,
        ConfirmAttempts: 0,
        ConfirmCooldown: 0);
}

/// <summary>
/// The pushback tick's latch storage — the one mutable thing the host keeps beside the
/// sequencer. Named single-field mutations so the planner's latch effects and the applier's
/// async confirmation outcome never overwrite each other's fields. Thread-safe.
/// </summary>
public sealed class PushbackTickLatches
{
    private readonly Lock _gate = new();
    private GsxAutomationPhase _lastResetPhase = GsxAutomationPhase.SessionStart;
    private int _lastVehicleState = -1;
    private double _lastBypassPin;
    private PushbackTickCore.HookState _hookState = PushbackTickCore.HookState.Initial;
    private bool _engineStartConfirmed;
    private int _confirmAttempts;
    private int _confirmCooldown;

    internal PushbackLatchSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PushbackLatchSnapshot(
                _lastResetPhase, _lastVehicleState, _lastBypassPin, _hookState,
                _engineStartConfirmed, _confirmAttempts, _confirmCooldown);
        }
    }

    public void SetLastVehicleState(int value)
    {
        lock (_gate)
        {
            _lastVehicleState = value;
        }
    }

    public void SetLastBypassPin(double value)
    {
        lock (_gate)
        {
            _lastBypassPin = value;
        }
    }

    internal void SetHookState(PushbackTickCore.HookState state)
    {
        lock (_gate)
        {
            _hookState = state;
        }
    }

    /// <summary>A pushback call that did not confirm un-latches the tug path so the next tick
    /// re-offers (campaign #77 retry-once semantics).</summary>
    public void ClearTugPushbackCalled()
    {
        lock (_gate)
        {
            _hookState = _hookState with { TugPushbackCalled = false };
        }
    }

    /// <summary>A new flight segment (getting airborne, or a new turnaround): the per-push
    /// latches start over.</summary>
    public void ResetForSegment(GsxAutomationPhase phase)
    {
        lock (_gate)
        {
            _lastResetPhase = phase;
            _hookState = PushbackTickCore.HookState.Initial;
            _engineStartConfirmed = false;
            _confirmAttempts = 0;
            _confirmCooldown = 0;
        }
    }

    public void MarkEngineStartConfirmed()
    {
        lock (_gate)
        {
            _engineStartConfirmed = true;
        }
    }

    /// <summary>Counts one failed confirmation pick and returns the new total.</summary>
    public int CountConfirmAttempt()
    {
        lock (_gate)
        {
            return ++_confirmAttempts;
        }
    }

    public void SetConfirmCooldown(int ticks)
    {
        lock (_gate)
        {
            _confirmCooldown = ticks;
        }
    }

    public void DecrementConfirmCooldown()
    {
        lock (_gate)
        {
            if (_confirmCooldown > 0)
            {
                _confirmCooldown--;
            }
        }
    }
}
