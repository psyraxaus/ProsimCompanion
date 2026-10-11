namespace ProsimCompanion.Core.Airports;

/// <summary>
/// Remembers the answer of one runway-geometry lookup per (airport, runway, plan) key so a
/// tick-driven caller never re-asks the gateway for an answer it already has — including a
/// <em>missing</em> one (issue #175: the pushback advisor retried a null answer on every GSX
/// store push, 1 435 <c>runways EGCC</c> round-trips in one leg, one per second). A found
/// runway is kept for the key's lifetime; a miss is kept for <see cref="MissRetryAfter"/> and
/// then asked once more, because the gateway may simply not have been up yet. Pure; the
/// caller supplies the clock. Not thread-safe — the owner serialises evaluations.
/// </summary>
public sealed class RunwayLookupCache
{
    /// <summary>How long a "not found" answer is trusted before one more lookup.</summary>
    public static readonly TimeSpan MissRetryAfter = TimeSpan.FromMinutes(5);

    private string? _key;
    private RunwayGeometry? _geometry;
    private DateTimeOffset _answeredAtUtc;

    /// <summary>The cache key: airport, runway and the plan the pair came from (an OFP request
    /// id — a new plan is a new question even for the same runway).</summary>
    public static string Key(string icao, string runway, string? planId)
        => $"{icao.Trim().ToUpperInvariant()}|{runway.Trim().ToUpperInvariant()}|{planId ?? ""}";

    /// <summary>True when the cache has an answer for <paramref name="key"/> that is still
    /// trusted at <paramref name="nowUtc"/>; <paramref name="geometry"/> is that answer (null =
    /// a remembered miss).</summary>
    public bool TryGet(string key, DateTimeOffset nowUtc, out RunwayGeometry? geometry)
    {
        ArgumentNullException.ThrowIfNull(key);
        geometry = null;
        if (!string.Equals(key, _key, StringComparison.Ordinal))
        {
            return false;
        }

        if (_geometry is null && nowUtc - _answeredAtUtc >= MissRetryAfter)
        {
            return false;
        }

        geometry = _geometry;
        return true;
    }

    /// <summary>Stores the answer of a lookup (null = not found).</summary>
    public void Store(string key, RunwayGeometry? geometry, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
        _geometry = geometry;
        _answeredAtUtc = nowUtc;
    }
}
