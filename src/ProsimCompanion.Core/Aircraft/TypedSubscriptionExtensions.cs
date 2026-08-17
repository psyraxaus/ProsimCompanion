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
    /// Decorates an untyped handle with descriptor fallback policy. The inner handle keeps
    /// sole ownership of caching and staleness; disposing the wrapper releases the one shared
    /// registration exactly as before. <see cref="ValueChanged"/> is re-raised (not delegated)
    /// so the event's sender is THIS typed handle — the handle the subscriber was given.
    /// Forwarding the accessor to the inner event leaked the untyped table subscription as
    /// sender, and any handler casting sender to <see cref="IDataRefSubscription{T}"/> threw
    /// on every push (issue #91 — the INT/RAD service-skip was dead in the field).
    /// </summary>
    private sealed class TypedDataRefSubscription<T> : IDataRefSubscription<T>
    {
        private readonly IDataRefSubscription _inner;
        private readonly T _fallback;

        public TypedDataRefSubscription(IDataRefSubscription inner, T fallback)
        {
            _inner = inner;
            _fallback = fallback;
            _inner.ValueChanged += OnInnerValueChanged;
        }

        public string Name => _inner.Name;

        public object? RawValue => _inner.RawValue;

        public bool IsStale => _inner.IsStale;

        public DateTimeOffset? LastUpdatedUtc => _inner.LastUpdatedUtc;

        public event EventHandler? ValueChanged;

        public T Value => _inner.GetValue(_fallback);

        public T GetValueOr(T overrideFallback) => _inner.GetValue(overrideFallback);

        public TValue GetValue<TValue>(TValue fallback) => _inner.GetValue(fallback);

        public void Dispose()
        {
            _inner.ValueChanged -= OnInnerValueChanged;
            _inner.Dispose();
        }

        private void OnInnerValueChanged(object? sender, EventArgs e)
            => ValueChanged?.Invoke(this, e);
    }
}
