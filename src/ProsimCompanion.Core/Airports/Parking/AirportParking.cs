using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>A position plus the heading the aircraft faces there (degrees true, 0–360).</summary>
public readonly record struct GeoPose(GeoPoint Position, double HeadingDeg)
{
    /// <summary>Builds a pose from raw numbers; null when the position is not one
    /// (<see cref="GeoPoint.FromRaw"/>). GSX writes headings in −180..180; normalised here.</summary>
    public static GeoPose? FromRaw(double lat, double lon, double headingDeg)
    {
        if (GeoPoint.FromRaw(lat, lon) is not { } point || !double.IsFinite(headingDeg))
        {
            return null;
        }

        var heading = headingDeg % 360;
        return new GeoPose(point, heading < 0 ? heading + 360 : heading);
    }
}

/// <summary>Which automatic pushback directions a stand allows — the scenery's
/// <c>TAXI_PARKING.PUSHBACK</c> / GSX's <c>pushback</c> ini key (0 none, 1 left, 2 right, 3 both).</summary>
public enum PushbackDirections
{
    None = 0,
    Left = 1,
    Right = 2,
    Both = 3,
}

/// <summary>One line of GSX's "Select pushback direction" menu as the profile predicts it:
/// the label GSX will print and, when the profile stores it, where the aircraft ends up.</summary>
/// <param name="Label">Menu text ("Nose Right/Tail Left (LEFT)" by default, or the author's
/// custom label such as "Facing SW on Taxi AV").</param>
/// <param name="Kind">Which slot this is.</param>
/// <param name="FinalPose">Position + heading at release, when the profile has a custom route;
/// null for GSX's fully automatic default route.</param>
public sealed record PushbackSlot(string Label, PushbackSlotKind Kind, GeoPose? FinalPose);

public enum PushbackSlotKind
{
    /// <summary>GSX's LEFT slot — nose ends pointing right ("Nose Right/Tail Left").</summary>
    Left,

    /// <summary>GSX's RIGHT slot — nose ends pointing left ("Nose Left/Tail Right").</summary>
    Right,

    /// <summary>An author-added slot (<c>pushbackaddpos</c>) with its own label and route.</summary>
    Additional,
}

/// <summary>The pushback knowledge a stand carries (GSX ini, manual p.52–53).</summary>
public sealed record ParkingPushback(PushbackDirections Allowed, IReadOnlyList<PushbackSlot> Slots)
{
    public static readonly ParkingPushback Unknown = new(PushbackDirections.Both, []);
}

/// <summary>Where a fact about a parking came from — merged records carry every flag.</summary>
[Flags]
public enum ParkingDataSources
{
    None = 0,

    /// <summary>GSX airport profile <c>.ini</c> (user APPDATA or scenery-package copy).</summary>
    GsxIni = 1,

    /// <summary>GSX airport customisation <c>.py</c> (name templates).</summary>
    GsxPy = 2,

    /// <summary>MSFS facility data over SimConnect (the scenery itself).</summary>
    Facility = 4,
}

/// <summary>
/// Everything the app knows about one parking position at an airport, merged from the GSX
/// profile and the simulator's facility data. Every field but the identity is optional —
/// a stand may be known only from the ini, only from the scenery, or from both.
/// </summary>
/// <param name="Identity">Scenery identity (name group + number + suffix).</param>
/// <param name="GsxUiName">GSX's full menu name when a <c>.py</c> template resolves it, e.g.
/// "Apron 1W (Gates W34-W48) | Gate 40" — the string <c>gate.select</c> echoes as <c>uiName</c>.</param>
/// <param name="GsxGateName">The part after the <c>|</c> ("Gate 40"), GSX's <c>uiGateName</c>.</param>
/// <param name="TypeCode">Scenery parking type (SDK enum: 9 GATE_MEDIUM, 10 GATE_HEAVY, 5 RAMP_CARGO…).</param>
/// <param name="Pose">Stand position and heading when known.</param>
/// <param name="RadiusM">Scenery radius (facility data).</param>
/// <param name="RadiusLeftM">GSX ini left radius (wing-tip clearance left).</param>
/// <param name="RadiusRightM">GSX ini right radius.</param>
/// <param name="MaxWingspanM">GSX ini maximum wingspan.</param>
/// <param name="HasJetway">Jetway at the stand (ini <c>hasjetway</c>, or a facility JETWAY record).</param>
/// <param name="AirlineCodes">ICAO airline codes assigned in the scenery / ini.</param>
/// <param name="HandlingOperators">GSX ini <c>handlingtexture</c> operator list.</param>
/// <param name="ParkingSystem">GSX ini guidance system ("SafeDockTS42", "Marshaller").</param>
/// <param name="Pushback">Pushback directions and slots.</param>
/// <param name="Sources">Which sources contributed.</param>
public sealed record AirportParking(
    ParkingIdentity Identity,
    string? GsxUiName,
    string? GsxGateName,
    int? TypeCode,
    GeoPose? Pose,
    double? RadiusM,
    double? RadiusLeftM,
    double? RadiusRightM,
    double? MaxWingspanM,
    bool? HasJetway,
    IReadOnlyList<string> AirlineCodes,
    IReadOnlyList<string> HandlingOperators,
    string? ParkingSystem,
    ParkingPushback? Pushback,
    ParkingDataSources Sources)
{
    /// <summary>The name GSX shows: the template name when known, else the default shape.</summary>
    public string DisplayName => GsxGateName ?? Identity.DefaultDisplayName;

    /// <summary>Merges a second record for the same identity: non-null wins, the first record
    /// wins ties; lists union; sources OR.</summary>
    public AirportParking MergeWith(AirportParking other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return this with
        {
            GsxUiName = GsxUiName ?? other.GsxUiName,
            GsxGateName = GsxGateName ?? other.GsxGateName,
            TypeCode = TypeCode ?? other.TypeCode,
            Pose = Pose ?? other.Pose,
            RadiusM = RadiusM ?? other.RadiusM,
            RadiusLeftM = RadiusLeftM ?? other.RadiusLeftM,
            RadiusRightM = RadiusRightM ?? other.RadiusRightM,
            MaxWingspanM = MaxWingspanM ?? other.MaxWingspanM,
            HasJetway = HasJetway ?? other.HasJetway,
            AirlineCodes = AirlineCodes.Union(other.AirlineCodes, StringComparer.OrdinalIgnoreCase).ToList(),
            HandlingOperators = HandlingOperators.Union(other.HandlingOperators, StringComparer.OrdinalIgnoreCase).ToList(),
            ParkingSystem = ParkingSystem ?? other.ParkingSystem,
            Pushback = Pushback ?? other.Pushback,
            Sources = Sources | other.Sources,
        };
    }

    /// <summary>A bare record with only the identity, for sources that know nothing else.</summary>
    public static AirportParking Bare(ParkingIdentity identity, ParkingDataSources source)
        => new(identity, null, null, null, null, null, null, null, null, null, [], [], null, null, source);
}

/// <summary>The merged parking catalogue of one airport.</summary>
public sealed record AirportParkings(
    string Icao,
    IReadOnlyList<AirportParking> Parkings,
    ParkingDataSources Sources,
    IReadOnlyList<string> SourceNotes,
    DateTimeOffset LoadedAt)
{
    /// <summary>True when the GSX profile supplied name templates — then the display names are
    /// GSX's real menu names and not the default guess.</summary>
    public bool HasGsxNames => Sources.HasFlag(ParkingDataSources.GsxPy);
}

/// <summary>
/// One place parking knowledge can come from — the GSX profile reader and the facility-data
/// reader implement it in their own projects; Core merges. The contract follows
/// <see cref="IAirportCoordinateSource"/>: never throw, "unknown" is null.
/// </summary>
public interface IAirportParkingSource
{
    /// <summary>Short name for logs and session events ("gsx-profile", "facility").</summary>
    string Name { get; }

    /// <summary>Merge precedence — lower wins ties for the same field. GSX profile 10 (what GSX
    /// itself uses for the stand), scenery facility data 20.</summary>
    int Order { get; }

    /// <summary>The parkings this source knows for the airport, or null when it knows nothing
    /// (no profile, sim not connected). Only cancellation may throw.</summary>
    Task<AirportParkingSourceResult?> LoadAsync(string icao, CancellationToken cancellationToken = default);
}

/// <summary>One source's answer: its parkings plus a one-line note for diagnostics
/// ("EFHK-MKStudios-GSX-luca.br.ini + EFHK-MKStudios.py (user)", "facility: 131 parkings").</summary>
public sealed record AirportParkingSourceResult(IReadOnlyList<AirportParking> Parkings, string Note);

/// <summary>The merged, cached view the rest of the app reads.</summary>
public interface IAirportParkingCatalog
{
    /// <summary>The merged catalogue for the airport, or null when no source knows it.
    /// Cached per ICAO; <paramref name="refresh"/> re-reads every source.</summary>
    Task<AirportParkings?> GetAsync(string icao, bool refresh = false, CancellationToken cancellationToken = default);
}
