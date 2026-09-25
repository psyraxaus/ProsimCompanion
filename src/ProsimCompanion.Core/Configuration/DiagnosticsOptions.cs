namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the "Export diagnostics" support bundle (Logs page). The bundle is the one
/// artefact a user sends in with a bug report: the newest session event logs, the newest
/// CMTrace app logs, a redacted copy of settings.json and the version banner, zipped with a
/// manifest of SHA-256 hashes. Sizes are bounded here so a bundle stays mailable.
/// </summary>
public sealed class DiagnosticsOptions : IOptionSection
{
    public static string SectionName => "diagnostics";

    /// <summary>Hard bounds for <see cref="BundleSessionCount"/>.</summary>
    public const int MinSessions = 1;
    public const int MaxSessions = 20;

    /// <summary>Hard bounds for <see cref="BundleLogDays"/>.</summary>
    public const int MinLogDays = 1;
    public const int MaxLogDays = 14;

    /// <summary>How many of the newest <c>session-*.jsonl</c> files go into the bundle. Five
    /// covers a company day plus the restart-before-the-bug that support usually asks for.</summary>
    public int BundleSessionCount { get; set; } = 5;

    /// <summary>How many of the newest daily CMTrace app logs go into the bundle.</summary>
    public int BundleLogDays { get; set; } = 3;

    /// <summary>
    /// Off by default: the wire trace (raw GSX / gateway frames) reached 24 MB in one smoke
    /// test and is only useful when a protocol question is on the table.
    /// </summary>
    public bool IncludeWireTrace { get; set; }
}
