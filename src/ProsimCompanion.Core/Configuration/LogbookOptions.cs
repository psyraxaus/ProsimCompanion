namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Pilot logbook: folds each completed flight's deterministic facts (from the session event
/// log) into a persistent store. Aggregates are always computed on read — never stored — so
/// they cannot drift out of sync with the flights list.
/// </summary>
public sealed class LogbookOptions : IOptionSection
{
    public static string SectionName => "logbook";

    public bool Enabled { get; set; } = true;

    /// <summary>Store path override; blank = <c>%LOCALAPPDATA%\ProsimCompanion\logbook.json</c>.</summary>
    public string Path { get; set; } = "";
}
