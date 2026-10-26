namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// The RECENT list: a ring of the last <see cref="Capacity"/> diagnostic events, newest first
/// when read. Collectors push through the <see cref="DiagEventSink"/> the page builder exposes
/// and only on a state transition (see <see cref="StateTransitions{TKey}"/>), so this is a
/// change log, not a heartbeat. Thread-safe: collectors run on the snapshot service's thread,
/// the bundle builder and the API read it from request threads.
/// </summary>
public sealed class DiagEventLog
{
    /// <summary>How many events are kept.</summary>
    public const int Capacity = 50;

    private readonly object _gate = new();
    private readonly Queue<DiagEvent> _ring = new(Capacity);

    /// <summary>Appends an event, dropping the oldest when the ring is full.</summary>
    public void Push(DiagEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            if (_ring.Count == Capacity)
            {
                _ring.Dequeue();
            }

            _ring.Enqueue(evt);
        }
    }

    /// <summary>The events, newest first.</summary>
    public IReadOnlyList<DiagEvent> Snapshot()
    {
        lock (_gate)
        {
            var items = _ring.ToArray();
            Array.Reverse(items);
            return items;
        }
    }

    /// <summary>Count of events held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ring.Count;
            }
        }
    }
}

/// <summary>What a collector pushes RECENT entries through: the log plus the tick's clock, so
/// every event of one tick carries the same stamp.</summary>
public sealed class DiagEventSink
{
    private readonly DiagEventLog _log;
    private readonly DateTimeOffset _now;

    public DiagEventSink(DiagEventLog log, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _now = now;
    }

    /// <summary>The tick's timestamp.</summary>
    public DateTimeOffset Now => _now;

    /// <summary>Records one line ("STT OK 412 MS"), green unless the state says otherwise.</summary>
    public void Push(string text, DiagState state = DiagState.Normal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _log.Push(new DiagEvent(_now, text, state));
    }
}

/// <summary>
/// Remembers the previous <see cref="DiagState"/> per key and answers which keys changed this
/// tick, so a collector emits a RECENT entry on the edge and never on every tick. One instance
/// per collector (collectors are singletons and run on one thread, so no locking).
/// </summary>
public sealed class StateTransitions<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, DiagState> _previous = [];

    /// <summary>Compares the current states with the remembered ones, remembers the current
    /// ones, and returns the keys whose state changed. A key seen for the first time counts
    /// as changed only when its state is abnormal (a fresh Normal is not news).</summary>
    public IReadOnlyList<(TKey Id, DiagState From, DiagState To)> Observe(IEnumerable<KeyValuePair<TKey, DiagState>> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var changed = new List<(TKey, DiagState, DiagState)>();
        foreach (var (id, state) in current)
        {
            if (_previous.TryGetValue(id, out var before))
            {
                if (before != state)
                {
                    changed.Add((id, before, state));
                }
            }
            else if (state.IsAbnormal())
            {
                changed.Add((id, DiagState.Normal, state));
            }

            _previous[id] = state;
        }

        return changed;
    }

    /// <summary>Single-key convenience: true when the key's state differs from last time.</summary>
    public bool Changed(TKey id, DiagState state, out DiagState previous)
    {
        var had = _previous.TryGetValue(id, out previous);
        _previous[id] = state;
        if (!had)
        {
            previous = DiagState.Normal;
            return state.IsAbnormal();
        }

        return previous != state;
    }
}
