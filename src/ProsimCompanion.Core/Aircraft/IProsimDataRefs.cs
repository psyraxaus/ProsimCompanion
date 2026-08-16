namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The single seam for ProSim aircraft data. Reads are push-subscribed once and served from a
/// local cache (never a per-read network round-trip); writes are gated by a code-level allow-list.
/// Implemented by ProsimCompanion.Prosim; consumed by every feature pillar.
/// </summary>
public interface IProsimDataRefs
{
    /// <summary>
    /// Subscribes to a dataref by raw wire name — the escape hatch for names that only
    /// exist at runtime (user-authored checklists, abnormals, aircraft-state files,
    /// briefing settings). Compile-time refs must go through the typed
    /// <see cref="TypedSubscriptionExtensions.Subscribe{T}(IProsimDataRefs, DataRef{T})"/>
    /// path instead; a guard test whitelists the files allowed to call this directly.
    /// Repeated subscriptions to the same name share one server registration at the fastest
    /// requested tier. Dispose the handle to release the registration.
    /// </summary>
    IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier);

    /// <summary>
    /// Writes a value to an allow-listed dataref.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dataref is not on the write allow-list.</exception>
    Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a serialized momentary press (write 1 → hold → write 0 → gap) on an allow-listed
    /// switch dataref. Presses from all features are serialized through one worker so they can
    /// never interleave. The returned task completes when the press has been performed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dataref is not on the write allow-list.</exception>
    Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// A cached view of one subscribed dataref. Values update from ProSim push notifications;
/// <see cref="ValueChanged"/> fires on the SDK's thread — consumers marshal to their own context.
/// On disconnect values are flagged stale but retained ("valid or hold previous decision").
/// </summary>
public interface IDataRefSubscription : IDisposable
{
    /// <summary>Canonical dataref name.</summary>
    string Name { get; }

    /// <summary>Latest raw value pushed by ProSim, or null if none received yet.</summary>
    object? RawValue { get; }

    /// <summary>True when the connection dropped after this value was received.</summary>
    bool IsStale { get; }

    /// <summary>UTC time of the last push, or null if none received yet.</summary>
    DateTimeOffset? LastUpdatedUtc { get; }

    /// <summary>Raised after each pushed update, on the SDK's (arbitrary) thread.</summary>
    event EventHandler? ValueChanged;

    /// <summary>Returns the cached value coerced to <typeparamref name="T"/>, or the fallback
    /// when no value has arrived or the value cannot be coerced.</summary>
    T GetValue<T>(T fallback);
}
