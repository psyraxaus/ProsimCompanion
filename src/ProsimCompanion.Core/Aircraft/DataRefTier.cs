namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Polling cadence tiers for dataref push subscriptions, carried over from the predecessors'
/// proven tiering. The numeric value is the interval in milliseconds.
/// </summary>
public enum DataRefTier
{
    /// <summary>100 ms — flight-critical values (callout triggers, control positions).</summary>
    Critical = 100,

    /// <summary>250 ms — frequently-changing values (speeds, altitudes, phases).</summary>
    Frequent = 250,

    /// <summary>500 ms — normal state (doors, ground services, fuel).</summary>
    Normal = 500,

    /// <summary>2000 ms — slow-moving state (config, planning data).</summary>
    Infrequent = 2000,
}
