using System.Globalization;
using System.IO;
using ProsimCompanion.Core.Logging;
using Serilog.Events;
using Serilog.Formatting;

namespace ProsimCompanion.App.Logging;

/// <summary>Serilog file formatter emitting CMTrace lines (see <see cref="CmTraceFormat"/>).</summary>
public sealed class CmTraceTextFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        if (logEvent.Exception is not null)
        {
            message = message + Environment.NewLine + logEvent.Exception;
        }

        var source = GetScalar(logEvent, "SourceContext") ?? "ProsimCompanion";
        var lastDot = source.LastIndexOf('.');
        var component = lastDot >= 0 && lastDot < source.Length - 1 ? source[(lastDot + 1)..] : source;

        var threadId = 0;
        if (logEvent.Properties.TryGetValue("ThreadId", out var threadValue)
            && threadValue is ScalarValue { Value: int id })
        {
            threadId = id;
        }

        var severity = logEvent.Level switch
        {
            LogEventLevel.Warning => CmTraceFormat.SeverityWarning,
            LogEventLevel.Error or LogEventLevel.Fatal => CmTraceFormat.SeverityError,
            _ => CmTraceFormat.SeverityInfo,
        };

        output.WriteLine(CmTraceFormat.Line(
            logEvent.Timestamp,
            message,
            component,
            source,
            severity,
            threadId));
    }

    private static string? GetScalar(LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text }
            ? text
            : null;
}
