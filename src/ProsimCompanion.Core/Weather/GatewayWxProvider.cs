using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Gateway;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Weather via the ProSim EFB gateway METAR endpoint — the tier between ActiveSky (the
/// injected truth) and SayIntentions (the network fallback). The gateway's 204 "no METAR" is
/// a success (surfaced by <see cref="IProsimGateway.GetMetarAsync"/> as null), not an error,
/// so it simply falls through. Never throws.
/// </summary>
public sealed class GatewayWxProvider : IWxProvider
{
    private readonly IProsimGateway _gateway;
    private readonly ILogger<GatewayWxProvider> _logger;

    public GatewayWxProvider(IProsimGateway gateway, ILogger<GatewayWxProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _logger = logger;
    }

    public async Task<WxFacts> GetAsync(string? icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return WxFacts.None;
        }

        try
        {
            var metar = await _gateway.GetMetarAsync(icao.Trim().ToUpperInvariant(), cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(metar?.MetarText)
                ? WxFacts.None
                : MetarParser.ToFacts(metar.MetarText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway METAR fetch failed for {Icao}", icao);
            return WxFacts.None;
        }
    }
}
