namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Company day mode (Prosim2FO "Prompt L" semantics): multi-leg duty tracking — turnaround
/// detection between sectors, cumulative block/duty figures, schedule awareness and an
/// end-of-day rotation summary. This is convenience tracking of what was actually flown,
/// NOT a flight-time-limitations calculation; there are no regulatory claims and the app
/// never grounds the aircraft.
/// </summary>
public sealed class DayOptions : IOptionSection
{
    public static string SectionName => "day";

    /// <summary>Master on/off for automatic day tracking. Voice ("start duty day") and the
    /// web button work regardless — parity with Prosim2FO, where an explicit request always
    /// wins over the automation toggle.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.day.track")]
    public bool Enabled { get; set; }

    /// <summary>Begin a day automatically at the first Preflight (else use "start duty day").</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.day.autoStart", DependsOn = "Enabled")]
    public bool AutoStart { get; set; } = true;

    /// <summary>Minutes added after the last on-blocks for the duty figure.</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.day.allowance", Suffix = "min", Min = "0", DependsOn = "Enabled")]
    public int PostFlightAllowanceMinutes { get; set; } = 15;

    /// <summary>Auto-close an open day after this many idle minutes in a turnaround
    /// (0 = never).</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.day.autoClose", Suffix = "min", Min = "0", DependsOn = "Enabled")]
    public int AutoCloseIdleMinutes { get; set; } = 90;

    /// <summary>Planned-rotation JSON folder. Empty (the default since 2026-10-13) =
    /// <c>%LOCALAPPDATA%\ProsimCompanion\config\rotations</c>, which the app seeds with a
    /// README; a relative path resolves against the application base directory.</summary>
    [OptionField(FieldKind.Text, "speechSettings.company.day.rotations", TextKind = TextFieldKind.FolderPath, Placeholder = @"%LOCALAPPDATA%\ProsimCompanion\config\rotations", BrowseStartIn = "Rotations", Normalize = FieldNormalization.Path, Advanced = true)]
    public string RotationsFolder { get; set; } = "";

    /// <summary>Speak the on-blocks turnaround summary (block, schedule delta, open
    /// tech-log items).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.day.turnaround", DependsOn = "Enabled")]
    public bool TurnaroundSummary { get; set; } = true;

    /// <summary>Day-state file override; blank = <c>%LOCALAPPDATA%\ProsimCompanion\daystate.json</c>
    /// (the predecessor hard-coded this path; here it is options-overridable).</summary>
    [OptionField(FieldKind.Text, "speechSettings.company.day.stateFile", Hint = "speechSettings.common.emptyUsesFileShown", TextKind = TextFieldKind.OutputFilePath, Placeholder = @"%LOCALAPPDATA%\ProsimCompanion\daystate.json", BrowseStartIn = "AppStateFolder", BrowseFilter = "JSON (*.json)|*.json", BrowseFileName = "daystate.json", Normalize = FieldNormalization.Path, Advanced = true)]
    public string Path { get; set; } = "";
}
