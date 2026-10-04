using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// <see cref="IRunwayGeometrySource"/> over the Navigraph DFD's runway table — the tier behind
/// the ProSim gateway for the pushback advisor (2026-10-04). The DFD's runway record is the
/// landing threshold with the true bearing, exactly the "where does the runway start and which
/// way does it point" the advisor needs. Nothing cached (the <see cref="DfdAirportNames"/>
/// rule); no DFD or no such runway is null.
/// </summary>
public sealed class DfdRunwayGeometry : IRunwayGeometrySource
{
    private readonly DfdNavDataProvider _navData;

    public DfdRunwayGeometry(DfdNavDataProvider navData)
    {
        ArgumentNullException.ThrowIfNull(navData);
        _navData = navData;
    }

    public string Name => "dfd";

    public int Order => 20;

    public Task<RunwayGeometry?> FindAsync(string icao, string runway, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao) || RunwayLocator.NormalizeIdent(runway) is not { } ident)
        {
            return Task.FromResult<RunwayGeometry?>(null);
        }

        var id = icao.Trim().ToUpperInvariant();
        var geometry = _navData.RunwayThreshold(id, ident) is { } threshold
            && GeoPoint.FromRaw(threshold.LatitudeDeg, threshold.LongitudeDeg) is { } point
                ? new RunwayGeometry(id, ident, point, threshold.TrueBearingDeg, threshold.LengthFt is { } l ? (int)Math.Round(l) : null, Name)
                : null;
        return Task.FromResult(geometry);
    }
}
