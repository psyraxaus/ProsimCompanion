namespace ProsimCompanion.Core.State;

/// <summary>Where the pilot is relative to the MSFS flight session. Derived from the camera
/// state, the SimConnect "Sim"/"Pause_EX1" system events and (MSFS 2024) the avatar SimVar —
/// never from ProSim, which pushes plausible cold-and-dark data while MSFS is still on the
/// main menu.</summary>
public enum SimSessionPhase
{
    /// <summary>No SimConnect connection, or no camera data yet — nothing can be concluded.
    /// Session-gated automation must hold in this state (a signal we cannot read is not a
    /// signal that passed).</summary>
    Unknown,

    /// <summary>MSFS is up but the pilot is not in a flight: main menu, world map, loading
    /// screen, or the paused "Ready to Fly" hold.</summary>
    NotInSession,

    /// <summary>MSFS 2024 walkaround/avatar mode: the session is live but the pilot is
    /// outside the aircraft — ground automation still holds (predecessor parity: services
    /// must not be driven at an avatar).</summary>
    Walkaround,

    /// <summary>The pilot is in the aircraft in a running flight session.</summary>
    InSession,
}

/// <summary>Point-in-time view of the MSFS session signals.</summary>
public sealed record SimSessionSnapshot(
    SimSessionPhase Phase,
    bool SimRunning,
    bool Paused,
    int? CameraState,
    string? SimVersion)
{
    public static SimSessionSnapshot Empty { get; } =
        new(SimSessionPhase.Unknown, SimRunning: false, Paused: true, CameraState: null, SimVersion: null);

    /// <summary>True while a flight session exists at all (aboard or on walkaround) — the
    /// window in which sim-session LVARs and flight data are meaningful.</summary>
    public bool InSession => Phase is SimSessionPhase.InSession or SimSessionPhase.Walkaround;
}

/// <summary>
/// Observable store of the MSFS session state, written by the Sim pillar's session monitor and
/// read by session-gated automation (GSX ground prep) and the web UI. Follows the
/// <see cref="ConnectionStatusStore"/> pattern: events fire on the writer's thread, consumers
/// marshal to their own context. Without the Sim pillar the store stays at
/// <see cref="SimSessionSnapshot.Empty"/> (phase <see cref="SimSessionPhase.Unknown"/>), which
/// consumers treat as "hold" — degrade, not fail, but never teleport the aircraft on a signal
/// nobody is producing.
/// </summary>
public sealed class SimSessionStore
{
    private readonly object _gate = new();
    private SimSessionSnapshot _snapshot = SimSessionSnapshot.Empty;

    /// <summary>Raised after any snapshot change, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="SimSessionPhase"/> transitions, with (old, new), on the
    /// caller's thread. Session-end teardown hooks off this.</summary>
    public event Action<SimSessionPhase, SimSessionPhase>? PhaseChanged;

    public SimSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public SimSessionPhase Phase => Snapshot().Phase;

    public void Publish(SimSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        SimSessionPhase oldPhase;
        lock (_gate)
        {
            if (snapshot == _snapshot)
            {
                return;
            }

            oldPhase = _snapshot.Phase;
            _snapshot = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (oldPhase != snapshot.Phase)
        {
            PhaseChanged?.Invoke(oldPhase, snapshot.Phase);
        }
    }
}
