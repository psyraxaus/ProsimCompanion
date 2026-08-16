using Microsoft.AspNetCore.Components;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Base for components that render store state (campaign #86): a component declares what it
/// watches in <c>OnInitialized</c> and this base owns the lifecycle — initial read, circuit
/// marshalling (store events fire on the writer's thread), re-render, and unsubscription on
/// dispose. No page wires subscribe/marshal/dispose by hand. Layouts use
/// <see cref="StoreObserverLayout"/>; the shared mechanics live in <see cref="WatchSet"/>.
/// </summary>
public abstract class StoreObserverComponent : ComponentBase, IDisposable
{
    private WatchSet? _watches;

    private WatchSet Watches => _watches ??= new WatchSet(
        action => InvokeAsync(action), StateHasChanged);

    /// <summary>Watches a snapshot store: <paramref name="apply"/> runs immediately with the
    /// current snapshot, then inside the circuit (followed by a re-render) on every change.</summary>
    protected void Watch<T>(SnapshotStore<T> store, Action<T> apply) => Watches.Watch(store, apply);

    /// <summary>Watches an <see cref="EventHandler"/>-based change source (the stores not on
    /// <see cref="SnapshotStore{T}"/>): <paramref name="refresh"/> runs immediately, then
    /// inside the circuit on every event.</summary>
    protected void WatchChanged(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe, Action refresh)
        => Watches.WatchChanged(subscribe, unsubscribe, refresh);

    /// <summary>Watches an <see cref="Action"/>-based change source (e.g. the tech log).</summary>
    protected void WatchChanged(Action<Action> subscribe, Action<Action> unsubscribe, Action refresh)
        => Watches.WatchChanged(subscribe, unsubscribe, refresh);

    /// <summary>Watches an <see cref="EventHandler{TArgs}"/>-based source (e.g. the flight
    /// phase source). The event args are not passed on — pages re-read the source's snapshot
    /// in <paramref name="refresh"/>, same as every other watch.</summary>
    protected void WatchChanged<TArgs>(
        Action<EventHandler<TArgs>> subscribe, Action<EventHandler<TArgs>> unsubscribe, Action refresh)
        => Watches.WatchChanged(subscribe, unsubscribe, refresh);

    public virtual void Dispose() => _watches?.Dispose();
}
