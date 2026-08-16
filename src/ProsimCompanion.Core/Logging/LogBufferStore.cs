namespace ProsimCompanion.Core.Logging;

/// <summary>One captured log event for the in-memory buffer / web Logs page.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string? Exception);

/// <summary>
/// Bounded in-memory ring buffer of recent log events, feeding the web Logs page. Thread-safe;
/// consumers poll <see cref="Snapshot"/> (no per-event fan-out — logging must stay cheap).
/// </summary>
public sealed class LogBufferStore
{
    public const int Capacity = 2000;

    private readonly Collections.BoundedLog<LogEntry> _entries = new(Capacity);
    private long _warningCount;
    private long _errorCount;

    /// <summary>Warnings seen this session (including those evicted from the buffer).</summary>
    public long WarningCount => Interlocked.Read(ref _warningCount);

    /// <summary>Errors/fatals seen this session (including those evicted from the buffer).</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public void Add(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Level is "Warning")
        {
            Interlocked.Increment(ref _warningCount);
        }
        else if (entry.Level is "Error" or "Fatal")
        {
            Interlocked.Increment(ref _errorCount);
        }

        _entries.Add(entry);
    }

    /// <summary>Newest-first copy of the buffered entries.</summary>
    public IReadOnlyList<LogEntry> Snapshot() => _entries.Snapshot();
}
