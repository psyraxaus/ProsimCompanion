namespace ProsimCompanion.Core.Day;

/// <summary>One leg row for the web page's per-leg table — a snapshot copy, never the live
/// mutable <see cref="DayLeg"/>.</summary>
public sealed record DayLegView(
    int Index,
    string? From,
    string? To,
    string? FlightNo,
    string? ScheduledOffUtc,
    string? ScheduledOnUtc,
    string? ActualOffUtc,
    string? ActualOnUtc,
    int? BlockMinutes,
    bool Landed,
    bool? Stabilized,
    string? Deviation)
{
    /// <summary>True once the leg has an actual on-blocks time.</summary>
    public bool Completed => !string.IsNullOrEmpty(ActualOnUtc);
}

/// <summary>Everything the web Day page renders, plus the debrief's day-context line.
/// <see cref="View"/> is null when no day exists (never started, or state file absent).</summary>
public sealed record DaySnapshot(
    DayView? View,
    IReadOnlyList<DayLegView> Legs,
    string? DebriefContextLine)
{
    public static DaySnapshot Empty { get; } = new(null, [], null);
}

/// <summary>
/// Live day-mode state for the web page. Kept in Core so the Web project (which references
/// only Core) can render it; written by the company day service in ProsimCompanion.Speech.
/// </summary>
public sealed class DayStatusStore
{
    private readonly object _gate = new();
    private DaySnapshot _snapshot = DaySnapshot.Empty;

    /// <summary>Raised after any update, on the writer's thread — consumers marshal to their
    /// own context (InvokeAsync in Blazor components).</summary>
    public event EventHandler? Changed;

    public DaySnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <summary>Replaces the snapshot and raises <see cref="Changed"/>.</summary>
    public void Update(DaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _snapshot = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Commands the web Day page can issue (implemented by the company day service in
/// ProsimCompanion.Speech; registered only when that pillar is). Both are idempotent from the
/// page's point of view: starting an already-open day and ending a non-existent one no-op.</summary>
public interface IDayControl
{
    /// <summary>Starts a duty day now (same path as the "start duty day" voice command).</summary>
    void StartDay();

    /// <summary>Ends the open duty day (same path as the "end duty day" voice command).</summary>
    void EndDay();
}
