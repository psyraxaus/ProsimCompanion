namespace ProsimCompanion.Core.State;

/// <summary>Whether the First Officer is in the cockpit to answer (issue #196).</summary>
/// <param name="IsAway">True while the FO is out of the cockpit (the walkaround).</param>
/// <param name="Reason">What they are doing ("walkaround"), for the event and the page.</param>
/// <param name="SinceUtc">When they left.</param>
public sealed record FoAvailability(bool IsAway, string? Reason, DateTimeOffset? SinceUtc)
{
    public static FoAvailability Present { get; } = new(false, null, null);
}

/// <summary>
/// The one place that says whether the FO can answer the captain right now (issue #196). The
/// utterance router reads it before offering an utterance to an FO-voiced feature: while the
/// FO is away the feature is skipped and the deterministic <see cref="AwayLine"/> speaks
/// instead. Safety features declare themselves available regardless. The departure pump reads
/// it to hold the refuel call until the walkaround report. Kept in Core so the Speech, Gsx
/// and Web pillars share it without referencing each other.
/// </summary>
public sealed class FoAvailabilityStore : SnapshotStore<FoAvailability>
{
    /// <summary>The one line every gated feature answers with while the FO is away.</summary>
    public const string AwayLine = "The First Officer is on the walkaround, captain.";

    public const string WalkaroundReason = "walkaround";

    public FoAvailabilityStore()
        : base(FoAvailability.Present)
    {
    }

    /// <summary>True while the FO is out of the cockpit.</summary>
    public bool IsAway => Snapshot().IsAway;

    /// <summary>The FO leaves the cockpit.</summary>
    public void SetAway(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Update(_ => new FoAvailability(true, reason, nowUtc));
    }

    /// <summary>The FO is back.</summary>
    public void Clear() => Update(_ => FoAvailability.Present);
}
