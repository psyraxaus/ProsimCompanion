using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// The store-watching mechanics behind <see cref="StoreObserverComponent"/> and
/// <see cref="StoreObserverLayout"/> (campaign #86): initial read, circuit marshalling (store
/// events fire on the writer's thread), re-render, and unsubscription on dispose. Split out
/// because layouts and components need different Blazor base classes but identical watching.
/// </summary>
public sealed class WatchSet(Func<Action, Task> invokeAsync, Action stateHasChanged) : IDisposable
{
    private readonly List<IDisposable> _watches = [];

    public void Watch<T>(SnapshotStore<T> store, Action<T> apply)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(apply);

        apply(store.Snapshot());
        _watches.Add(store.Observe(snapshot => _ = invokeAsync(() =>
        {
            apply(snapshot);
            stateHasChanged();
        })));
    }

    public void WatchChanged(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        ArgumentNullException.ThrowIfNull(refresh);

        refresh();
        EventHandler handler = (_, _) => _ = invokeAsync(() =>
        {
            refresh();
            stateHasChanged();
        });
        subscribe(handler);
        _watches.Add(new Unsubscriber(() => unsubscribe(handler)));
    }

    public void WatchChanged(Action<Action> subscribe, Action<Action> unsubscribe, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        ArgumentNullException.ThrowIfNull(refresh);

        refresh();
        Action handler = () => _ = invokeAsync(() =>
        {
            refresh();
            stateHasChanged();
        });
        subscribe(handler);
        _watches.Add(new Unsubscriber(() => unsubscribe(handler)));
    }

    public void WatchChanged<TArgs>(
        Action<EventHandler<TArgs>> subscribe, Action<EventHandler<TArgs>> unsubscribe, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        ArgumentNullException.ThrowIfNull(refresh);

        refresh();
        EventHandler<TArgs> handler = (_, _) => _ = invokeAsync(() =>
        {
            refresh();
            stateHasChanged();
        });
        subscribe(handler);
        _watches.Add(new Unsubscriber(() => unsubscribe(handler)));
    }

    public void Dispose()
    {
        for (var i = _watches.Count - 1; i >= 0; i--)
        {
            _watches[i].Dispose();
        }

        _watches.Clear();
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            unsubscribe();
        }
    }
}
