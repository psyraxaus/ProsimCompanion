using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Airports;

/// <summary>
/// Airport names from the user-editable
/// <c>%LOCALAPPDATA%\ProsimCompanion\config\airport-names.json</c> (owner decision
/// 2026-11-14: the FO names an airport instead of spelling its ICAO code, and that must work
/// without a Navigraph database). The shipped file is derived from the OpenFlights airport
/// database under the ODbL (THIRD_PARTY.md) and carries the short forms crews say for the
/// big fields ("Heathrow", "Schiphol"). It is the FIRST tier of <see cref="IAirportNames"/> —
/// the pilot's own wording beats the DFD's formal name — and an airport it does not list
/// falls through to the next tier. Loaded on first use; a missing or unreadable file answers
/// nothing, never a failure.
/// </summary>
public sealed class AirportNameCatalog : IAirportNames
{
    /// <summary>The user-editable file under the user config root.</summary>
    public const string FileName = "airport-names.json";

    private readonly Lazy<Dictionary<string, string>> _names;

    public AirportNameCatalog(ILogger<AirportNameCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _names = new Lazy<Dictionary<string, string>>(() => LoadFile(logger));
    }

    private AirportNameCatalog(Dictionary<string, string> names) => _names = new Lazy<Dictionary<string, string>>(names);

    /// <summary>A catalogue over the given file text — the test seam.</summary>
    public static AirportNameCatalog FromJson(string? json) => new(Parse(json));

    /// <inheritdoc />
    public string? SpokenName(string? icao)
        => !string.IsNullOrWhiteSpace(icao) && _names.Value.TryGetValue(icao.Trim(), out var name) ? name : null;

    /// <summary>Pure parse of the file text (<c>{"airports":[{icao,name,city}]}</c>): ICAO →
    /// the name to say. An entry with a blank name says its city; one with neither is
    /// dropped; the first entry wins per ICAO. Empty on any problem.</summary>
    public static Dictionary<string, string> Parse(string? json)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            var file = JsonSerializer.Deserialize<AirportFile>(json, Options);
            foreach (var airport in file?.Airports ?? [])
            {
                var icao = (airport.Icao ?? "").Trim();
                var name = string.IsNullOrWhiteSpace(airport.Name) ? (airport.City ?? "").Trim() : airport.Name.Trim();
                if (icao.Length == 4 && name.Length > 0)
                {
                    names.TryAdd(icao, name);
                }
            }
        }
        catch (JsonException)
        {
            names.Clear();
        }

        return names;
    }

    private static Dictionary<string, string> LoadFile(ILogger logger)
    {
        try
        {
            var path = UserConfigPaths.File(FileName);
            if (!File.Exists(path))
            {
                logger.LogInformation("{File} not found — airport names come from the nav database only", FileName);
                return Parse(null);
            }

            var names = Parse(File.ReadAllText(path));
            if (names.Count == 0)
            {
                logger.LogWarning("{File} has no usable airports — airport names come from the nav database only", FileName);
            }
            else
            {
                logger.LogInformation("Airport names loaded: {Count} airports", names.Count);
            }

            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "{File} load failed — airport names come from the nav database only", FileName);
            return Parse(null);
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class AirportFile
    {
        [JsonPropertyName("airports")]
        public List<AirportJson>? Airports { get; set; }
    }

    private sealed class AirportJson
    {
        [JsonPropertyName("icao")]
        public string? Icao { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }
    }
}

/// <summary>The first tier that knows the airport answers: the pilot's
/// <see cref="AirportNameCatalog"/>, then the nav database. A tier that throws is skipped —
/// <see cref="IAirportNames.SpokenName"/> must never throw.</summary>
public sealed class AirportNameChain : IAirportNames
{
    private readonly IReadOnlyList<IAirportNames> _tiers;

    public AirportNameChain(IReadOnlyList<IAirportNames> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        _tiers = tiers;
    }

    /// <inheritdoc />
    public string? SpokenName(string? icao)
    {
        foreach (var tier in _tiers)
        {
            if (tier.SpokenName(icao) is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }
}
