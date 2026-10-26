namespace ProsimCompanion.Core.Aircraft;

/// <summary>Registered names and how many of them are stale, for the diagnostics pages.</summary>
public readonly record struct SubscriptionStats(int Count, int Stale);

/// <summary>
/// The subscription counters of one push feed (ProSim datarefs, SimConnect simvars), exposed by
/// the feed's service beside its read seam rather than widening <see cref="IProsimDataRefs"/>
/// or <see cref="ISimVars"/>: a consumer that only reads values never sees the plumbing.
/// </summary>
public interface IDataRefSubscriptionStats
{
    /// <summary>Names currently registered with the feed.</summary>
    int Count { get; }

    /// <summary>Of those, the names whose cached value is stale or has never arrived.</summary>
    int Stale { get; }
}
