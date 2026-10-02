using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// <see cref="IAirportCoordinateSource"/> over the Navigraph DFD's airport reference point —
/// the fallback tier behind the gateway (issue #145). Nothing is cached here: the caller
/// resolves once per OFP, and a DFD configured mid-session must start answering without a
/// restart (the <see cref="DfdAirportNames"/> rule). No DFD or no such airport is null.
/// </summary>
public sealed class DfdAirportCoordinates : IAirportCoordinateSource
{
    private readonly DfdNavDataProvider _navData;

    public DfdAirportCoordinates(DfdNavDataProvider navData)
    {
        ArgumentNullException.ThrowIfNull(navData);
        _navData = navData;
    }

    public string Name => "dfd";

    public int Order => 20;

    public Task<AirportLocation?> FindAsync(string icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return Task.FromResult<AirportLocation?>(null);
        }

        var id = icao.Trim().ToUpperInvariant();
        // AirportReferencePoint is already null on any DFD/query failure.
        var location = _navData.AirportReferencePoint(id) is { } reference
            && GeoPoint.FromRaw(reference.LatitudeDeg, reference.LongitudeDeg) is { } point
                ? new AirportLocation(id, point, reference.ElevationFt, Name)
                : null;
        return Task.FromResult(location);
    }
}
