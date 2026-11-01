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
    /// wins over the automation toggle. On by default since 1.0 (ADR-0019 §7, issue #216): the
    /// leg-boundary machine runs always and the duty day gives its legs names and an end; a
    /// settings.json that says <c>false</c> keeps saying it.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.day.track")]
    public bool Enabled { get; set; } = true;

    /// <summary>Begin a day automatically at the first Preflight (else use "start duty day").</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.day.autoStart", DependsOn = "Enabled")]
    public bool AutoStart { get; set; } = true;

    /// <summary>Minutes added after the last on-blocks for the duty figure.</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.day.allowance", Suffix = "min", Min = "0", DependsOn = "Enabled")]
    public int PostFlightAllowanceMinutes { get; set; } = 15;

    /// <summary>The backstop (ADR-0019 §8, issue #217): auto-close an open day this many minutes
    /// after the arrival let go of the aircraft — deboarding complete, else on-blocks — with no
    /// new plan and no end of duty (0 = never). 300 since 1.0: the old 90 counted from the last
    /// phase edge or app start and the real EHAM turnaround of 2026-11-01 ran 88 minutes against
    /// it. A settings.json that names its own figure keeps it.</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.day.autoClose", Suffix = "min", Min = "0", DependsOn = "Enabled")]
    public int AutoCloseIdleMinutes { get; set; } = 300;

    /// <summary>The FO's one-time offer (ADR-0019 §8, issue #217): "Captain, is that the end of
    /// our duty day?" once the aircraft has been cold and dark, parked, with no plan for this
    /// many minutes in a turnaround (0 = never offer). Yes ends the day; no or silence leaves it
    /// open and the question is not asked again that day.</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.day.endOfDutyOffer", Suffix = "min", Min = "0", Max = "120", DependsOn = "Enabled")]
    public int EndOfDutyOfferMinutes { get; set; } = 10;

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

    /// <summary>Flight-duty-period warning (issue #189): the FDP from the report time against
    /// the EASA ORO.FTL.205 basic maximum for the report hour and sector count
    /// (<c>config/ftl-easa.json</c>, user-editable) — a banner on the Day page and one spoken line
    /// at the turnaround summary when the next leg would exceed it. Advisory: no acclimatisation,
    /// extensions or rest rules. Off by default.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.day.fdpWarning", DependsOn = "Enabled")]
    public bool FdpWarning { get; set; }

    /// <summary>Minutes before the day's first session start the report time is assumed at,
    /// when the rotation file names no report time.</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.day.reportBefore", Suffix = "min", Min = "0", Max = "180", DependsOn = "Enabled; FdpWarning")]
    public int ReportBeforeStdMinutes { get; set; } = 60;
}
