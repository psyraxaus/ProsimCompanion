using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Gsx.Profiles;

/// <summary>One customised stand of a GSX airport <c>.ini</c> profile: its scenery identity
/// (from the section header <c>[gate w 40]</c>) and the raw key/value pairs beneath it.</summary>
public sealed record GsxIniStand(ParkingIdentity Identity, string? MarsId, IReadOnlyDictionary<string, string> Values)
{
    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public double? GetDouble(string key)
        => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public int? GetInt(string key)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public bool? GetBool(string key) => GetInt(key) switch { null => null, 0 => false, _ => true };

    /// <summary>"lat lon heading" triples such as <c>this_parking_pos</c> / <c>pushbackleftpos</c>
    /// (a fourth number — a scale — is ignored; a two-number value has no heading and is skipped).</summary>
    public GeoPose? GetPose(string key)
    {
        var parts = (Get(key) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var hdg))
        {
            return null;
        }

        return GeoPose.FromRaw(lat, lon, hdg);
    }

    /// <summary>Comma-separated lists (<c>airlinecodes</c>, <c>handlingtexture</c>).</summary>
    public IReadOnlyList<string> GetList(string key)
        => (Get(key) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}

/// <summary>A parsed GSX airport <c>.ini</c> profile.</summary>
public sealed record GsxIniProfile(IReadOnlyDictionary<string, string> General, IReadOnlyList<GsxIniStand> Stands)
{
    public static readonly GsxIniProfile Empty = new(new Dictionary<string, string>(), []);
}

/// <summary>
/// Pure parser for GSX airport profiles (manual p.68: "plain standard .INI files, with a general
/// section and multiple sections for each parking that has been customized"). Section headers
/// name the stand by scenery identity, lower-case: <c>[gate 17]</c>, <c>[gate w 40]</c>,
/// <c>[gate s 45a]</c>, <c>[parking 401]</c>, <c>[n parking 1w]</c>, <c>[none 101]</c>,
/// <c>[gate a 252_mars_252a]</c> (a MARS sub-position). De-ice areas, jetway tables and anything
/// else that is not a parking name are kept out of <see cref="GsxIniProfile.Stands"/>.
/// Keys are case-insensitive; values keep their text — the stand record decodes on demand.
/// </summary>
public static class GsxIniProfileParser
{
    private static readonly Regex StandHeader = new(
        @"^(?<words>[a-z]+(?: [a-z]+)*?) (?<number>\d{1,5})(?<suffix>[a-z]{0,2})(?:_mars_(?<mars>[a-z0-9]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static GsxIniProfile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stands = new List<GsxIniStand>();
        Dictionary<string, string>? current = null;
        ParkingIdentity? currentIdentity = null;
        string? currentMars = null;

        void Flush()
        {
            if (current is not null && currentIdentity is not null)
            {
                stands.Add(new GsxIniStand(currentIdentity, currentMars, current));
            }
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                var header = line[1..^1].Trim().ToLowerInvariant();
                current = null;
                currentIdentity = null;
                currentMars = null;

                if (header == "general")
                {
                    current = general;
                    continue;
                }

                var match = StandHeader.Match(header);
                if (match.Success && ParkingIdentity.ParseIniNameWords(match.Groups["words"].Value) is { } name)
                {
                    currentIdentity = new ParkingIdentity(
                        name,
                        int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture),
                        match.Groups["suffix"].Value.ToUpperInvariant());
                    currentMars = match.Groups["mars"].Success ? match.Groups["mars"].Value.ToUpperInvariant() : null;
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        Flush();
        return new GsxIniProfile(general, stands);
    }

    /// <summary>GSX's default labels for the two automatic slots (manual p.23).</summary>
    public const string DefaultLeftLabel = "Nose Right/Tail Left (LEFT)";
    public const string DefaultRightLabel = "Nose Left/Tail Right (RIGHT)";

    /// <summary>Builds the stand's pushback knowledge from the ini keys <c>pushback</c>,
    /// <c>pushbacklabels</c> (left|right), <c>pushbackleftpos</c> / <c>pushbackrightpos</c>
    /// (final lat lon heading) and <c>pushbackaddpos</c> (Python list of extra slots).</summary>
    public static ParkingPushback? ReadPushback(GsxIniStand stand)
    {
        ArgumentNullException.ThrowIfNull(stand);
        var allowedCode = stand.GetInt("pushback");
        var labels = stand.Get("pushbacklabels");
        var leftPose = stand.GetPose("pushbackleftpos");
        var rightPose = stand.GetPose("pushbackrightpos");
        var add = stand.Get("pushbackaddpos");
        if (allowedCode is null && labels is null && leftPose is null && rightPose is null && string.IsNullOrEmpty(add))
        {
            return null;
        }

        var allowed = allowedCode is >= 0 and <= 3 ? (PushbackDirections)allowedCode.Value : PushbackDirections.Both;
        var labelParts = (labels ?? "").Split('|');
        var leftLabel = labelParts.Length > 0 && !string.IsNullOrWhiteSpace(labelParts[0]) ? labelParts[0].Trim() : DefaultLeftLabel;
        var rightLabel = labelParts.Length > 1 && !string.IsNullOrWhiteSpace(labelParts[1]) ? labelParts[1].Trim() : DefaultRightLabel;

        var slots = new List<PushbackSlot>();
        if (allowed is PushbackDirections.Left or PushbackDirections.Both)
        {
            slots.Add(new PushbackSlot(leftLabel, PushbackSlotKind.Left, leftPose));
        }
        if (allowed is PushbackDirections.Right or PushbackDirections.Both)
        {
            slots.Add(new PushbackSlot(rightLabel, PushbackSlotKind.Right, rightPose));
        }

        if (!string.IsNullOrWhiteSpace(add) && PyLiteral.Parse(add) is List<object?> extra)
        {
            foreach (var item in extra.OfType<Dictionary<string, object?>>())
            {
                var label = item.TryGetValue("label", out var l) ? l as string : null;
                GeoPose? pose = null;
                if (item.TryGetValue("pos", out var p) && p is List<object?> { Count: >= 3 } pos
                    && pos[0] is double lat && pos[1] is double lon && pos[2] is double hdg)
                {
                    pose = GeoPose.FromRaw(lat, lon, hdg);
                }

                if (!string.IsNullOrWhiteSpace(label))
                {
                    slots.Add(new PushbackSlot(label.Trim(), PushbackSlotKind.Additional, pose));
                }
            }
        }

        return new ParkingPushback(allowed, slots);
    }

    /// <summary>Converts one ini stand into the shared parking record.</summary>
    public static AirportParking ToParking(GsxIniStand stand)
    {
        ArgumentNullException.ThrowIfNull(stand);
        return new AirportParking(
            stand.Identity,
            GsxUiName: null,
            GsxGateName: null,
            TypeCode: stand.GetInt("type"),
            Pose: stand.GetPose("this_parking_pos"),
            RadiusM: null,
            RadiusLeftM: stand.GetDouble("radiusleft"),
            RadiusRightM: stand.GetDouble("radiusright"),
            MaxWingspanM: stand.GetDouble("maxwingspan"),
            HasJetway: stand.GetBool("hasjetway"),
            AirlineCodes: stand.GetList("airlinecodes"),
            HandlingOperators: stand.GetList("handlingtexture"),
            ParkingSystem: NullIfEmpty(stand.Get("parkingsystem")),
            Pushback: ReadPushback(stand),
            Sources: ParkingDataSources.GsxIni);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
