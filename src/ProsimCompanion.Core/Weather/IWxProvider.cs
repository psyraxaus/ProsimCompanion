namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Provides weather facts for an airport. Implementations follow the degrade-not-fail rule:
/// they return an empty probe when their source is unavailable and never throw, so the
/// composite chain can always fall through to the next tier. The probe carries a
/// per-source reason (issue #62) so a consumer can tell "no data for this ICAO" apart from
/// "the source could not be asked" instead of rendering a bare "no METAR".
/// </summary>
public interface IWxProvider
{
    /// <summary>Weather for <paramref name="icao"/> with an outcome classification; an empty
    /// probe (<see cref="WxProbeStatus.NoData"/> / <see cref="WxProbeStatus.Unavailable"/>)
    /// when this source has nothing (never throws).</summary>
    Task<WxProbe> ProbeAsync(string? icao, CancellationToken cancellationToken = default);
}

/// <summary>Convenience for consumers that only want the facts (briefings, weather watch) —
/// the probe reasons exist for UI surfaces; everything else keeps the old one-liner.</summary>
public static class WxProviderExtensions
{
    /// <summary>Weather facts for <paramref name="icao"/>; <see cref="WxFacts.None"/> when the
    /// provider has nothing.</summary>
    public static async Task<WxFacts> GetAsync(
        this IWxProvider provider, string? icao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var probe = await provider.ProbeAsync(icao, cancellationToken).ConfigureAwait(false);
        return probe.Facts;
    }
}
