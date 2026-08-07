using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.EventLog;

/// <summary>
/// Structured session record: one JSONL file per session under
/// <c>%LOCALAPPDATA%\ProsimCompanion\sessions\</c>, oldest files pruned beyond a retention cap.
/// Events are written by a single background writer fed from an unbounded channel, so callers
/// never block on disk I/O. This is the substrate for the future replay harness and debriefs
/// (pattern proven in Prosim2FO). Company day mode rotates in a fresh session per leg via
/// <see cref="StartNewSession"/> so each sector debriefs/logs cleanly.
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

    // Entries are strings (serialized lines) or RotateTo commands. Rotation rides the same
    // ordered channel as the lines, so everything recorded before StartNewSession lands in the
    // old file and everything after lands in the new one — no writer-side locking needed.
    private readonly Channel<object> _entries;
    private readonly Task _writer;
    private readonly string _sessionsDirectory;
    private readonly object _pathGate = new();

    // Every path this instance has used, including the current one — a same-second rotation
    // must never reuse a path whose file the async writer has not created yet.
    private readonly HashSet<string> _usedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _sessionFilePath;

    public JsonlEventLog(string sessionsDirectory, ILogger<JsonlEventLog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _sessionsDirectory = sessionsDirectory;
        Directory.CreateDirectory(sessionsDirectory);
        Prune(sessionsDirectory);

        _sessionFilePath = NewSessionPath();

        _entries = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

        // The loop's first file is captured HERE, not read from Path later: a rotation racing
        // the writer's startup must not make the loop skip the original file.
        var initialPath = _sessionFilePath;
        _writer = Task.Run(() => WriteLoopAsync(initialPath));
    }

    /// <summary>Path of the current session's log file (changes on <see cref="StartNewSession"/>).</summary>
    public string Path
    {
        get
        {
            lock (_pathGate)
            {
                return _sessionFilePath;
            }
        }
    }

    /// <summary>
    /// Closes the current session file and starts a fresh <c>session-&lt;timestamp&gt;.jsonl</c>;
    /// <see cref="Path"/> reflects the new file immediately. Thread-safe with the non-blocking
    /// writer: events already recorded drain to the old file, later ones to the new. Callers own
    /// the timing — company day mode rotates only at next-leg start, AFTER the finalization
    /// steps have read the completed leg's file (the predecessor rotated on a bare delay and
    /// could yank the file out from under a slow debrief). Never throws.
    /// </summary>
    public void StartNewSession()
    {
        try
        {
            string newPath;
            lock (_pathGate)
            {
                newPath = NewSessionPath();
                _sessionFilePath = newPath;
                _entries.Writer.TryWrite(new RotateTo(newPath));
            }

            _logger.LogInformation("Event log rotated to {Session}", System.IO.Path.GetFileName(newPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event log session rotation failed — continuing on the current file");
        }
    }

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
            _entries.Writer.TryWrite(line);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Event {EventType} could not be serialized for the session log", type);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _entries.Writer.TryComplete();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session event log writer did not shut down cleanly");
        }
    }

    private async Task WriteLoopAsync(string initialPath)
    {
        StreamWriter? writer = null;
        try
        {
            writer = TryOpen(initialPath);
            await foreach (var entry in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (entry is RotateTo rotate)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    writer = TryOpen(rotate.Path);
                    continue;
                }

                if (writer is null)
                {
                    continue; // open failed (already logged) — drop rather than block callers
                }

                await writer.WriteLineAsync((string)entry).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Session event log writer failed; further events from this session are lost");
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private StreamWriter? TryOpen(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not open session event log {Path}; its events are lost", path);
            return null;
        }
    }

    /// <summary>A fresh timestamped file path, suffixed when a same-second rotation (or a
    /// pre-existing file on disk) would collide. Registers the path as used.</summary>
    private string NewSessionPath()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = System.IO.Path.Combine(_sessionsDirectory, $"session-{stamp}.jsonl");
        for (var n = 2; _usedPaths.Contains(candidate) || File.Exists(candidate); n++)
        {
            candidate = System.IO.Path.Combine(_sessionsDirectory, $"session-{stamp}-{n}.jsonl");
        }

        _usedPaths.Add(candidate);
        return candidate;
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

    private sealed record RotateTo(string Path);

    private sealed record EventEnvelope(DateTimeOffset Timestamp, string Type, object? Payload);
}
