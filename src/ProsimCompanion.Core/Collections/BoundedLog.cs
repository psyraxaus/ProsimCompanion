namespace ProsimCompanion.Core.Collections;

/// <summary>
/// Bounded newest-first ring log (campaign #86) — the mechanics that were hand-copied between
/// <c>LogBufferStore</c> and the diagnostics store's command/decision rings, written once.
/// Thread-safe; <see cref="Snapshot"/> returns a newest-first copy.
/// </summary>
public sealed class BoundedLog<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items;

    public BoundedLog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    public int Capacity { get; }

    /// <summary>Appends an item, evicting the oldest beyond <see cref="Capacity"/>.</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            _items.Enqueue(item);
            while (_items.Count > Capacity)
            {
                _ = _items.Dequeue();
            }
        }
    }

    /// <summary>Newest-first copy of the retained items.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var items = _items.ToArray();
            Array.Reverse(items);
            return items;
        }
    }
}
