namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Pilot logbook: folds each completed flight's deterministic facts (from the session event
/// log) into a persistent store. Aggregates are always computed on read — never stored — so
/// they cannot drift out of sync with the flights list.
/// </summary>
public sealed class LogbookOptions : IOptionSection
{
    public static string SectionName => "logbook";

    [OptionField(FieldKind.Bool, "speechSettings.common.enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Store path override; blank = <c>%LOCALAPPDATA%\ProsimCompanion\logbook.json</c>.</summary>
    [OptionField(FieldKind.Text, "speechSettings.techLog.logbook.file", Hint = "speechSettings.common.emptyUsesFileShown", TextKind = TextFieldKind.OutputFilePath, Placeholder = @"%LOCALAPPDATA%\ProsimCompanion\logbook.json", BrowseStartIn = "AppStateFolder", BrowseFilter = "JSON (*.json)|*.json", BrowseFileName = "logbook.json", Normalize = FieldNormalization.Path, Advanced = true)]
    public string Path { get; set; } = "";
}
