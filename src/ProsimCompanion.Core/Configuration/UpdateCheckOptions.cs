namespace ProsimCompanion.Core.Configuration;

/// <summary>Update-available check against GitHub releases (Prosim2GSX's "New Stable Version"
/// banner). Purely informational — the check never blocks startup and fails silently offline.</summary>
public sealed class UpdateCheckOptions : IOptionSection
{
    public static string SectionName => "updateCheck";

    public bool Enabled { get; set; } = true;

    /// <summary>Re-check interval. The predecessor checked once at startup only; a long
    /// interval keeps long-running sessions informed without hammering the API.</summary>
    public int IntervalHours { get; set; } = 24;
}
