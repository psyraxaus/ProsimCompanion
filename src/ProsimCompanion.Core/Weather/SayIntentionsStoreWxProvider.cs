using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// The SayIntentions tier of the composite chain — deliberately lazy: it only reads what the
/// SayIntentions weather service has already cached in the <see cref="WeatherStore"/> and
/// never makes a network call of its own. The store's own TTL/debounce policy governs
/// freshness; a briefing must never add surprise API traffic just because a lower tier was
/// empty. Never throws.
/// </summary>
public sealed class SayIntentionsStoreWxProvider : IWxProvider
{
    private readonly WeatherStore _store;

    public SayIntentionsStoreWxProvider(WeatherStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task<WxFacts> GetAsync(string? icao, CancellationToken cancellationToken = default)
    {
        var entry = _store.Snapshot().ForIcao(icao);
        return Task.FromResult(entry is null ? WxFacts.None : ToFacts(entry));
    }

    /// <summary>Builds facts from a cached SI entry: explicit wind fields win over the parsed
    /// METAR (SI computes them server-side); everything else derives from the METAR text.</summary>
    public static WxFacts ToFacts(AirportWeather entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var metar = string.IsNullOrWhiteSpace(entry.Metar) ? null : entry.Metar.Trim();
        var parsed = MetarParser.Parse(metar);
        return new WxFacts(
            metar,
            entry.WindDirection ?? parsed.WindDirDeg,
            entry.WindSpeed ?? parsed.WindSpeedKt,
            parsed.VisibilityMeters,
            parsed.QnhHpa,
            parsed.TemperatureC,
            AtisLetter.Extract(entry.Atis),
            string.IsNullOrWhiteSpace(entry.ActiveRunway) ? null : entry.ActiveRunway,
            parsed.CeilingFt,
            parsed.WindGustKt,
            parsed.Precip);
    }
}
