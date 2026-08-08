using System.Text.Json;
using ProsimCompanion.Speech.SayIntentions;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Route identifiers the SayIntentions client publishes in flight.json — the middle
/// tier of the procedure source. All fields optional.</summary>
public sealed record FlightJsonRoute(
    string? CurrentAirport,
    string? Origin,
    string? Destination,
    string? DepartureRunway,
    string? ArrivalRunway,
    string? Sid,
    string? Star)
{
    public static FlightJsonRoute Empty { get; } = new(null, null, null, null, null, null, null);
}

/// <summary>
/// Reads the route fields from the SayIntentions flight.json (write-sharing read via
/// <see cref="FlightJsonFile"/> — never deny the client its own file). Fields live either at
/// the flight_details root or under current_flight depending on client version, so both are
/// probed. Never throws; a missing/torn file returns <see cref="FlightJsonRoute.Empty"/>.
/// </summary>
public static class FlightJsonRouteReader
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SayIntentionsAI", "flight.json");

    public static FlightJsonRoute Read() => Read(DefaultPath);

    public static FlightJsonRoute Read(string path)
    {
        if (!File.Exists(path))
        {
            return FlightJsonRoute.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(FlightJsonFile.ReadAllText(path));
            var root = doc.RootElement.TryGetProperty("flight_details", out var details)
                ? details
                : doc.RootElement;
            var current = root.TryGetProperty("current_flight", out var cf) ? cf : default;

            string? Str(string name)
            {
                foreach (var element in new[] { current, root })
                {
                    if (element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty(name, out var v)
                        && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                    {
                        return v.GetString()!.Trim();
                    }
                }

                return null;
            }

            return new FlightJsonRoute(
                Str("current_airport"),
                Str("flight_origin"),
                Str("flight_destination"),
                Str("flight_plan_departing_runway"),
                Str("flight_plan_arriving_runway"),
                Str("flight_plan_sid"),
                Str("flight_plan_star"));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return FlightJsonRoute.Empty; // mid-write / absent — next resolve retries
        }
    }
}
