using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The one write path for the GSX sync modules' EFB-domain writes (pax zones, cargo, ground
/// services, refuel power): routes via the ProSim gateway — the predecessors' proven transport
/// for these datarefs, several of which reject SDK writes — and, crucially, <b>never lets a
/// failure vanish</b>: every failed write is logged and decision-logged (smoke-test find:
/// fire-and-forget SDK writes failed invisibly for the whole boarding sequence).
/// </summary>
public sealed class GsxProsimWriter
{
    private readonly IProsimGateway _gateway;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxProsimWriter> _logger;

    public GsxProsimWriter(IProsimGateway gateway, GsxDiagnosticsStore diagnostics, ILogger<GsxProsimWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>Writes via the gateway; failures are logged + decision-logged, never thrown.</summary>
    public async Task<bool> WriteAsync(string name, object value, CancellationToken cancellationToken = default)
    {
        try
        {
            var ok = await _gateway.WriteDataRefAsync(name, value, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                _logger.LogDebug("Gateway write {DataRef} = {Value}", name, value);
            }
            else
            {
                Report(name, value, "gateway rejected/unreachable");
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(name, value, ex.Message);
            return false;
        }
    }

    private void Report(string name, object value, string reason)
    {
        _logger.LogWarning("Write FAILED: {DataRef} = {Value} ({Reason})", name, value, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(
            DateTimeOffset.UtcNow,
            "write failed",
            $"{name} = {value} ({reason})"));
    }
}
