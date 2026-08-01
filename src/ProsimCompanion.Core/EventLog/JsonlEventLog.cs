using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.EventLog;

/// <summary>
/// Structured session record: one JSONL file per application session under
/// <c>%LOCALAPPDATA%\ProsimCompanion\sessions\</c>, oldest files pruned beyond a retention cap.
/// Events are written by a single background writer fed from an unbounded channel, so callers
/// never block on disk I/O. This is the substrate for the future replay harness and debriefs
/// (pattern proven in Prosim2FO).
/// </summary>
public sealed class JsonlEventLog : IAsyncDisposable
{
    private const int RetainedSessions = 20;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<JsonlEventLog> _logger;
    private readonly Channel<string> _lines;
    private readonly Task _writer;
    private readonly string _sessionFilePath;

    public JsonlEventLog(string sessionsDirectory, ILogger<JsonlEventLog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        Directory.CreateDirectory(sessionsDirectory);
        Prune(sessionsDirectory);

        _sessionFilePath = System.IO.Path.Combine(
            sessionsDirectory,
            $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Path of this session's log file.</summary>
    public string Path => _sessionFilePath;

    /// <summary>
    /// Records an event. <paramref name="type"/> is a short kebab-case event name
    /// (e.g. "phase-changed", "prosim-connected"); <paramref name="payload"/> is serialized
    /// verbatim (camelCase). Never throws — a failing event log must not affect features.
    /// </summary>
    public void Record(string type, object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(
                new EventEnvelope(DateTimeOffset.UtcNow, type, payload),
                SerializerOptions);
            _lines.Writer.TryWrite(line);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Event {EventType} could not be serialized for the session log", type);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session event log writer did not shut down cleanly");
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await using var stream = new FileStream(
                _sessionFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            await using var writer = new StreamWriter(stream);

            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Session event log writer failed; events from this session are lost");
        }
    }

    private void Prune(string directory)
    {
        try
        {
            var stale = Directory.GetFiles(directory, "session-*.jsonl")
                .OrderByDescending(File.GetCreationTimeUtc)
                .Skip(RetainedSessions - 1);
            foreach (var file in stale)
            {
                File.Delete(file);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not prune old session logs");
        }
    }

    private sealed record EventEnvelope(DateTimeOffset Timestamp, string Type, object? Payload);
}
