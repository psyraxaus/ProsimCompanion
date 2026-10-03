using System.Globalization;

namespace ProsimCompanion.Core.Geo;

/// <summary>
/// The First Officer's deterministic "where we are" sentence from a <see cref="PlaceFix"/>
/// (issue #153) — spoken at once, before any model is asked, so the position itself never
/// waits on the LLM and never comes from it. Distances are rounded the way a pilot would say
/// them; compass points are eight-wind.
/// </summary>
public static class PlaceFixText
{
    /// <summary>Spoken when there is no position to look up.</summary>
    public const string NoPosition = "I don't have our position right now.";

    /// <summary>Spoken when the atlas has nothing at all for the position.</summary>
    public const string NothingNearby = "We're over open water, nothing on the chart for miles.";

    /// <summary>"We're over the Alps in northern Italy, about 30 miles north of Turin. Nearest
    /// town is Aosta, 12 miles out on the left."</summary>
    public static string Spoken(PlaceFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        if (fix.IsEmpty)
        {
            return NothingNearby;
        }

        var place = Place(fix);
        var reference = fix.Reference;
        var nearest = fix.Nearest;

        // Right over a town: the town and its country — "the North European Plain" adds
        // nothing to "right over Amsterdam".
        var countryOnly = fix.Country is null ? "" : (fix.CountryPart is { } cp ? cp + " " : "") + CountryName(fix.Country);
        string first;
        if (reference is { DistanceNm: < 3 })
        {
            first = $"We're right over {reference.Town.Name}" + (countryOnly.Length > 0 ? $", in {countryOnly}." : ".");
            reference = null;
        }
        else if (nearest is { DistanceNm: < 3 } && reference is null)
        {
            first = $"We're right over {nearest.Town.Name}" + (countryOnly.Length > 0 ? $", in {countryOnly}." : ".");
            nearest = null;
        }
        else if (place.Length > 0)
        {
            first = $"We're over {place}" + (reference is null ? "." : $", about {Miles(reference.DistanceNm)} {CompassFrom(reference.BearingDeg)} of {reference.Town.Name}.");
            reference = null;
        }
        else if (reference is not null)
        {
            first = $"We're about {Miles(reference.DistanceNm)} {CompassFrom(reference.BearingDeg)} of {reference.Town.Name}.";
            reference = null;
        }
        else
        {
            first = NothingNearby;
        }

        var second = nearest is null
            ? ""
            : $" Nearest town is {nearest.Town.Name}, {Miles(nearest.DistanceNm)} {SidePhrase(nearest.Side)}.";
        return first + second;
    }

    /// <summary>Names only, for the model's prompt: "northern France; the Alps; Turin, Aosta".</summary>
    public static string PlaceNames(PlaceFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        var parts = new List<string>();
        if (fix.Country is not null)
        {
            parts.Add((fix.CountryPart is { } part ? part + " " : "") + CountryName(fix.Country));
        }

        if (fix.Region is not null)
        {
            parts.Add(WithArticle(fix.Region));
        }

        if (fix.Sea is not null)
        {
            parts.Add(WithArticle(fix.Sea));
        }

        var towns = new[] { fix.Reference, fix.Nearest }.Where(t => t is not null).Select(t => t!.Town.Name).Distinct();
        parts.AddRange(towns);
        return string.Join("; ", parts);
    }

    /// <summary>The headline place: "the Alps in northern Italy", "northern France", "the North Sea".</summary>
    private static string Place(PlaceFix fix)
    {
        var country = fix.Country is null ? "" : (fix.CountryPart is { } part ? part + " " : "") + CountryName(fix.Country);
        if (fix.Region is not null)
        {
            var region = WithArticle(fix.Region);
            return country.Length > 0 ? $"{region} in {country}" : region;
        }

        if (country.Length > 0)
        {
            return country;
        }

        return fix.Sea is null ? "" : WithArticle(fix.Sea);
    }

    /// <summary>Countries that take "the" in speech, and the few whose atlas name is not what
    /// a pilot says ("United States of America" → "the United States").</summary>
    private static readonly Dictionary<string, string> SpokenCountries = new(StringComparer.Ordinal)
    {
        ["United States of America"] = "the United States",
        ["United Kingdom"] = "the United Kingdom",
        ["Netherlands"] = "the Netherlands",
        ["United Arab Emirates"] = "the United Arab Emirates",
        ["Philippines"] = "the Philippines",
        ["Bahamas"] = "the Bahamas",
        ["Gambia"] = "the Gambia",
        ["Maldives"] = "the Maldives",
        ["Seychelles"] = "the Seychelles",
        ["Comoros"] = "the Comoros",
        ["Dominican Republic"] = "the Dominican Republic",
        ["Central African Republic"] = "the Central African Republic",
        ["Democratic Republic of the Congo"] = "the Democratic Republic of the Congo",
        ["Republic of the Congo"] = "the Republic of the Congo",
        ["Czech Republic"] = "the Czech Republic",
        ["Ivory Coast"] = "the Ivory Coast",
        ["Marshall Islands"] = "the Marshall Islands",
        ["Solomon Islands"] = "the Solomon Islands",
        ["Falkland Islands"] = "the Falkland Islands",
        ["Faroe Islands"] = "the Faroe Islands",
        ["Isle of Man"] = "the Isle of Man",
        ["Western Sahara"] = "the Western Sahara",
        ["Federated States of Micronesia"] = "Micronesia",
        ["Vatican"] = "the Vatican",
    };

    /// <summary>The country as spoken.</summary>
    public static string CountryName(AtlasCountry country)
    {
        ArgumentNullException.ThrowIfNull(country);
        return SpokenCountries.TryGetValue(country.Name, out var spoken) ? spoken : country.Name;
    }

    /// <summary>"the Alps", "the North Sea", but "Lapland", "Florida", "Sicily" — named areas
    /// take "the" when they read as a feature (a plural, or a kind word in the name).</summary>
    public static string WithArticle(AtlasArea area)
    {
        var name = area.Name;
        if (name.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        // Physical features always take "the" (the Alps, the Sahara, the Altiplano, the North
        // Sea). Islands, peninsulas and geo-areas only when the name carries a kind word or is
        // a plural (the Arabian Peninsula, the Balkans) — Sicily, Florida, Lapland, Siberia do not.
        var feature = area.Kind is "Sea" or "Range/mtn" or "Desert" or "Plateau" or "Plain" or "Basin" or "Delta"
            or "Valley" or "Lowland" or "Gorge" or "Lake" or "Wetlands" or "Tundra";
        var isthmus = area.Kind == "Isthmus" && !name.Equals("Central America", StringComparison.Ordinal);
        if (feature || isthmus)
        {
            return "the " + name;
        }

        string[] kindWords = ["Peninsula", "Island", "Isles", "Archipelago", "Corridor", "subcontinent", "horn", "Shield"];
        var kindWord = name.Contains(' ', StringComparison.Ordinal) && kindWords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        var plural = name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal);
        return kindWord || plural ? "the " + name : name;
    }

    /// <summary>Distances as a pilot says them: under 10 to the mile, under 50 to 5, above to 10.</summary>
    public static string Miles(double nm)
    {
        var rounded = nm < 10 ? Math.Max(1, Math.Round(nm)) : nm < 50 ? Math.Round(nm / 5) * 5 : Math.Round(nm / 10) * 10;
        return rounded.ToString("0", CultureInfo.InvariantCulture) + (rounded == 1 ? " mile" : " miles");
    }

    /// <summary>Where WE are relative to the town: the town bears 320 from us, so we are
    /// south-east of it.</summary>
    public static string CompassFrom(double bearingToTownDeg) => Compass((bearingToTownDeg + 180) % 360);

    public static string Compass(double deg)
    {
        var d = ((deg % 360) + 360) % 360;
        var points = new[] { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        return points[(int)Math.Round(d / 45) % 8];
    }

    private static string SidePhrase(string? side) => side switch
    {
        "left" => "out on the left",
        "right" => "out on the right",
        "ahead" => "ahead of us",
        "behind" => "behind us",
        _ => "away",
    };
}
