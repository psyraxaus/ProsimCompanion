using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Sim.Facilities;

/// <summary>
/// The scenery tier of the parking catalogue (Option B, 2026-10-04): the simulator's own
/// TAXI_PARKING list for an airport over SimConnect facility data — the same source GSX reads
/// (its log: <c>requestFacilityData EFHK</c>). Works for streamed default airports in MSFS
/// 2024 (no local BGL exists for those) and for add-ons alike. Gives every stand's identity,
/// position, heading, radius, type and whether a jetway serves it.
/// <para>
/// This class holds no SimConnect types (the wrapper may be absent — degraded mode):
/// <see cref="SimConnectService"/> attaches a request delegate when connected, feeds rows
/// back through <see cref="OnRow"/> and completes a request via <see cref="OnEnd"/>. One
/// airport per request; requests time out after <see cref="Timeout"/>; a disconnect fails
/// every pending request to "unknown". Never throws out of <see cref="LoadAsync"/>.
/// </para>
/// </summary>
public sealed class AirportFacilityService : IAirportParkingSource
{
    /// <summary>Facility requests sit in their own id range so they can never be mistaken
    /// for a SimVar request id in logs or exception reports.</summary>
    public const uint RequestIdBase = 0x4000_0000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<AirportFacilityService> _logger;
    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private readonly object _gate = new();
    private Func<string, uint, bool>? _request;
    private uint _nextId = RequestIdBase;
    private bool _definitionFailed;

    public AirportFacilityService(ILogger<AirportFacilityService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string Name => "facility";

    public int Order => 20;

    /// <summary>True while the simulator is connected and the facility definition stands.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _request is not null && !_definitionFailed;
            }
        }
    }

    /// <summary>Called by the SimConnect service once connected and the definition is
    /// registered: the delegate issues <c>RequestFacilityData(icao, requestId)</c> and returns
    /// false when it could not be sent.</summary>
    internal void Attach(Func<string, uint, bool> request)
    {
        lock (_gate)
        {
            _request = request;
            _definitionFailed = false;
        }
    }

    /// <summary>The facility definition could not be registered (a field name the running
    /// sim does not know): the tier stays silent until the next connect.</summary>
    internal void MarkDefinitionFailed()
    {
        lock (_gate)
        {
            _definitionFailed = true;
        }
    }

    /// <summary>Disconnected: fail every pending request.</summary>
    internal void Detach()
    {
        lock (_gate)
        {
            _request = null;
        }

        foreach (var id in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Completion.TrySetResult(null);
            }
        }
    }

    /// <summary>A facility row arrived (pump thread): copy it, never block.</summary>
    internal void OnRow(uint requestId, object row)
    {
        if (!_pending.TryGetValue(requestId, out var pending))
        {
            return;
        }

        switch (row)
        {
            case FacilityAirportRow airport:
                pending.Airport = airport;
                break;
            case FacilityParkingRow parking:
                pending.Parkings.Add(parking);
                break;
            case FacilityJetwayRow jetway:
                pending.Jetways.Add(jetway);
                break;
        }
    }

    /// <summary>The request finished (pump thread).</summary>
    internal void OnEnd(uint requestId)
    {
        if (_pending.TryRemove(requestId, out var pending))
        {
            pending.Completion.TrySetResult(pending);
        }
    }

    /// <summary>SimConnect reported an exception for a request we sent: give up on it.</summary>
    internal void OnFailed(uint requestId, string reason)
    {
        if (_pending.TryRemove(requestId, out var pending))
        {
            _logger.LogWarning("Facility request for {Icao} failed: {Reason}", pending.Icao, reason);
            pending.Completion.TrySetResult(null);
        }
    }

    public async Task<AirportParkingSourceResult?> LoadAsync(string icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var id = icao.Trim().ToUpperInvariant();
        Func<string, uint, bool>? request;
        uint requestId;
        lock (_gate)
        {
            if (_request is null || _definitionFailed)
            {
                return null;
            }

            request = _request;
            requestId = _nextId++;
        }

        var pending = new Pending(id);
        _pending[requestId] = pending;
        try
        {
            if (!request(id, requestId))
            {
                _pending.TryRemove(requestId, out _);
                return null;
            }

            var completed = await pending.Completion.Task
                .WaitAsync(Timeout, cancellationToken)
                .ConfigureAwait(false);
            if (completed is null)
            {
                return null;
            }

            var parkings = ToParkings(completed.Airport, completed.Parkings, completed.Jetways);
            _logger.LogInformation(
                "Facility data for {Icao}: {Parkings} parkings, {Jetways} jetways (airport reference {Lat:F4},{Lon:F4})",
                id, parkings.Count, completed.Jetways.Count, completed.Airport?.Latitude ?? 0, completed.Airport?.Longitude ?? 0);
            return parkings.Count == 0
                ? null
                : new AirportParkingSourceResult(parkings, $"{parkings.Count} parkings, {completed.Jetways.Count} jetways");
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(requestId, out _);
            _logger.LogWarning("Facility data for {Icao} timed out after {Timeout}s", id, Timeout.TotalSeconds);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <summary>Pure: rows → shared parking records. Public for tests. Positions come from
    /// BIAS_X (east) / BIAS_Z (north) metres off the airport reference point; stands whose
    /// NAME is not a parking name (vehicles, fuel) are kept — the resolver filters by identity.</summary>
    public static IReadOnlyList<AirportParking> ToParkings(FacilityAirportRow? airport, IReadOnlyList<FacilityParkingRow> parkings, IReadOnlyList<FacilityJetwayRow> jetways)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        ArgumentNullException.ThrowIfNull(jetways);

        var jetwayKeys = new HashSet<(int, int, int)>(jetways.Select(j => (j.ParkingGate, j.ParkingSuffix, j.ParkingSpot)));
        var reference = airport is { } a ? GeoPoint.FromRaw(a.Latitude, a.Longitude) : null;
        var list = new List<AirportParking>(parkings.Count);
        foreach (var row in parkings)
        {
            if (row.Name is < 0 or > (int)ParkingName.GateZ)
            {
                continue;
            }

            var identity = new ParkingIdentity((ParkingName)row.Name, (int)row.Number, SuffixLetter(row.Suffix));
            GeoPose? pose = null;
            if (reference is { } origin)
            {
                var position = Offset(origin, row.BiasX, row.BiasZ);
                pose = GeoPose.FromRaw(position.LatitudeDeg, position.LongitudeDeg, row.HeadingDeg);
            }

            var hasJetway = jetwayKeys.Count > 0 ? jetwayKeys.Contains((row.Name, row.Suffix, (int)row.Number)) : (bool?)null;
            list.Add(new AirportParking(
                identity,
                GsxUiName: null,
                GsxGateName: null,
                TypeCode: row.Type,
                Pose: pose,
                RadiusM: row.RadiusM > 0 ? row.RadiusM : null,
                RadiusLeftM: null,
                RadiusRightM: null,
                MaxWingspanM: null,
                HasJetway: hasJetway,
                AirlineCodes: [],
                HandlingOperators: [],
                ParkingSystem: null,
                Pushback: null,
                Sources: ParkingDataSources.Facility));
        }

        return list;
    }

    /// <summary>SDK suffix enum: 0 none, 1..26 = A..Z.</summary>
    public static string SuffixLetter(int suffix) => suffix is >= 1 and <= 26 ? ((char)('A' + suffix - 1)).ToString() : "";

    /// <summary>Metres east/north of a reference point → position (flat-earth, fine within an airport).</summary>
    public static GeoPoint Offset(GeoPoint origin, double eastM, double northM)
    {
        const double metresPerDegreeLat = 111_320.0;
        var lat = origin.LatitudeDeg + (northM / metresPerDegreeLat);
        var metresPerDegreeLon = metresPerDegreeLat * Math.Cos(origin.LatitudeDeg * Math.PI / 180.0);
        var lon = metresPerDegreeLon < 1 ? origin.LongitudeDeg : origin.LongitudeDeg + (eastM / metresPerDegreeLon);
        return new GeoPoint(lat, lon);
    }

    private sealed class Pending(string icao)
    {
        public string Icao { get; } = icao;
        public FacilityAirportRow? Airport { get; set; }
        public List<FacilityParkingRow> Parkings { get; } = [];
        public List<FacilityJetwayRow> Jetways { get; } = [];
        public TaskCompletionSource<Pending?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
