namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Runtime-adjustable logging configuration (hot-reloaded — no restart needed). Levels use
/// Serilog names: Verbose, Debug, Information, Warning, Error, Fatal.
/// </summary>
public sealed class LoggingOptions : IOptionSection
{
    public static string SectionName => "logging";

    /// <summary>Minimum level for all ProsimCompanion sources without an override.</summary>
    public string DefaultLevel { get; set; } = "Information";

    /// <summary>
    /// Per-source overrides, keyed by namespace prefix (e.g. "ProsimCompanion.Prosim": "Debug").
    /// Framework sources ("Microsoft", "Microsoft.AspNetCore") default to Warning unless
    /// overridden here.
    /// </summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, raw protocol traffic (GraphQL bodies, GSX Remote API frames) is written to the
    /// separate wire-trace CMTrace file. Off by default — verbose by design.
    /// </summary>
    public bool WireTrace { get; set; }

    /// <summary>
    /// Which log events are mirrored into the session JSONL as <c>log.warning</c> /
    /// <c>log.error</c> / <c>log.fatal</c> events (SessionEventLogSink), making the session
    /// file the single evidence stream for triage. Warning-and-above by default; Information
    /// is never mirrored.
    /// </summary>
    public LogMirrorLevel MirrorToSession { get; set; } = LogMirrorLevel.WarningAndAbove;
}

/// <summary>Threshold for mirroring log events into the session event log.</summary>
public enum LogMirrorLevel
{
    /// <summary>No log events reach the session file.</summary>
    Off,

    /// <summary>Warning, Error and Fatal (the default).</summary>
    WarningAndAbove,

    /// <summary>Error and Fatal only.</summary>
    ErrorAndAbove,
}
