using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using Serilog.Core;
using Serilog.Events;

namespace ProsimCompanion.App.Logging;

/// <summary>
/// Where the sink finds the session event log. The Serilog logger is built before the host
/// (so startup itself is logged), while the event log is a DI singleton — the composition
/// root sets <see cref="Current"/> once the container is up. Events raised before that are
/// dropped, not queued: they are the startup lines, which the CMTrace file still has.
/// </summary>
public sealed class SessionEventLogTarget
{
    private volatile JsonlEventLog? _current;

    /// <summary>The event log to mirror into, or null while the host is not built yet.</summary>
    public JsonlEventLog? Current
    {
        get => _current;
        set => _current = value;
    }
}

/// <summary>
/// Mirrors Warning-and-above log events into the session JSONL as <c>log.warning</c> /
/// <c>log.error</c> / <c>log.fatal</c> events, so the session file is the single evidence
/// stream for triage: an error lands in order between the flight events instead of having
/// to be timestamp-joined against the CMTrace file. Information is never mirrored — the
/// per-second flight sample plus the chatty subsystems would double the file.
/// </summary>
/// <remarks>
/// Two re-entrancy guards: <see cref="JsonlEventLog"/> logs its own failures through ILogger
/// (a failed Record, a rotation that could not open its file), so events from that source
/// are skipped by name; and a thread-static "in sink" flag means anything logged while
/// <see cref="JsonlEventLog.Record"/> runs on this thread can never recurse back in.
/// </remarks>
public sealed class SessionEventLogSink : ILogEventSink
{
    /// <summary>Source context of the event log itself — its warnings must never be mirrored.</summary>
    public static readonly string EventLogSource = typeof(JsonlEventLog).FullName!;

    [ThreadStatic]
    private static bool _inSink;

    private readonly SessionEventLogTarget _target;
    private readonly Func<LogMirrorLevel> _mirrorLevel;

    /// <param name="target">Holder the composition root fills once the host exists.</param>
    /// <param name="mirrorLevel">Live option read (hot-reloaded with the logging section).</param>
    public SessionEventLogSink(SessionEventLogTarget target, Func<LogMirrorLevel> mirrorLevel)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mirrorLevel);
        _target = target;
        _mirrorLevel = mirrorLevel;
    }

    /// <summary>Pure gate: does <paramref name="level"/> pass <paramref name="mirror"/>?</summary>
    public static bool ShouldMirror(LogEventLevel level, LogMirrorLevel mirror) => mirror switch
    {
        LogMirrorLevel.WarningAndAbove => level >= LogEventLevel.Warning,
        LogMirrorLevel.ErrorAndAbove => level >= LogEventLevel.Error,
        _ => false,
    };

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (_inSink)
        {
            return;
        }

        // The guard is raised before anything else runs, so an option read, a target read or
        // the Record itself logging on this thread can never recurse back in.
        _inSink = true;
        try
        {
            if (!ShouldMirror(logEvent.Level, _mirrorLevel()))
            {
                return;
            }

            var eventName = SessionLogEventFormatter.EventName(logEvent.Level);
            if (eventName is null
                || string.Equals(SessionLogEventFormatter.Source(logEvent), EventLogSource, StringComparison.Ordinal))
            {
                return;
            }

            var eventLog = _target.Current;
            if (eventLog is null)
            {
                return;
            }

            eventLog.Record(eventName, SessionLogEventFormatter.ToPayload(logEvent));
        }
        catch (Exception)
        {
            // A sink must never throw into the logging pipeline; Record already swallows its
            // own failures, so anything reaching here is a formatting surprise — drop the event.
        }
        finally
        {
            _inSink = false;
        }
    }
}
