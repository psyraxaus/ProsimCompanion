using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Airports;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// <see cref="IDepartureRunwaySource"/> over the briefing <see cref="ProcedureSource"/>: the
/// departure runway the FMS actually has (then the configured dataref, flight.json, the
/// manual setting) — fresher than the OFP's plan when ATC changed the runway after dispatch.
/// Feeds the pushback advisor (2026-10-04).
/// </summary>
public sealed class FmsDepartureRunway : IDepartureRunwaySource
{
    private readonly ProcedureSource _procedures;
    private readonly ILogger<FmsDepartureRunway> _logger;

    public FmsDepartureRunway(ProcedureSource procedures, ILogger<FmsDepartureRunway> logger)
    {
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(logger);
        _procedures = procedures;
        _logger = logger;
    }

    public (string Airport, string Runway)? Current()
    {
        try
        {
            var ids = _procedures.Resolve(departure: true);
            if (string.IsNullOrWhiteSpace(ids.Airport) || RunwayLocator.NormalizeIdent(ids.Runway) is not { } runway)
            {
                return null;
            }

            return (ids.Airport.Trim().ToUpperInvariant(), runway);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Departure runway lookup failed");
            return null;
        }
    }
}
