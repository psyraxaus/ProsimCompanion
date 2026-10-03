using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Geo;

/// <summary>Where the aircraft is, in words a First Officer would use (issue #153).</summary>
public interface IPlaceLookup
{
    /// <summary>Resolves a position against the atlas. Never throws; an empty fix (no country,
    /// no sea, no towns) means the atlas has nothing there.</summary>
    PlaceFix Locate(GeoPoint position, double? headingDeg);
}

/// <summary>A town near the aircraft: how far, which way, and which side of the nose.</summary>
/// <param name="BearingDeg">True bearing from the aircraft to the town.</param>
/// <param name="Side">"ahead", "right", "behind", "left" relative to the heading; null without one.</param>
public sealed record NearbyTown(AtlasTown Town, double DistanceNm, double BearingDeg, string? Side);

/// <summary>The resolved place. <see cref="Reference"/> is the big city the position is
/// described against ("forty miles south-east of Paris"); <see cref="Nearest"/> the closest
/// notable town when it is a different one.</summary>
public sealed record PlaceFix(
    GeoPoint Position,
    AtlasCountry? Country,
    string? CountryPart,
    AtlasArea? Region,
    AtlasArea? Sea,
    NearbyTown? Reference,
    NearbyTown? Nearest)
{
    public bool IsEmpty => Country is null && Region is null && Sea is null && Reference is null && Nearest is null;
}

/// <summary>
/// Pure lookup over an <see cref="Atlas"/>: the country whose outline holds the point (and
/// which part of it — "northern France" — from the containing polygon's extent), the most
/// specific named region and sea, and the towns worth naming. Linear scans; the atlas is a
/// few hundred shapes and twenty-odd thousand towns, a millisecond or two per question.
/// </summary>
public sealed class PlaceLookup : IPlaceLookup
{
    /// <summary>Towns farther than this are not "near".</summary>
    public const double TownRadiusNm = 150;

    /// <summary>The reference city is the biggest within this radius…</summary>
    public const double ReferenceRadiusNm = 120;

    /// <summary>…and must be at least this big, or the position is described by the nearest town alone.</summary>
    public const long ReferenceMinimumPopulation = 100_000;

    /// <summary>A "notable" nearest town: this big, or a capital / first-order seat.</summary>
    public const long NotableMinimumPopulation = 50_000;

    private readonly Lazy<Atlas> _atlas;

    public PlaceLookup(Atlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        _atlas = new Lazy<Atlas>(() => atlas);
    }

    /// <summary>Deferred: the shipped atlas is only read when the first question arrives,
    /// not when the service graph is built at startup.</summary>
    public PlaceLookup(Func<Atlas> atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        _atlas = new Lazy<Atlas>(atlas, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private Atlas Atlas => _atlas.Value;

    public PlaceFix Locate(GeoPoint position, double? headingDeg)
    {
        AtlasCountry? country = null;
        AtlasPolygon? within = null;
        foreach (var candidate in Atlas.Countries)
        {
            if (candidate.Containing(position) is { } polygon
                && (within is null || polygon.AreaDeg2 < within.AreaDeg2))
            {
                // The smallest match wins: an enclave (Lesotho) over the country around it
                // when the outline carries no hole for it.
                country = candidate;
                within = polygon;
            }
        }

        var region = Smallest(Atlas.Regions.Where(r => WorthNaming(r) && r.Contains(position)));
        var sea = country is null ? Smallest(Atlas.Seas.Where(s => s.Contains(position))) : null;

        var (reference, nearest) = Towns(position, headingDeg);
        return new PlaceFix(position, country, within is null ? null : Part(position, within), region, sea, reference, nearest);
    }

    private static AtlasArea? Smallest(IEnumerable<AtlasArea> areas)
        => areas.OrderBy(a => a.AreaDeg2).FirstOrDefault();

    /// <summary>Regions too broad to be an answer are skipped: "the North European Plain"
    /// covers Paris to Warsaw, "Siberia" a continent's worth. Mountains, deserts, deltas,
    /// valleys and lakes read well at any size; everything else only when it is compact.</summary>
    public const double BroadRegionDeg2 = 40;

    internal static bool WorthNaming(AtlasArea region)
        => region.Kind is "Range/mtn" or "Desert" or "Delta" or "Valley" or "Gorge" or "Lake" or "Wetlands"
            || region.AreaDeg2 <= BroadRegionDeg2;

    /// <summary>"northern" / "south-western" / "central" from where the point sits in the
    /// containing outline's extent (thirds). Small countries get no part — nobody says
    /// "northern Luxembourg" from six miles up.</summary>
    internal static string? Part(GeoPoint point, AtlasPolygon polygon)
    {
        var width = polygon.MaxLon - polygon.MinLon;
        var height = polygon.MaxLat - polygon.MinLat;
        if (width < 4.0 || height < 3.0)
        {
            return null;
        }

        var lonFrac = (point.LongitudeDeg - polygon.MinLon) / width;
        var latFrac = (point.LatitudeDeg - polygon.MinLat) / height;
        var ns = latFrac >= 0.66 ? "north" : latFrac <= 0.34 ? "south" : "";
        var ew = lonFrac >= 0.66 ? "east" : lonFrac <= 0.34 ? "west" : "";
        return (ns, ew) switch
        {
            ("", "") => "central",
            ("", _) => ew + "ern",
            (_, "") => ns + "ern",
            _ => ns + "-" + ew + "ern",
        };
    }

    private (NearbyTown? Reference, NearbyTown? Nearest) Towns(GeoPoint position, double? headingDeg)
    {
        // Cheap band first: 150 nm is 2.5° of latitude; longitude widens towards the poles.
        var latBand = (TownRadiusNm / 60.0) + 0.1;
        var lonBand = latBand / Math.Max(0.2, Math.Cos(position.LatitudeDeg * Math.PI / 180));

        AtlasTown? nearestAny = null;
        AtlasTown? nearestNotable = null;
        AtlasTown? reference = null;
        double nearestAnyNm = double.MaxValue, nearestNotableNm = double.MaxValue, referenceNm = 0;
        double referenceScore = 0;

        foreach (var town in Atlas.Towns)
        {
            if (Math.Abs(town.Position.LatitudeDeg - position.LatitudeDeg) > latBand
                || LonDelta(town.Position.LongitudeDeg, position.LongitudeDeg) > lonBand)
            {
                continue;
            }

            var nm = GreatCircle.DistanceNm(position, town.Position);
            if (nm > TownRadiusNm)
            {
                continue;
            }

            if (nm < nearestAnyNm)
            {
                nearestAny = town;
                nearestAnyNm = nm;
            }

            var notable = town.Population >= NotableMinimumPopulation || town.Rank != TownRank.Town;
            if (notable && nm < nearestNotableNm)
            {
                nearestNotable = town;
                nearestNotableNm = nm;
            }

            if (nm <= ReferenceRadiusNm && town.Population >= ReferenceMinimumPopulation)
            {
                // Size over distance squared: a big city 60 nm off beats a bigger one 110 nm
                // off ("east of Aberdeen", not "east-north-east of Edinburgh"), while Paris
                // still wins over a town of a hundred thousand next to the track. Capitals
                // count triple. Inside 5 nm the distance stops mattering.
                var score = town.Population * (town.Rank == TownRank.Capital ? 3.0 : 1.0) / Math.Pow(Math.Max(nm, 5), 2);
                if (score > referenceScore)
                {
                    reference = town;
                    referenceScore = score;
                    referenceNm = nm;
                }
            }
        }

        var nearestTown = nearestNotable ?? nearestAny;
        var nearestNm = nearestNotable is not null ? nearestNotableNm : nearestAnyNm;
        var nearest = nearestTown is null ? null : Describe(position, nearestTown, nearestNm, headingDeg);
        var referenceView = reference is null ? null : Describe(position, reference, referenceNm, headingDeg);
        if (referenceView is not null && nearest is not null && ReferenceEquals(referenceView.Town, nearest.Town))
        {
            nearest = null;
        }

        return (referenceView, nearest);
    }

    private static double LonDelta(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static NearbyTown Describe(GeoPoint from, AtlasTown town, double nm, double? headingDeg)
    {
        var bearing = GreatCircle.InitialBearingDeg(from, town.Position);
        return new NearbyTown(town, nm, bearing, headingDeg is { } hdg ? Side(bearing, hdg) : null);
    }

    /// <summary>Which side of the nose a bearing lies: ±30° is ahead, 150–210 behind.</summary>
    public static string Side(double bearingDeg, double headingDeg)
    {
        var relative = ((bearingDeg - headingDeg) % 360 + 360) % 360;
        return relative switch
        {
            <= 30 or >= 330 => "ahead",
            < 150 => "right",
            <= 210 => "behind",
            _ => "left",
        };
    }
}
