using Microsoft.AspNetCore.Components;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// <see cref="StoreObserverComponent"/> for layouts — Blazor layouts must derive from
/// <see cref="LayoutComponentBase"/>, so the watch surface is repeated here as thin
/// delegations to the same <see cref="WatchSet"/>.
/// </summary>
public abstract class StoreObserverLayout : LayoutComponentBase, IDisposable
{
    private WatchSet? _watches;

    private WatchSet Watches => _watches ??= new WatchSet(
        action => InvokeAsync(action), StateHasChanged);

    /// <inheritdoc cref="StoreObserverComponent.Watch{T}"/>
    protected void Watch<T>(SnapshotStore<T> store, Action<T> apply) => Watches.Watch(store, apply);

    /// <inheritdoc cref="StoreObserverComponent.WatchChanged(Action{EventHandler}, Action{EventHandler}, Action)"/>
    protected void WatchChanged(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe, Action refresh)
        => Watches.WatchChanged(subscribe, unsubscribe, refresh);

    /// <inheritdoc cref="StoreObserverComponent.WatchChanged(Action{Action}, Action{Action}, Action)"/>
    protected void WatchChanged(Action<Action> subscribe, Action<Action> unsubscribe, Action refresh)
        => Watches.WatchChanged(subscribe, unsubscribe, refresh);

    /// <inheritdoc cref="StoreObserverComponent.WatchChanged{TArgs}"/>
    protected void WatchChanged<TArgs>(
        Action<EventHandler<TArgs>> subscribe, Action<EventHandler<TArgs>> unsubscribe, Action refresh)
        => Watches.WatchChanged(subscribe, unsubscribe, refresh);

    public virtual void Dispose() => _watches?.Dispose();
}
