using System.Globalization;

namespace ProsimCompanion.Core.Logging;

/// <summary>
/// Renders log lines in the CMTrace format so the rolling files can be watched live (and
/// severity-coloured) in Microsoft's CMTrace viewer — the diagnostic practice proven during the
/// predecessors' GSX Remote API work. Pure string building; kept free of Serilog types so it is
/// unit-testable from Core.
/// </summary>
public static class CmTraceFormat
{
    /// <summary>CMTrace severity: informational (also used for debug/verbose).</summary>
    public const int SeverityInfo = 1;

    /// <summary>CMTrace severity: warning.</summary>
    public const int SeverityWarning = 2;

    /// <summary>CMTrace severity: error (also used for fatal).</summary>
    public const int SeverityError = 3;

    /// <summary>
    /// Builds one CMTrace line. <paramref name="component"/> is the short display name (CMTrace's
    /// Component column); <paramref name="source"/> the full source context (File column).
    /// </summary>
    public static string Line(
        DateTimeOffset timestamp,
        string message,
        string component,
        string source,
        int severity,
        int threadId)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A message containing the literal close marker would corrupt the line structure.
        var safeMessage = message.Replace("]LOG]!>", "]LOG]! >", StringComparison.Ordinal);

        var offsetMinutes = (int)timestamp.Offset.TotalMinutes;
        var time = string.Create(
            CultureInfo.InvariantCulture,
            $"{timestamp:HH:mm:ss.fff}{(offsetMinutes >= 0 ? "+" : "")}{offsetMinutes}");
        var date = timestamp.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"<![LOG[{safeMessage}]LOG]!><time=\"{time}\" date=\"{date}\" component=\"{component}\" context=\"\" type=\"{severity}\" thread=\"{threadId}\" file=\"{source}\">");
    }
}
