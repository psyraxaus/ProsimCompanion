using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>The one JSON shape of a diagnostics snapshot: camelCase, enums as their names,
/// nulls omitted. The API endpoint, the support bundle and the reducer all use it, so a
/// snapshot written by one build reads back in another without a translation table.</summary>
public static class EcamJson
{
    /// <summary>Serializer options for <see cref="DiagnosticsSnapshot"/> and <see cref="DiagEvent"/>.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>The snapshot as one JSON document.</summary>
    public static string Serialize(DiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    /// <summary>One event per line, oldest first, for <c>diagnostics-events.jsonl</c>.</summary>
    public static IReadOnlyList<string> EventLines(IReadOnlyList<DiagEvent> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        var lines = new List<string>(newestFirst.Count);
        for (var i = newestFirst.Count - 1; i >= 0; i--)
        {
            lines.Add(JsonSerializer.Serialize(newestFirst[i], Options));
        }

        return lines;
    }
}
