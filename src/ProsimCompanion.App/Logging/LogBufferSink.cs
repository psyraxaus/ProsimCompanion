using System.Globalization;
using ProsimCompanion.Core.Logging;
using Serilog.Core;
using Serilog.Events;

namespace ProsimCompanion.App.Logging;

/// <summary>Feeds every emitted log event into the in-memory <see cref="LogBufferStore"/> that
/// backs the web Logs page.</summary>
public sealed class LogBufferSink : ILogEventSink
{
    private readonly LogBufferStore _store;

    public LogBufferSink(LogBufferStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var source = logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string text }
                ? text
                : "ProsimCompanion";

        _store.Add(new LogEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            source,
            logEvent.RenderMessage(CultureInfo.InvariantCulture),
            logEvent.Exception?.ToString()));
    }
}
