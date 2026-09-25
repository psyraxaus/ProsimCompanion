using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.App.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logging;

/// <summary>
/// The Warning+ mirror into the session JSONL: the pure formatter, the level gate, and the
/// two re-entrancy guards (the event log's own warnings are skipped by source; a nested
/// emit on the same thread is dropped).
/// </summary>
public sealed class SessionEventLogSinkTests : IDisposable
{
    private static readonly MessageTemplateParser Parser = new();

    private readonly string _dir = Directory.CreateTempSubdirectory("pc-logsink-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static LogEvent Event(
        LogEventLevel level,
        string template,
        string? source = "ProsimCompanion.Gsx.GsxClient",
        Exception? exception = null,
        int threadId = 7,
        params object[] args)
    {
        var parsed = Parser.Parse(template);
        var properties = new List<LogEventProperty>();
        if (source is not null)
        {
            properties.Add(new LogEventProperty("SourceContext", new ScalarValue(source)));
        }

        properties.Add(new LogEventProperty("ThreadId", new ScalarValue(threadId)));
        var names = parsed.Tokens.OfType<PropertyToken>().Select(t => t.PropertyName).ToList();
        for (var i = 0; i < names.Count && i < args.Length; i++)
        {
            properties.Add(new LogEventProperty(names[i], new ScalarValue(args[i])));
        }

        return new LogEvent(DateTimeOffset.UtcNow, level, exception, parsed, properties);
    }

    private static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void ToPayload_RendersMessage_SplitsComponent_AndKeepsTopFrames()
    {
        var logEvent = Event(
            LogEventLevel.Error, "Gate {Gate} refused after {Attempts} attempts",
            exception: Thrown(), args: ["A12", 3]);

        var payload = SessionLogEventFormatter.ToPayload(logEvent);

        Assert.Equal("ProsimCompanion.Gsx.GsxClient", payload.Source);
        Assert.Equal("GsxClient", payload.Component);
        Assert.Equal("Gate \"A12\" refused after 3 attempts", payload.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, payload.ExceptionType);
        Assert.Equal("boom", payload.ExceptionMessage);
        Assert.NotNull(payload.StackTop);
        Assert.InRange(payload.StackTop.Count, 1, SessionLogEventFormatter.StackFrames);
        Assert.All(payload.StackTop, frame => Assert.StartsWith("at ", frame, StringComparison.Ordinal));
        Assert.Equal(7, payload.ThreadId);
    }

    [Fact]
    public void ToPayload_NoSourceNoException_UsesDefaults()
    {
        var payload = SessionLogEventFormatter.ToPayload(Event(LogEventLevel.Warning, "plain", source: null));

        Assert.Equal(SessionLogEventFormatter.DefaultSource, payload.Source);
        Assert.Equal(SessionLogEventFormatter.DefaultSource, payload.Component);
        Assert.Null(payload.ExceptionType);
        Assert.Null(payload.StackTop);
    }

    [Fact]
    public void StackTop_UnthrownException_IsNull()
        => Assert.Null(SessionLogEventFormatter.StackTop(new InvalidOperationException("never thrown")));

    [Theory]
    [InlineData(LogEventLevel.Information, null)]
    [InlineData(LogEventLevel.Warning, "log.warning")]
    [InlineData(LogEventLevel.Error, "log.error")]
    [InlineData(LogEventLevel.Fatal, "log.fatal")]
    public void EventName_OnlyWarningAndAbove(LogEventLevel level, string? expected)
        => Assert.Equal(expected, SessionLogEventFormatter.EventName(level));

    [Theory]
    [InlineData(LogMirrorLevel.Off, LogEventLevel.Fatal, false)]
    [InlineData(LogMirrorLevel.WarningAndAbove, LogEventLevel.Information, false)]
    [InlineData(LogMirrorLevel.WarningAndAbove, LogEventLevel.Warning, true)]
    [InlineData(LogMirrorLevel.ErrorAndAbove, LogEventLevel.Warning, false)]
    [InlineData(LogMirrorLevel.ErrorAndAbove, LogEventLevel.Error, true)]
    public void ShouldMirror_FollowsTheOption(LogMirrorLevel mirror, LogEventLevel level, bool expected)
        => Assert.Equal(expected, SessionEventLogSink.ShouldMirror(level, mirror));

    [Fact]
    public async Task Emit_WritesWarningAndError_SkipsInformation_AndTheEventLogsOwnWarnings()
    {
        var log = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        var target = new SessionEventLogTarget { Current = log };
        var sink = new SessionEventLogSink(target, () => LogMirrorLevel.WarningAndAbove);

        sink.Emit(Event(LogEventLevel.Information, "chatty"));
        sink.Emit(Event(LogEventLevel.Warning, "careful"));
        sink.Emit(Event(LogEventLevel.Error, "broken", exception: Thrown()));
        sink.Emit(Event(LogEventLevel.Warning, "event log trouble", source: SessionEventLogSink.EventLogSource));
        await log.DisposeAsync();

        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"type\":\"log.warning\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"message\":\"careful\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"component\":\"GsxClient\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"log.error\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"exceptionType\":\"System.InvalidOperationException\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"stackTop\":[", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("chatty", string.Join('\n', lines), StringComparison.Ordinal);
        Assert.DoesNotContain("event log trouble", string.Join('\n', lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_BeforeTheHostSetsTheTarget_DropsSilently()
    {
        var sink = new SessionEventLogSink(new SessionEventLogTarget(), () => LogMirrorLevel.WarningAndAbove);

        sink.Emit(Event(LogEventLevel.Error, "too early"));
        // No exception, nothing to assert on disk — the point is that nothing is queued or thrown.
    }

    [Fact]
    public async Task Emit_NestedOnTheSameThread_IsDroppedByTheGuard()
    {
        var log = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        var target = new SessionEventLogTarget { Current = log };
        SessionEventLogSink? sink = null;
        var mirror = new ReentrantMirror(() => sink!.Emit(Event(LogEventLevel.Error, "nested")));
        sink = new SessionEventLogSink(target, mirror.Level);

        sink.Emit(Event(LogEventLevel.Warning, "outer"));
        await log.DisposeAsync();

        var text = File.ReadAllText(log.Path);
        Assert.Contains("outer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nested", text, StringComparison.Ordinal);
        Assert.Equal(1, mirror.Calls); // the nested emit bailed before reading the option
    }

    /// <summary>An option reader that re-enters the sink on the second call, standing in for
    /// anything logged while Record runs on the emitting thread.</summary>
    private sealed class ReentrantMirror(Action reenter)
    {
        public int Calls { get; private set; }

        public LogMirrorLevel Level()
        {
            Calls++;
            if (Calls == 1)
            {
                reenter();
            }

            return LogMirrorLevel.WarningAndAbove;
        }
    }
}
