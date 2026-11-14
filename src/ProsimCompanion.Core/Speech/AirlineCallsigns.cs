using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Speech;

/// <summary>One airline of <c>airline-callsigns.json</c>.</summary>
/// <param name="Icao">Three-letter ICAO designator ("BAW"), or empty.</param>
/// <param name="Iata">Two-character IATA designator ("BA"), or empty.</param>
/// <param name="Callsign">The telephony designator as it is said ("Speedbird").</param>
public sealed record AirlineCallsignEntry(string Icao, string Iata, string Callsign);

/// <summary>
/// A flight number the way a crew says it (owner decision 2026-11-14): "BA124" / "BAW124" →
/// "Speedbird one two four". The airline's radio callsign comes from the user-editable
/// <c>%LOCALAPPDATA%\ProsimCompanion\config\airline-callsigns.json</c> (seeded from the shipped
/// file, which is derived from the OpenFlights airline database under the ODbL — see
/// THIRD_PARTY.md); an airline that is not listed is spelled by its letters ("B A one two
/// four"). With SayIntentions running the callsign on the RADIO is SayIntentions' own — this
/// is for the lines that only have a flight number. Loaded on first use; a missing or
/// unreadable file leaves every airline spelled, never a failure.
/// </summary>
public sealed partial class AirlineCallsigns
{
    /// <summary>The user-editable file under the user config root.</summary>
    public const string FileName = "airline-callsigns.json";

    private readonly Lazy<Lookup> _lookup;

    public AirlineCallsigns(ILogger<AirlineCallsigns> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _lookup = new Lazy<Lookup>(() => LoadFile(logger));
    }

    private AirlineCallsigns(Lookup lookup) => _lookup = new Lazy<Lookup>(lookup);

    /// <summary>A catalogue over the given file text — the test seam.</summary>
    public static AirlineCallsigns FromJson(string? json) => new(new Lookup(Parse(json)));

    // ICAO form first ("BAW124"), then IATA ("BA124", "U2123"); a letter suffix is allowed.
    [GeneratedRegex(@"^(?<airline>[A-Z]{3})(?<number>\d{1,4}[A-Z]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IcaoFlight();

    [GeneratedRegex(@"^(?<airline>[A-Z][A-Z0-9]|[0-9][A-Z])(?<number>\d{1,4}[A-Z]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IataFlight();

    /// <summary>The radio callsign for an ICAO ("BAW") or IATA ("BA") airline code; null when
    /// the airline is not listed.</summary>
    public string? Callsign(string? airlineCode)
    {
        if (string.IsNullOrWhiteSpace(airlineCode))
        {
            return null;
        }

        var code = airlineCode.Trim().ToUpperInvariant();
        var lookup = _lookup.Value;
        return code.Length == 3 && lookup.ByIcao.TryGetValue(code, out var byIcao) ? byIcao
            : code.Length == 2 && lookup.ByIata.TryGetValue(code, out var byIata) ? byIata
            : null;
    }

    /// <summary>"BA124" → "Speedbird one two four"; an unlisted airline → "B A one two four";
    /// text that is not a flight number → its identifier spelling; blank → "".</summary>
    public string Speak(string? flightNumber)
    {
        if (string.IsNullOrWhiteSpace(flightNumber))
        {
            return "";
        }

        var compact = string.Concat(flightNumber.Trim().ToUpperInvariant().Where(char.IsAsciiLetterOrDigit));
        var match = IcaoFlight().Match(compact);
        if (!match.Success)
        {
            match = IataFlight().Match(compact);
        }

        if (!match.Success)
        {
            return NatoPhonetics.SpeakIdentifier(flightNumber);
        }

        var airline = match.Groups["airline"].Value;
        var name = Callsign(airline) ?? string.Join(" ", airline.Select(c => c is >= '0' and <= '9' ? NatoPhonetics.DigitWords[c - '0'] : c.ToString()));
        return name + " " + Phraseology.Spell(match.Groups["number"].Value, " ");
    }

    /// <summary>Pure parse of the file text (<c>{"airlines":[{icao,iata,callsign}]}</c>);
    /// empty on any problem. Entries without a callsign or without any code are dropped.</summary>
    public static IReadOnlyList<AirlineCallsignEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<AirlineFile>(json, Options);
            return file?.Airlines is { Count: > 0 } airlines
                ? [.. airlines
                    .Where(a => !string.IsNullOrWhiteSpace(a.Callsign)
                        && (!string.IsNullOrWhiteSpace(a.Icao) || !string.IsNullOrWhiteSpace(a.Iata)))
                    .Select(a => new AirlineCallsignEntry(
                        (a.Icao ?? "").Trim().ToUpperInvariant(),
                        (a.Iata ?? "").Trim().ToUpperInvariant(),
                        a.Callsign!.Trim()))]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Lookup LoadFile(ILogger logger)
    {
        try
        {
            var path = UserConfigPaths.File(FileName);
            if (!File.Exists(path))
            {
                logger.LogInformation("{File} not found — airlines are spelled by their letters", FileName);
                return new Lookup([]);
            }

            var entries = Parse(File.ReadAllText(path));
            if (entries.Count == 0)
            {
                logger.LogWarning("{File} has no usable airlines — airlines are spelled by their letters", FileName);
            }
            else
            {
                logger.LogInformation("Airline callsigns loaded: {Count} airlines", entries.Count);
            }

            return new Lookup(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "{File} load failed — airlines are spelled by their letters", FileName);
            return new Lookup([]);
        }
    }

    /// <summary>First entry wins per code — the file's order is the pilot's priority.</summary>
    private sealed class Lookup
    {
        public Lookup(IReadOnlyList<AirlineCallsignEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Icao.Length == 3)
                {
                    ByIcao.TryAdd(entry.Icao, entry.Callsign);
                }

                if (entry.Iata.Length == 2)
                {
                    ByIata.TryAdd(entry.Iata, entry.Callsign);
                }
            }
        }

        public Dictionary<string, string> ByIcao { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ByIata { get; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class AirlineFile
    {
        [JsonPropertyName("airlines")]
        public List<AirlineJson>? Airlines { get; set; }
    }

    private sealed class AirlineJson
    {
        [JsonPropertyName("icao")]
        public string? Icao { get; set; }

        [JsonPropertyName("iata")]
        public string? Iata { get; set; }

        [JsonPropertyName("callsign")]
        public string? Callsign { get; set; }
    }
}
