namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Tech log &amp; MEL simulation. This feature is the paperwork layer of airline operations —
/// deferral categories, due dates and preflight briefs — and deliberately never injects a
/// failure or writes a dataref: it models the procedure, not the malfunction. An overdue item
/// is advisory only; the app never grounds the aircraft.
/// </summary>
public sealed class TechLogOptions
{
    public const string SectionName = "techLog";

    public bool Enabled { get; set; } = true;

    /// <summary>Close open defects automatically when their MEL due date passes (checked at
    /// start and on entering ColdAndDark/Preflight). Off by default — rectification is meant
    /// to be an explicit maintenance action.</summary>
    public bool AutoRectifyOnDueDate { get; set; }

    /// <summary>Small per-flight chance of a benign, purely procedural cabin defect appearing
    /// at shutdown (from <c>config/techlog/wear-pool.json</c>) so a career's tech log gathers
    /// character. Off by default.</summary>
    public bool RandomWear { get; set; }

    /// <summary>Store path override; blank = <c>%LOCALAPPDATA%\ProsimCompanion\techlog.json</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>Wear-pool file override; blank = <c>config/techlog/wear-pool.json</c> beside the exe.</summary>
    public string WearPoolPath { get; set; } = "";

    // Representative ATA repair intervals (calendar days) per MEL category. Real intervals are
    // operator-specific; these are the simulation's defaults, overridable per category.

    public int CategoryADays { get; set; } = 3;

    public int CategoryBDays { get; set; } = 3;

    public int CategoryCDays { get; set; } = 10;

    public int CategoryDDays { get; set; } = 120;
}
