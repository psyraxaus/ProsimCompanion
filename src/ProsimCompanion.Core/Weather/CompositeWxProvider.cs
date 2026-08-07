using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Chains weather providers in priority order — ActiveSky (file then API), ProSim gateway
/// METAR, then the SayIntentions store cache — returning the first tier that yields a real
/// observation (a non-null <see cref="WxFacts.RawMetar"/>). The injected sim weather wins,
/// but a machine without ActiveSky still gets weather.
/// <para>Merge rule: when the winning tier lacks ATIS letter / active runway (only SI carries
/// them), they are backfilled from the <see cref="WeatherStore"/> if SI weather has already
/// been fetched — a free read, never an extra network call.</para>
/// Never throws.
/// </summary>
public sealed class CompositeWxProvider : IWxProvider
{
    private readonly IReadOnlyList<IWxProvider> _providers;
    private readonly WeatherStore _store;
    private readonly ILogger<CompositeWxProvider> _logger;

    /// <param name="providers">The tiers, highest priority first.</param>
    /// <param name="store">SayIntentions weather cache used for the ATIS/runway backfill.</param>
    /// <param name="logger">Diagnostics only — a throwing tier is logged and skipped.</param>
    public CompositeWxProvider(
        IReadOnlyList<IWxProvider> providers,
        WeatherStore store,
        ILogger<CompositeWxProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers;
        _store = store;
        _logger = logger;
    }

    public async Task<WxFacts> GetAsync(string? icao, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var facts = await provider.GetAsync(icao, cancellationToken).ConfigureAwait(false);
                if (facts.RawMetar is not null)
                {
                    return Backfill(facts, icao);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Providers promise not to throw; if one does anyway, the chain must survive.
                _logger.LogDebug(ex, "Weather provider {Provider} threw for {Icao}",
                    provider.GetType().Name, icao);
            }
        }

        return WxFacts.None;
    }

    private WxFacts Backfill(WxFacts facts, string? icao)
    {
        if (facts.AtisLetter is not null && facts.ActiveRunway is not null)
        {
            return facts;
        }

        var cached = _store.Snapshot().ForIcao(icao);
        if (cached is null)
        {
            return facts;
        }

        return facts with
        {
            AtisLetter = facts.AtisLetter ?? AtisLetter.Extract(cached.Atis),
            ActiveRunway = facts.ActiveRunway
                ?? (string.IsNullOrWhiteSpace(cached.ActiveRunway) ? null : cached.ActiveRunway),
        };
    }
}
