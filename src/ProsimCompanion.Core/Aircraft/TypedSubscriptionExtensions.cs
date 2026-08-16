namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The typed subscribe path — the only way normal feature code opens a subscription.
/// Implemented as extensions over the narrow string-based seams so every implementation
/// (ProSim table, SimConnect table, test fakes) gets the identical typed behaviour from
/// one adapter instead of re-implementing coercion policy per transport.
/// </summary>
public static class TypedSubscriptionExtensions
{
    /// <summary>Subscribes to a catalog-declared ProSim dataref and returns a typed handle.</summary>
    public static IDataRefSubscription<T> Subscribe<T>(this IProsimDataRefs dataRefs, DataRef<T> dataRef)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        return new TypedDataRefSubscription<T>(dataRefs.SubscribeDynamic(dataRef.Name, dataRef.Tier), dataRef.Fallback);
    }

    /// <summary>Subscribes to a catalog-declared SimVar/LVAR and returns a typed handle.</summary>
    public static IDataRefSubscription<T> Subscribe<T>(this ISimVars simVars, SimVarRef<T> simVar)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        return new TypedDataRefSubscription<T>(simVars.SubscribeDynamic(simVar.Name, simVar.Unit, simVar.Tier), simVar.Fallback);
    }

    /// <summary>Writes a value through a typed descriptor (same allow-list gate as the string path).</summary>
    public static Task WriteAsync<T>(this IProsimDataRefs dataRefs, DataRef<T> dataRef, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        return dataRefs.WriteAsync(dataRef.Name, value, cancellationToken);
    }

    /// <summary>
    /// Decorates an untyped handle with descriptor fallback policy. Pure delegation — the
    /// inner handle keeps sole ownership of caching, staleness and events, so disposing the
    /// wrapper releases the one shared registration exactly as before.
    /// </summary>
    private sealed class TypedDataRefSubscription<T>(IDataRefSubscription inner, T fallback) : IDataRefSubscription<T>
    {
        public string Name => inner.Name;

        public object? RawValue => inner.RawValue;

        public bool IsStale => inner.IsStale;

        public DateTimeOffset? LastUpdatedUtc => inner.LastUpdatedUtc;

        public event EventHandler? ValueChanged
        {
            add => inner.ValueChanged += value;
            remove => inner.ValueChanged -= value;
        }

        public T Value => inner.GetValue(fallback);

        public T GetValueOr(T overrideFallback) => inner.GetValue(overrideFallback);

        public TValue GetValue<TValue>(TValue fallback) => inner.GetValue(fallback);

        public void Dispose() => inner.Dispose();
    }
}
