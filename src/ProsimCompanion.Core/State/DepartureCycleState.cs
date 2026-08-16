namespace ProsimCompanion.Core.State;

/// <summary>
/// The departure cycle (CONTEXT.md): one turnaround's departure side, owned by no single
/// feature. The GSX automation and the ground-prep coordinator each need the other's flags —
/// this store is the shared owner, so neither references the other (campaign #78; replaces
/// the <c>Lazy&lt;IGsxDepartureControl&gt;</c> constructor-cycle workaround and the
/// four-way turnaround duplication). Thread-safe; events fire on the mutating thread.
/// </summary>
public sealed class DepartureCycleState
{
    private readonly Lock _gate = new();
    private bool _started;
    private bool _complete;
    private bool _isTurnaround;
    private bool _prepComplete;

    /// <summary>True once the departure service sequence has been started (voice, web, API
    /// or auto-start) this cycle.</summary>
    public bool Started
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <summary>True once every departure service completed or was skipped — the
    /// beacon-orchestrated pushback sequence arms on this.</summary>
    public bool Complete
    {
        get
        {
            lock (_gate)
            {
                return _complete;
            }
        }
    }

    /// <summary>True when this cycle is a turnaround continuation (an in-session arrival, or
    /// recovered from tracking LVARs by the startup resync) rather than a fresh origin.</summary>
    public bool IsTurnaround
    {
        get
        {
            lock (_gate)
            {
                return _isTurnaround;
            }
        }
    }

    /// <summary>True once the ground-preparation chain (reposition → GPU/chocks →
    /// jetway/stairs) has run for this gate session — departure services hold on it.</summary>
    public bool PrepComplete
    {
        get
        {
            lock (_gate)
            {
                return _prepComplete;
            }
        }
    }

    /// <summary>Raised after any flag actually changes (never on a no-op write).</summary>
    public event Action? Changed;

    /// <summary>Raised when a new cycle begins at the arrival boundary, after the flags have
    /// been reset. Feature-local latches keyed to "this departure" reset on it.</summary>
    public event Action? CycleReset;

    /// <summary>Starts the departure sequence (idempotent). Clears a stale
    /// <see cref="Complete"/> so a restart mid-cycle re-runs the sequence.</summary>
    public void MarkStarted() => Mutate(
        static s =>
        {
            s._started = true;
            s._complete = false;
        },
        when: static s => !s._started);

    /// <summary>All departure services completed or skipped.</summary>
    public void MarkComplete() => Mutate(static s => s._complete = true, when: static s => !s._complete);

    /// <summary>This cycle is a turnaround (sticky until process end — a session that saw one
    /// arrival never returns to first-leg service constraints).</summary>
    public void MarkTurnaround() => Mutate(static s => s._isTurnaround = true, when: static s => !s._isTurnaround);

    /// <summary>The ground-prep chain finished (or was seeded finished by the startup resync).</summary>
    public void MarkPrepComplete() => Mutate(static s => s._prepComplete = true, when: static s => !s._prepComplete);

    /// <summary>The ground-prep chain restarted (gate change, Couatl restart, session end).</summary>
    public void ResetPrep() => Mutate(static s => s._prepComplete = false, when: static s => s._prepComplete);

    /// <summary>Arrival boundary: fresh departure sequence, fresh prep, turnaround latched.
    /// Fires <see cref="Changed"/> then <see cref="CycleReset"/>.</summary>
    public void BeginTurnaroundCycle()
    {
        lock (_gate)
        {
            _started = false;
            _complete = false;
            _prepComplete = false;
            _isTurnaround = true;
        }
        Changed?.Invoke();
        CycleReset?.Invoke();
    }

    private void Mutate(Action<DepartureCycleState> apply, Func<DepartureCycleState, bool> when)
    {
        lock (_gate)
        {
            if (!when(this))
            {
                return;
            }
            apply(this);
        }
        Changed?.Invoke();
    }
}
