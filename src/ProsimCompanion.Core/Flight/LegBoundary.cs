using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>The pilot-visible states between two legs (ADR-0019 §2, CONTEXT.md "Leg
/// boundary"). NOT flight phases: the phase engine reads PREFLIGHT while this reads
/// AWAITING PLAN, and the two never overlap in meaning (ADR-0019 §4).</summary>
public enum LegBoundaryState
{
    /// <summary>No boundary in progress: a leg is planned, under way, or the arrival is still
    /// deboarding.</summary>
    None,

    /// <summary>Deboarding complete; parked, powered, cabin empty, the old plan still in the FMS.</summary>
    Turnaround,

    /// <summary>The FMS plan cleared; nothing to prepare until a new OFP lands.</summary>
    AwaitingPlan,
}

/// <summary>What the boundary machine holds: the state, the plan the current leg began with,
/// and the two step flags of this turnaround (so a step arriving out of order is still
/// counted once).</summary>
public sealed record LegBoundarySnapshot(
    LegBoundaryState State,
    string? PlanId,
    bool Deboarded,
    bool FmsCleared,
    DateTimeOffset? ChangedAtUtc,
    string? Reason)
{
    public static LegBoundarySnapshot Empty { get; } = new(LegBoundaryState.None, null, false, false, null, null);
}

/// <summary>One accepted edge: the states before and after, why, the order warning when the
/// step came out of the preferred order, and whether a new leg began on it.</summary>
public sealed record LegBoundaryChange(
    LegBoundaryState From,
    LegBoundaryState To,
    string Reason,
    string? Warning,
    bool LegBegan,
    string? PlanId,
    string? PreviousPlanId = null);

/// <summary>Published leg-boundary state for the Flight Status pill and the monitor board.</summary>
public sealed class LegBoundaryStore : SnapshotStore<LegBoundarySnapshot>
{
    public LegBoundaryStore()
        : base(LegBoundarySnapshot.Empty)
    {
    }
}

/// <summary>A new leg began: a different OFP landed (ADR-0019 §1). The Day engine, the
/// cross-check and the sector announcement hang off this edge, never off the Preflight edge.</summary>
public sealed class LegBeganEventArgs(string planId, string? previousPlanId, LegBoundaryState from) : EventArgs
{
    public string PlanId { get; } = planId;

    public string? PreviousPlanId { get; } = previousPlanId;

    /// <summary>The boundary state the plan arrived in — Turnaround means the FMS still held
    /// the previous plan (ADR-0019 §3's reminder case).</summary>
    public LegBoundaryState From { get; } = from;
}

/// <summary>The edges other pillars subscribe to (cross-feature traffic goes through Core).</summary>
public interface ILegBoundaryEvents
{
    event EventHandler<LegBeganEventArgs>? LegBegan;
}

/// <summary>
/// The pure leg-boundary machine (ADR-0019 §2). Three edges feed it — deboarding complete,
/// FMS cleared, a different plan loaded — and the order is preferred, never required: any
/// edge is accepted from any state, a step out of order is a warning on the change, a new
/// plan always begins the leg whatever else happened, the same plan twice does nothing.
/// Timer-free so the EHAM turnaround can be driven through it in a test.
/// </summary>
public sealed class LegBoundaryCore
{
    public LegBoundarySnapshot Snapshot { get; private set; } = LegBoundarySnapshot.Empty;

    /// <summary>Step 1: GSX deboarding completed.</summary>
    public LegBoundaryChange? Deboarded(DateTimeOffset now)
    {
        var s = Snapshot;
        if (s.Deboarded)
        {
            return null;
        }

        var to = s.State == LegBoundaryState.AwaitingPlan ? LegBoundaryState.AwaitingPlan : LegBoundaryState.Turnaround;
        var warning = s.State == LegBoundaryState.AwaitingPlan
            ? "deboarding completed after the FMS was already cleared (Reset FMS after landing, or an early reset) — order noted, not an error"
            : null;
        Snapshot = s with { State = to, Deboarded = true, ChangedAtUtc = now, Reason = "deboarding complete" };
        return new LegBoundaryChange(s.State, to, "deboarding complete", warning, false, s.PlanId);
    }

    /// <summary>Step 2: the FMS plan went from a value to empty while parked.</summary>
    public LegBoundaryChange? FmsCleared(DateTimeOffset now)
    {
        var s = Snapshot;
        if (s.FmsCleared)
        {
            return null;
        }

        var warning = s.Deboarded
            ? null
            : "FMS cleared before deboarding completed (Reset FMS after landing, or an early reset) — order noted, not an error";
        Snapshot = s with { State = LegBoundaryState.AwaitingPlan, FmsCleared = true, ChangedAtUtc = now, Reason = "FMS plan cleared" };
        return new LegBoundaryChange(s.State, LegBoundaryState.AwaitingPlan, "FMS plan cleared", warning, false, s.PlanId);
    }

    /// <summary>Step 3: a plan landed. A different id begins the leg from any state; the same
    /// id is the same leg and does nothing.</summary>
    public LegBoundaryChange? PlanLoaded(string? planId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(planId))
        {
            return null;
        }

        var s = Snapshot;
        if (string.Equals(planId, s.PlanId, StringComparison.Ordinal))
        {
            return null;
        }

        string reason;
        string? warning = null;
        if (s.PlanId is null)
        {
            reason = "first plan of the session";
        }
        else if (s.State == LegBoundaryState.Turnaround)
        {
            reason = "new plan, turnaround complete";
            warning = "the new plan arrived while the FMS still held the previous plan — the FMS was not cleared (ADR-0019 §3)";
        }
        else if (s.State == LegBoundaryState.AwaitingPlan)
        {
            reason = "new plan, turnaround complete";
            if (!s.Deboarded)
            {
                warning = "the new plan arrived with no deboarding completed this turnaround (GSX deboarding skipped?)";
            }
        }
        else
        {
            reason = "a different plan replaced the loaded one with no turnaround in between";
            warning = "a different plan replaced " + s.PlanId + " at the gate — no deboarding, no FMS clear";
        }

        Snapshot = new LegBoundarySnapshot(LegBoundaryState.None, planId, false, false, now, reason);
        return new LegBoundaryChange(s.State, LegBoundaryState.None, reason, warning, true, planId, s.PlanId);
    }

    /// <summary>The aircraft flew: whatever the boundary held is over (a Turnaround or
    /// Awaiting plan state cannot exist in the air), and the step flags are armed again for
    /// the next arrival. The plan id is kept — the same plan may land and be flown again.</summary>
    public LegBoundaryChange? Airborne(DateTimeOffset now)
    {
        var s = Snapshot;
        if (s.State == LegBoundaryState.None && !s.Deboarded && !s.FmsCleared)
        {
            return null;
        }

        Snapshot = s with { State = LegBoundaryState.None, Deboarded = false, FmsCleared = false, ChangedAtUtc = now, Reason = "airborne" };
        return s.State == LegBoundaryState.None
            ? null
            : new LegBoundaryChange(s.State, LegBoundaryState.None, "airborne — the boundary state cannot outlive a take-off", null, false, s.PlanId);
    }

    /// <summary>Adopt a persisted snapshot (the restart rule, ADR-0019 §9).</summary>
    public void Restore(LegBoundarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }
}
