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

    public async Task<WxProbe> ProbeAsync(string? icao, CancellationToken cancellationToken = default)
    {
        // Per-tier reasons accumulate so an all-empty chain can say WHY (issue #62: a
        // deterministic gateway 500 rendered as a bare "No METAR available" for a whole
        // flight while other tiers held valid observations).
        var details = new List<string>();
        var anyNoData = false;

        foreach (var provider in _providers)
        {
            try
            {
                var probe = await provider.ProbeAsync(icao, cancellationToken).ConfigureAwait(false);
                if (probe.Status == WxProbeStatus.Found && probe.Facts.RawMetar is not null)
                {
                    return WxProbe.Found(Backfill(probe.Facts, icao));
                }

                anyNoData |= probe.Status == WxProbeStatus.NoData;
                if (!string.IsNullOrWhiteSpace(probe.Detail))
                {
                    details.Add(probe.Detail);
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
                details.Add($"{provider.GetType().Name}: {ex.Message}");
            }
        }

        var detail = details.Count > 0 ? string.Join("; ", details) : null;
        // Any tier authoritatively answering "nothing for this ICAO" beats "everything was
        // unreachable" — the pilot's next action differs (accept no data vs. fix a connection).
        return anyNoData ? WxProbe.NoData(detail) : WxProbe.Unavailable(detail);
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
