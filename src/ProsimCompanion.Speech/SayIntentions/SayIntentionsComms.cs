using System.Text.Json;
using ProsimCompanion.Core.Radios;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// Tolerant reader for the getWX <c>with_comms=1</c> station list — <c>comms[]</c> entries
/// of <c>{type, freq, callsign?}</c>, at the root, one level down, or inside the first
/// <c>airports[]</c> entry (the predecessor searched the first two; the SAPI docs show no
/// sample). Entries without a type or an in-band frequency are dropped; a malformed body
/// yields an empty list rather than an exception. Pure.
/// </summary>
internal static class SayIntentionsComms
{
    public static IReadOnlyList<AtcStation> Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!TryFindArray(doc.RootElement, "comms", out var comms))
            {
                return [];
            }

            var stations = new List<AtcStation>();
            foreach (var entry in comms.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = Str(entry, "type")?.Trim().ToUpperInvariant() ?? "";
                var khz = RadioFrequency.ToKhz(Str(entry, "freq"));
                if (type.Length == 0 || khz is null)
                {
                    continue;
                }

                stations.Add(new AtcStation(type, Str(entry, "callsign")?.Trim() ?? "", khz.Value));
            }

            return stations;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryFindArray(JsonElement root, string name, out JsonElement array)
    {
        array = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
