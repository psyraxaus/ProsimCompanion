using System.Globalization;
using Serilog.Events;

namespace ProsimCompanion.App.Logging;

/// <summary>
/// The <c>log.warning</c> / <c>log.error</c> / <c>log.fatal</c> session-event payload:
/// enough to cluster and attribute a problem without the CMTrace file. camelCase when
/// serialized by the event log. <see cref="StackTop"/> is the first frames only — a whole
/// stack per event would dwarf the flight events around it.
/// </summary>
public sealed record SessionLogEventPayload(
    string Source,
    string Component,
    string Message,
    string? ExceptionType,
    string? ExceptionMessage,
    IReadOnlyList<string>? StackTop,
    int ThreadId);

/// <summary>
/// Pure Serilog-event-to-session-payload mapping, kept free of I/O so it is testable without
/// a logger pipeline (the sink around it only adds the guards and the level gate).
/// </summary>
public static class SessionLogEventFormatter
{
    /// <summary>How many stack frames <see cref="SessionLogEventPayload.StackTop"/> keeps.</summary>
    public const int StackFrames = 5;

    /// <summary>Source context used when a log event carries none.</summary>
    public const string DefaultSource = "ProsimCompanion";

    /// <summary>The session event name for a level: only Warning and above have one.</summary>
    public static string? EventName(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => "log.warning",
        LogEventLevel.Error => "log.error",
        LogEventLevel.Fatal => "log.fatal",
        _ => null,
    };

    /// <summary>The full source context of an event (the ILogger&lt;T&gt; type name).</summary>
    public static string Source(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string text }
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : DefaultSource;
    }

    public static SessionLogEventPayload ToPayload(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var source = Source(logEvent);
        var lastDot = source.LastIndexOf('.');
        var component = lastDot >= 0 && lastDot < source.Length - 1 ? source[(lastDot + 1)..] : source;

        var threadId = 0;
        if (logEvent.Properties.TryGetValue("ThreadId", out var threadValue)
            && threadValue is ScalarValue { Value: int id })
        {
            threadId = id;
        }

        var exception = logEvent.Exception;
        return new SessionLogEventPayload(
            source,
            component,
            logEvent.RenderMessage(CultureInfo.InvariantCulture),
            exception?.GetType().FullName,
            exception?.Message,
            StackTop(exception),
            threadId);
    }

    /// <summary>The first <see cref="StackFrames"/> "at ..." lines of the exception's own stack
    /// (not inner exceptions); null when there is no exception or it was never thrown.</summary>
    public static IReadOnlyList<string>? StackTop(Exception? exception)
    {
        var stack = exception?.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
        {
            return null;
        }

        var frames = stack
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(StackFrames)
            .ToList();
        return frames.Count == 0 ? null : frames;
    }
}
