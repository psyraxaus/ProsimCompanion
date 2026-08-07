namespace ProsimCompanion.Core.TechLog;

/// <summary>Standard ATA MEL repair-interval category. Day counts are representative and
/// config-overridable (<see cref="Configuration.TechLogOptions"/>) — real intervals are
/// operator-specific, and this is a simulation of the paperwork, not a legal MEL.</summary>
public enum MelCategory
{
    A,
    B,
    C,
    D,
}

/// <summary>Where a defect sits in its life: just raised, carried under the MEL, or fixed.</summary>
public enum DefectStatus
{
    Open,
    Deferred,
    Rectified,
}

/// <summary>
/// One tech-log defect, carried between flights as a procedural item. Persisted in
/// <c>techlog.json</c>, keyed by <see cref="Id"/> for idempotent updates. Titles, MEL
/// references, categories and implications are locked facts — spoken verbatim, never
/// composed — and the MEL reference is always visibly simulated ("MEL (SIM) …"), never a
/// plausible-looking real citation.
/// </summary>
public sealed class TechLogDefect
{
    public string Id { get; set; } = "";

    /// <summary>ISO date the defect was raised, e.g. "2026-08-08".</summary>
    public string RaisedDate { get; set; } = "";

    /// <summary>Session id the defect was raised in (null when raised outside a session).</summary>
    public string? RaisedFlight { get; set; }

    /// <summary>Provenance: "manual" | "fromAbnormal:&lt;procedureId&gt;" | "randomWear".</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Short title — locked fact, e.g. "APU inoperative".</summary>
    public string Title { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Simulated MEL reference — locked, e.g. "MEL (SIM) CAT C".</summary>
    public string MelReference { get; set; } = "";

    public MelCategory Category { get; set; } = MelCategory.C;

    /// <summary>Repair interval in calendar days for the category (config-derived at raise
    /// time and then frozen, so later config edits never move an existing due date).</summary>
    public int RepairIntervalDays { get; set; } = 10;

    /// <summary>ISO date the deferral expires = RaisedDate + RepairIntervalDays.</summary>
    public string DueDate { get; set; } = "";

    /// <summary>Spoken operational implication — locked, e.g. "ground air required".</summary>
    public string OperationalImplications { get; set; } = "";

    /// <summary>Optional placard text shown in the UI.</summary>
    public string? Placard { get; set; }

    public DefectStatus Status { get; set; } = DefectStatus.Deferred;

    /// <summary>Sectors this defect has been carried — incremented once per session, guarded
    /// by <see cref="CountedSessions"/> so re-folding a session never double-counts.</summary>
    public int SectorsCarried { get; set; }

    /// <summary>Session ids already counted toward <see cref="SectorsCarried"/>.</summary>
    public List<string> CountedSessions { get; set; } = [];

    public string? RectifiedDate { get; set; }

    public string? RectifiedFlight { get; set; }

    public bool IsOpen => Status is DefectStatus.Open or DefectStatus.Deferred;
}

/// <summary>The persisted tech-log file shape (<c>techlog.json</c>).</summary>
public sealed class TechLogStore
{
    public int Version { get; set; } = 1;

    public List<TechLogDefect> Defects { get; set; } = [];
}

/// <summary>One benign, purely procedural defect template from the random-wear pool.</summary>
public sealed class WearPoolEntry
{
    public string Title { get; set; } = "";

    /// <summary>"A"/"B"/"C"/"D"; anything else falls back to C.</summary>
    public string Category { get; set; } = "C";

    public string? MelReference { get; set; }

    public string? Implications { get; set; }

    public string? Placard { get; set; }
}

/// <summary>The random-wear pool file shape (<c>config/techlog/wear-pool.json</c>).</summary>
public sealed class WearPool
{
    public List<WearPoolEntry> Entries { get; set; } = [];
}
