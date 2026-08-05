using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Acars;
using ProsimCompanion.Core.Aircraft.Gateway;

namespace ProsimCompanion.Prosim.Acars;

/// <summary>
/// Sends ACARS uplinks to the cockpit RCVD MSGS list: one GraphQL writeString of the lowercase
/// envelope to <c>efb.aoc.message.uplink</c>. ProSim dispatches asynchronously (~1 s); the
/// result dataref (<c>efb.aoc.message.uplink.result</c>, "Message processed" on success) is
/// diagnostics-only — the predecessor never gated on it and neither do we.
/// </summary>
public sealed class AcarsUplink
{
    private readonly IProsimGateway _gateway;
    private readonly ILogger<AcarsUplink> _logger;

    public AcarsUplink(IProsimGateway gateway, ILogger<AcarsUplink> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);
        _gateway = gateway;
        _logger = logger;
    }

    public async Task<bool> SendAsync(AcarsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var ok = await _gateway.WriteDataRefAsync(
            ProsimDataRefNames.AocMessageUplink,
            message.ToWireJson(),
            cancellationToken).ConfigureAwait(false);

        if (ok)
        {
            _logger.LogInformation(
                "ACARS uplink dispatched: {Type} slot {Slot} ({Length} chars)",
                message.Type, message.Id, message.Content.Length);
        }
        else
        {
            _logger.LogWarning("ACARS uplink failed for {Type} slot {Slot} — see gateway log", message.Type, message.Id);
        }

        return ok;
    }
}
