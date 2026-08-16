namespace ProsimCompanion.Core.State;

/// <summary>
/// The one snapshot-store implementation (campaign #86): an immutable snapshot behind a lock,
/// pure-transform updates, and change notification that fires only when the snapshot actually
/// changed — by record value equality unless the store supplies its own comparer (e.g.
/// <see cref="LlmHealthStore"/> ignores its refreshed-every-report timestamp). The snapshot is
/// always stored, even when equal, so silent refreshes (timestamps) persist without churning
/// subscribers. Observers are invoked on the writer's thread — consumers marshal to their own
/// context (<c>InvokeAsync</c> in Blazor components, see <c>StoreObserverComponent</c>).
/// </summary>
public abstract class SnapshotStore<T>
{
    private readonly object _gate = new();
    private readonly IEqualityComparer<T> _comparer;
    private readonly List<Subscription> _observers = [];
    private T _snapshot;

    protected SnapshotStore(T initial, IEqualityComparer<T>? comparer = null)
    {
        _snapshot = initial;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <summary>The current snapshot.</summary>
    public T Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <summary>Replaces the snapshot via a pure transform of the current one; observers are
    /// notified only when the result is unequal under the store's comparer.</summary>
    public void Update(Func<T, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        T updated;
        Subscription[] observers;
        lock (_gate)
        {
            updated = mutate(_snapshot);
            var changed = !_comparer.Equals(updated, _snapshot);
            _snapshot = updated;
            if (!changed)
            {
                return;
            }

            observers = [.. _observers];
        }

        foreach (var observer in observers)
        {
            observer.Invoke(updated);
        }
    }

    /// <summary>Subscribes to snapshot changes; disposing unsubscribes (idempotent). The new
    /// snapshot is passed to the observer, so no re-read is needed.</summary>
    public IDisposable Observe(Action<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        var subscription = new Subscription(this, observer);
        lock (_gate)
        {
            _observers.Add(subscription);
        }

        return subscription;
    }

    private sealed class Subscription(SnapshotStore<T> owner, Action<T> observer) : IDisposable
    {
        private bool _disposed;

        internal void Invoke(T snapshot) => observer(snapshot);

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner._observers.Remove(this);
            }
        }
    }
}
