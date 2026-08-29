namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>What the automatic preliminary-loadsheet trigger can see when it fires.</summary>
/// <param name="OfpImported">An OFP is in the store (the prelim's planned figures).</param>
/// <param name="CgPopulated">Both CG datarefs have pushed a first value since connect.</param>
/// <param name="GrossCgMac">Live <c>aircraft.cg</c>.</param>
/// <param name="ZfwCgMac">Live <c>aircraft.zfwcg</c>.</param>
public sealed record PrelimReadiness(bool OfpImported, bool CgPopulated, double GrossCgMac, double ZfwCgMac);

public enum PrelimTriggerAction
{
    /// <summary>Everything the prelim needs is present — generate now.</summary>
    Generate,

    /// <summary>A precondition is missing; hold the trigger and re-check later.</summary>
    Wait,

    /// <summary>Waited too long — surface the failure and stop.</summary>
    GiveUp,
}

public sealed record PrelimTriggerDecision(PrelimTriggerAction Action, string? Reason = null);

/// <summary>
/// The automatic prelim trigger is an edge (GSX refuel active, the tankering pre-skip, the
/// STD offset) that can arrive before the prelim's inputs exist: on the 2026-08-29 turnaround
/// the tankering skip raised it seconds after an app restart, the CG datarefs had not pushed
/// a value yet, and the page showed ERROR until the pilot pressed Resend. An edge must arm a
/// wait for readiness, not a one-shot — this policy is that wait, kept pure so it is testable.
/// </summary>
public static class PrelimTriggerPolicy
{
    /// <summary>Re-check cadence while waiting.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>Longest an armed trigger waits before it reports a failure. Generous: an OFP
    /// import can legitimately follow the departure sequence by many minutes.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(20);

    /// <summary>Decides the next step for an armed automatic trigger.</summary>
    /// <param name="readiness">What the trigger can see now.</param>
    /// <param name="waited">How long the trigger has been armed.</param>
    public static PrelimTriggerDecision Next(PrelimReadiness readiness, TimeSpan waited)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        var blocker = Blocker(readiness);
        if (blocker is null)
        {
            return new(PrelimTriggerAction.Generate);
        }

        return waited > MaxWait
            ? new(PrelimTriggerAction.GiveUp, $"gave up after {MaxWait.TotalMinutes:F0} min — {blocker}")
            : new(PrelimTriggerAction.Wait, blocker);
    }

    private static string? Blocker(PrelimReadiness r)
    {
        if (!r.OfpImported)
        {
            return "waiting for an OFP import";
        }

        if (!r.CgPopulated)
        {
            return "waiting for the CG datarefs (aircraft.cg / aircraft.zfwcg) to populate";
        }

        if (!IsPlausible(r.GrossCgMac) || !IsPlausible(r.ZfwCgMac))
        {
            return $"waiting for a plausible CG (gross {r.GrossCgMac:F1} %MAC, ZFW {r.ZfwCgMac:F1} %MAC; "
                + $"expected {A320WeightAndBalance.MinPlausibleCgMac:F0}–{A320WeightAndBalance.MaxPlausibleCgMac:F0})";
        }

        return null;
    }

    private static bool IsPlausible(double cgMac)
        => !double.IsNaN(cgMac) && !double.IsInfinity(cgMac)
            && cgMac >= A320WeightAndBalance.MinPlausibleCgMac && cgMac <= A320WeightAndBalance.MaxPlausibleCgMac;
}
