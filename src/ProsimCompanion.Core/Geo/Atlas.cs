using System.IO.Compression;
using System.Text.Json;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Geo;

/// <summary>
/// The offline atlas behind "what are we flying over?" (issue #153): country outlines, named
/// seas, named natural regions (ranges, deserts, plateaus, plains, peninsulas, islands…) and
/// notable towns, shipped as one gzipped JSON resource (<c>Geo/Data/atlas.json.gz</c>, built by
/// <c>tools/build-atlas.js</c> from Natural Earth and GeoNames). Owner decision 2026-10-04: an
/// atlas in the box rather than an online geocoder — it answers with no network, always the
/// same way, and nothing in the flight depends on a third party's uptime or terms.
/// <para>
/// Sources and licences: countries Natural Earth 1:110m, seas and regions Natural Earth 1:50m
/// (both public domain); towns GeoNames <c>cities15000</c> (CC BY 4.0 — the attribution lives
/// in the manual and on the settings card).
/// </para>
/// </summary>
public sealed class Atlas
{
    public const string ResourceName = "ProsimCompanion.Core.Geo.Data.atlas.json.gz";

    private static readonly Lazy<Atlas> Shipped = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    public Atlas(IReadOnlyList<AtlasCountry> countries, IReadOnlyList<AtlasArea> seas, IReadOnlyList<AtlasArea> regions, IReadOnlyList<AtlasTown> towns)
    {
        Countries = countries ?? throw new ArgumentNullException(nameof(countries));
        Seas = seas ?? throw new ArgumentNullException(nameof(seas));
        Regions = regions ?? throw new ArgumentNullException(nameof(regions));
        Towns = towns ?? throw new ArgumentNullException(nameof(towns));
    }

    /// <summary>The embedded atlas, read once on first use (a 3 MB JSON — ~100 ms — so never
    /// on the startup path; the first question pays it).</summary>
    public static Atlas Default => Shipped.Value;

    public IReadOnlyList<AtlasCountry> Countries { get; }
    public IReadOnlyList<AtlasArea> Seas { get; }
    public IReadOnlyList<AtlasArea> Regions { get; }
    public IReadOnlyList<AtlasTown> Towns { get; }

    private static Atlas LoadShipped()
    {
        using var raw = typeof(Atlas).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded atlas '{ResourceName}' is missing.");
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        return Load(gzip);
    }

    /// <summary>Parses the atlas JSON (uncompressed). Public for the tests and the build check.</summary>
    public static Atlas Load(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var countries = new List<AtlasCountry>();
        foreach (var c in root.GetProperty("countries").EnumerateArray())
        {
            countries.Add(new AtlasCountry(
                c.GetProperty("n").GetString() ?? "",
                c.GetProperty("a2").GetString() ?? "",
                c.GetProperty("sub").GetString() ?? "",
                Shape(c.GetProperty("p"))));
        }

        var seas = new List<AtlasArea>();
        foreach (var s in root.GetProperty("seas").EnumerateArray())
        {
            seas.Add(new AtlasArea(s.GetProperty("n").GetString() ?? "", "Sea", Shape(s.GetProperty("p"))));
        }

        var regions = new List<AtlasArea>();
        foreach (var r in root.GetProperty("regions").EnumerateArray())
        {
            regions.Add(new AtlasArea(r.GetProperty("n").GetString() ?? "", r.GetProperty("k").GetString() ?? "", Shape(r.GetProperty("p"))));
        }

        var towns = new List<AtlasTown>();
        foreach (var t in root.GetProperty("towns").EnumerateArray())
        {
            towns.Add(new AtlasTown(
                t[0].GetString() ?? "",
                t[1].GetString() ?? "",
                new GeoPoint(t[2].GetDouble(), t[3].GetDouble()),
                t[4].GetInt64(),
                (TownRank)t[5].GetInt32()));
        }

        return new Atlas(countries, seas, regions, towns);
    }

    private static IReadOnlyList<AtlasPolygon> Shape(JsonElement polygons)
    {
        var list = new List<AtlasPolygon>();
        foreach (var polygon in polygons.EnumerateArray())
        {
            var rings = new List<double[]>();
            foreach (var ring in polygon.EnumerateArray())
            {
                var flat = new double[ring.GetArrayLength()];
                var i = 0;
                foreach (var v in ring.EnumerateArray())
                {
                    flat[i++] = v.GetDouble();
                }

                rings.Add(flat);
            }

            if (rings.Count > 0)
            {
                list.Add(new AtlasPolygon(rings));
            }
        }

        return list;
    }
}

/// <summary>How notable a town is beyond its population: a national capital or a first-order
/// administrative seat is kept in the atlas however small.</summary>
public enum TownRank
{
    Town = 0,
    AdminSeat = 1,
    Capital = 2,
}

public sealed record AtlasCountry(string Name, string Iso2, string Subregion, IReadOnlyList<AtlasPolygon> Polygons)
{
    /// <summary>The polygon containing the point, if any (France's mainland, not its Guiana).</summary>
    public AtlasPolygon? Containing(GeoPoint point) => Polygons.FirstOrDefault(p => p.Contains(point));
}

public sealed record AtlasArea(string Name, string Kind, IReadOnlyList<AtlasPolygon> Polygons)
{
    public bool Contains(GeoPoint point) => Polygons.Any(p => p.Contains(point));

    /// <summary>Rough size in square degrees, to prefer the most specific of overlapping areas.</summary>
    public double AreaDeg2 => Polygons.Sum(p => p.AreaDeg2);
}

public sealed record AtlasTown(string Name, string CountryIso2, GeoPoint Position, long Population, TownRank Rank);

/// <summary>One polygon: an outer ring and zero or more holes, each a flat [lon, lat, lon, lat…]
/// array, with the outer ring's bounding box for the cheap reject.</summary>
public sealed class AtlasPolygon
{
    private readonly IReadOnlyList<double[]> _rings;

    public AtlasPolygon(IReadOnlyList<double[]> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (rings.Count == 0 || rings[0].Length < 6)
        {
            throw new ArgumentException("A polygon needs an outer ring of at least three points.", nameof(rings));
        }

        _rings = rings;
        var outer = rings[0];
        double minLon = double.MaxValue, minLat = double.MaxValue, maxLon = double.MinValue, maxLat = double.MinValue;
        for (var i = 0; i < outer.Length; i += 2)
        {
            minLon = Math.Min(minLon, outer[i]);
            maxLon = Math.Max(maxLon, outer[i]);
            minLat = Math.Min(minLat, outer[i + 1]);
            maxLat = Math.Max(maxLat, outer[i + 1]);
        }

        MinLon = minLon;
        MaxLon = maxLon;
        MinLat = minLat;
        MaxLat = maxLat;
    }

    public double MinLon { get; }
    public double MaxLon { get; }
    public double MinLat { get; }
    public double MaxLat { get; }

    public double AreaDeg2 => (MaxLon - MinLon) * (MaxLat - MinLat);

    /// <summary>Even-odd ray casting: inside the outer ring and outside every hole. Good
    /// enough at atlas scale — nobody asks which side of a border fence they are on.</summary>
    public bool Contains(GeoPoint point)
    {
        var lon = point.LongitudeDeg;
        var lat = point.LatitudeDeg;
        if (lon < MinLon || lon > MaxLon || lat < MinLat || lat > MaxLat)
        {
            return false;
        }

        if (!InRing(_rings[0], lon, lat))
        {
            return false;
        }

        for (var i = 1; i < _rings.Count; i++)
        {
            if (InRing(_rings[i], lon, lat))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InRing(double[] ring, double x, double y)
    {
        var inside = false;
        var n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var xi = ring[2 * i];
            var yi = ring[(2 * i) + 1];
            var xj = ring[2 * j];
            var yj = ring[(2 * j) + 1];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
