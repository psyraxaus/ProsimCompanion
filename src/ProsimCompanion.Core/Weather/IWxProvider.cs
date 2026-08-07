namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Provides weather facts for an airport. Implementations follow the degrade-not-fail rule:
/// they return <see cref="WxFacts.None"/> when their source is unavailable and never throw,
/// so the composite chain can always fall through to the next tier.
/// </summary>
public interface IWxProvider
{
    /// <summary>Weather for <paramref name="icao"/>; <see cref="WxFacts.None"/> when this
    /// source has nothing (never throws).</summary>
    Task<WxFacts> GetAsync(string? icao, CancellationToken cancellationToken = default);
}
