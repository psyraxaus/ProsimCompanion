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

    /// <summary>The session gate's first question (CONTEXT.md): true while a flight session
    /// exists at all — aboard or on walkaround — so sim-session LVARs and flight data may be
    /// trusted.</summary>
    public bool DataIsMeaningful => Phase is SimSessionPhase.InSession or SimSessionPhase.Walkaround;

    /// <summary>The session gate's second question (CONTEXT.md): true only with the pilot in
    /// the aircraft. Ground automation (reposition, GSX services) holds during the walkaround
    /// — services must not be driven while the pilot is outside the aircraft — and on Unknown
    /// (a signal we cannot read is not a signal that passed).</summary>
    public bool MayDriveGroundServices => Phase == SimSessionPhase.InSession;
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
    /// caller's thread.</summary>
    public event Action<SimSessionPhase, SimSessionPhase>? PhaseChanged;

    /// <summary>Raised once when a flight session begins — entering
    /// {InSession, Walkaround} from outside (campaign #79: the edge is computed here, never
    /// re-derived by consumers).</summary>
    public event Action? SessionStarted;

    /// <summary>Raised once when the flight session ends — leaving {InSession, Walkaround}
    /// into {NotInSession, Unknown}. Session-end teardown (prep reset, verdict withdrawal,
    /// latch re-arm) hooks off this.</summary>
    public event Action? SessionEnded;

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

            var wasLive = oldPhase is SimSessionPhase.InSession or SimSessionPhase.Walkaround;
            var isLive = snapshot.Phase is SimSessionPhase.InSession or SimSessionPhase.Walkaround;
            if (!wasLive && isLive)
            {
                SessionStarted?.Invoke();
            }
            else if (wasLive && !isLive)
            {
                SessionEnded?.Invoke();
            }
        }
    }

    /// <summary>Opens a session window (CONTEXT.md): a settle period anchored to
    /// session entry, after which time-gated checks may conclude. Re-anchors automatically on
    /// every session start and goes un-elapsed when the session ends — the 90 s tick
    /// arithmetic the startup resync and the aircraft-state check used to hand-roll.</summary>
    public SessionWindow OpenWindow(TimeSpan settle) => new(this, settle);
}

/// <summary>A settle window anchored to sim-session entry (see
/// <see cref="SimSessionStore.OpenWindow"/>). Monotonic clock; thread-safe; dispose to
/// unsubscribe.</summary>
public sealed class SessionWindow : IDisposable
{
    private readonly SimSessionStore _store;
    private readonly TimeSpan _settle;
    private long _anchorTicks = -1;

    internal SessionWindow(SimSessionStore store, TimeSpan settle)
    {
        _store = store;
        _settle = settle;
        _store.SessionStarted += OnSessionStarted;
        _store.SessionEnded += OnSessionEnded;
        if (store.Snapshot().DataIsMeaningful)
        {
            OnSessionStarted();
        }
    }

    /// <summary>True once the session has been live for the whole settle period. False while
    /// no session is live.</summary>
    public bool Elapsed
    {
        get
        {
            var anchor = Interlocked.Read(ref _anchorTicks);
            return anchor >= 0
                && TimeSpan.FromMilliseconds(Environment.TickCount64 - anchor) >= _settle;
        }
    }

    /// <summary>Re-anchors the window to now (an in-session re-arm, e.g. after a verdict is
    /// withdrawn without the session ending).</summary>
    public void Restart() => Interlocked.Exchange(ref _anchorTicks, Environment.TickCount64);

    public void Dispose()
    {
        _store.SessionStarted -= OnSessionStarted;
        _store.SessionEnded -= OnSessionEnded;
    }

    private void OnSessionStarted() => Interlocked.Exchange(ref _anchorTicks, Environment.TickCount64);

    private void OnSessionEnded() => Interlocked.Exchange(ref _anchorTicks, -1);
}
