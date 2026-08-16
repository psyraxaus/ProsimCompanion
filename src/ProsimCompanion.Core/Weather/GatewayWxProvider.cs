using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Gateway;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Weather via the ProSim EFB gateway METAR endpoint — the tier between ActiveSky (the
/// injected truth) and SayIntentions (the network fallback). The gateway's 204 "no METAR" is
/// a success (<see cref="WxProbeStatus.NoData"/>), while an unreachable/erroring endpoint is
/// <see cref="WxProbeStatus.Unavailable"/> carrying the HTTP status (issue #62). Never throws.
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

    public async Task<WxProbe> ProbeAsync(string? icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return WxProbe.NoData(null);
        }

        var id = icao.Trim().ToUpperInvariant();
        try
        {
            var fetch = await _gateway.GetMetarAsync(id, cancellationToken).ConfigureAwait(false);
            if (!fetch.Succeeded)
            {
                return WxProbe.Unavailable(fetch.FailureReason);
            }

            return string.IsNullOrWhiteSpace(fetch.Metar?.MetarText)
                ? WxProbe.NoData($"gateway has no METAR for {id}")
                : WxProbe.Found(MetarParser.ToFacts(fetch.Metar.MetarText));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway METAR fetch failed for {Icao}", icao);
            return WxProbe.Unavailable($"gateway: {ex.Message}");
        }
    }
}
