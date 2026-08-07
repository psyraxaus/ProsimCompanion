namespace ProsimCompanion.Core.Day;

/// <summary>How the day's rotation is sourced: learned sector-by-sector, or read from a
/// planned rotation file.</summary>
public enum DayMode
{
    /// <summary>Zero-setup: each flown sector auto-appends and the route is learned from the
    /// session's extracted facts.</summary>
    Progressive,

    /// <summary>A <c>rotations/*.json</c> file supplied the legs, flight numbers and
    /// schedule; actuals are matched against the plan.</summary>
    Planned,
}

/// <summary>Day-level state — distinct from and alongside the 14 <see cref="Flight.FlightPhase"/>s
/// (the day machine never modifies them).</summary>
public enum DayPhase
{
    /// <summary>A sector is in progress (or about to be).</summary>
    OnLeg,

    /// <summary>Between sectors — on blocks, awaiting the next Preflight.</summary>
    Turnaround,

    /// <summary>The day is closed; a new day may start.</summary>
    Ended,
}

/// <summary>One sector of a duty day. Scheduled fields come from a planned rotation; actuals
/// and the per-leg debrief facts are filled as flown (at the session-finalization step).</summary>
public sealed class DayLeg
{
    /// <summary>1-based position in the rotation.</summary>
    public int Index { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? FlightNo { get; set; }

    public string? ScheduledOffUtc { get; set; }

    public string? ScheduledOnUtc { get; set; }

    /// <summary>The leg's event-log session id (file name without extension) — the key the
    /// finalization step uses to fill this leg's facts from the right session file.</summary>
    public string? SessionId { get; set; }

    public string? ActualOffUtc { get; set; }

    public string? ActualOnUtc { get; set; }

    public int? BlockMinutes { get; set; }

    public int? FlightMinutes { get; set; }

    public bool Landed { get; set; }

    /// <summary>Planned-mode plan-vs-reality note, e.g. "planned EGCC, flew EGBB"; null when
    /// the leg matched the plan (or the day is progressive).</summary>
    public string? Deviation { get; set; }

    /// <summary>Overall approach verdict for the leg: any unstable gate → false, else any
    /// stable gate → true, no judged gate → null. ONE rule, aligned with the logbook's
    /// ApproachResult so day and career figures can never disagree.</summary>
    public bool? Stabilized { get; set; }

    public int Abnormals { get; set; }

    public int MemoryDrills { get; set; }

    public int DefectsRaised { get; set; }

    public int DefectsRectified { get; set; }
}

/// <summary>The persisted duty-day state (<c>daystate.json</c>).</summary>
public sealed class DayState
{
    public string DayId { get; set; } = "";

    public string StartedUtc { get; set; } = "";

    /// <summary>Duty start: the rotation's report time when planned, else the moment the day
    /// started.</summary>
    public string? DutyStartUtc { get; set; }

    public DayPhase State { get; set; } = DayPhase.OnLeg;

    public DayMode Mode { get; set; } = DayMode.Progressive;

    public string? ReportTimeUtc { get; set; }

    public int PostFlightAllowanceMin { get; set; } = 15;

    /// <summary>1-based index of the leg in progress (or just completed, in a turnaround).</summary>
    public int CurrentLegIndex { get; set; } = 1;

    public List<DayLeg> Legs { get; set; } = [];

    public bool IsOpen => State != DayPhase.Ended;

    public DayLeg? Current => Legs.FirstOrDefault(l => l.Index == CurrentLegIndex);

    public int LegsCompleted => Legs.Count(l => !string.IsNullOrEmpty(l.ActualOnUtc));
}

/// <summary>The derived, read-only view of the day (web page + summaries). Times and figures
/// are computed on demand, never stored — they cannot drift out of sync with the legs.</summary>
public sealed record DayView(
    bool Active,
    string State,
    int LegIndex,
    int LegCount,
    int LegsCompleted,
    string? From,
    string? To,
    int BlockMinutes,
    int DutyMinutes,
    int? DelayMinutes);

// ---- planned rotation file (rotations/*.json) ----

/// <summary>The planned rotation file shape: day metadata + ordered legs.</summary>
public sealed class RotationFile
{
    public string? DayId { get; set; }

    public string? ReportTimeUtc { get; set; }

    public List<RotationLegFile> Legs { get; set; } = [];
}

/// <summary>One planned leg of a rotation file.</summary>
public sealed class RotationLegFile
{
    public string? From { get; set; }

    public string? To { get; set; }

    public string? FlightNo { get; set; }

    public string? ScheduledOffUtc { get; set; }

    public string? ScheduledOnUtc { get; set; }
}
