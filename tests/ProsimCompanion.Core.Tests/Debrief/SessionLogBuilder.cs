using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProsimCompanion.Core.Tests.Debrief;

/// <summary>
/// Builds a synthetic session JSONL file with this codebase's real envelope shape
/// ({ timestamp, type, payload }) so extractor/logbook tests control every timestamp —
/// something the live <c>JsonlEventLog</c> (which stamps UtcNow) cannot do.
/// </summary>
internal sealed class SessionLogBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly StringBuilder _lines = new();
    private DateTimeOffset _clock = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    public SessionLogBuilder At(string time)
    {
        _clock = DateTimeOffset.Parse("2026-08-08T" + time + "Z", CultureInfo.InvariantCulture);
        return this;
    }

    public SessionLogBuilder Event(string type, object? payload = null)
    {
        var line = JsonSerializer.Serialize(new { timestamp = _clock, type, payload }, SerializerOptions);
        _lines.AppendLine(line);
        return this;
    }

    public SessionLogBuilder Phase(string from, string to, double? iasKt = null, double? groundSpeedKt = null)
        => Event("phase-changed", new
        {
            previous = from,
            current = to,
            snapshot = iasKt is null && groundSpeedKt is null
                ? null
                : (object)new { iasKt = iasKt ?? 0, groundSpeedKt = groundSpeedKt ?? 0 },
        });

    public SessionLogBuilder RawLine(string line)
    {
        _lines.AppendLine(line);
        return this;
    }

    /// <summary>Writes the log as <paramref name="sessionId"/>.jsonl in <paramref name="directory"/>.</summary>
    public string Write(string directory, string sessionId = "session-20260808-100000")
    {
        var path = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(path, _lines.ToString());
        return path;
    }
}
