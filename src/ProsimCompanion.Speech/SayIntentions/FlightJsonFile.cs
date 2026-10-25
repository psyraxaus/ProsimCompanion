using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// Tolerant reader for the SayIntentions client's flight.json. File.ReadAllText opens with
/// FileShare.Read, which denies the SayIntentions client WRITE access to its own file for the
/// duration of the read — at the service's 1 Hz poll that contention window recurs every
/// second and the client sees its file as locked. All flight.json reads go through here:
/// share ReadWrite+Delete so our read never denies the writer anything, and no handle
/// outlives the call.
/// </summary>
internal static class FlightJsonFile
{
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The API key SayIntentions keeps in flight.json (it survives between flights),
    /// or null when the file is absent, mid-write or malformed.</summary>
    public static string? TryReadApiKey()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SayIntentionsAI", "flight.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var root = JsonNode.Parse(ReadAllText(path));
            var apiKey = (root?["flight_details"] ?? root)?["api_key"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
    /// <summary>What the FO needs to work the radios (owner request 2026-10-25): the callsign as
    /// SayIntentions speaks it, the filed SID / STAR and runways, the assigned gate. Null when the
    /// file is absent, mid-write or malformed; IsActive false when no flight is filed.</summary>
    public static FlightRadioContext? TryReadRadioContext()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SayIntentionsAI", "flight.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var parsed = JsonNode.Parse(ReadAllText(path));
            var root = parsed?["flight_details"] ?? parsed;
            var current = root?["current_flight"];
            var callsign = Text(root?["callsign"]) ?? Text(root?["callsign_icao"]);
            return new FlightRadioContext(
                IsActive: callsign is not null,
                Callsign: callsign,
                Sid: Text(current?["flight_plan_sid"]),
                Star: Text(current?["flight_plan_star"]),
                DepartureRunway: Text(current?["flight_plan_departing_runway"]),
                ArrivalRunway: Text(current?["flight_plan_arriving_runway"]),
                Gate: Text(current?["assigned_gate"]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            return null;
        }

        static string? Text(JsonNode? node)
        {
            if (node is null)
            {
                return null;
            }

            string? value;
            try
            {
                value = node.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                value = node.ToString();
            }

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}

/// <summary>flight.json's radio-relevant facts (see <see cref="FlightJsonFile.TryReadRadioContext"/>).</summary>
internal sealed record FlightRadioContext(
    bool IsActive,
    string? Callsign,
    string? Sid,
    string? Star,
    string? DepartureRunway,
    string? ArrivalRunway,
    string? Gate);
