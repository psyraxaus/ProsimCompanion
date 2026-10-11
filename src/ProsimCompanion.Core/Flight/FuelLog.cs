using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>One line of the fuel log — the paper OFP's fuel column (issue #154): the plan for
/// a navlog fix and, once the fix is passed, what actually happened there.</summary>
/// <param name="NotRecorded">Issue #167: the fix was passed while nobody was watching — the
/// app was started after it (or during a gap), so there is no honest time or fuel for it.
/// Before this flag every fix passed since the last tick was stamped with the restart's
/// second and the fuel at that moment (2026-10-10: five rows at 20:58:10Z, deltaMinutes −126).</param>
public sealed record FuelLogRow(
    int Index,
    string Ident,
    TimeSpan? PlannedTimeFromTakeoff,
    double? PlannedFobKg,
    DateTimeOffset? ActualTimeUtc,
    double? ActualFobKg,
    bool NotRecorded = false)
{
    public bool Passed => ActualFobKg is not null;

    /// <summary>Passed with a figure, or passed unrecorded — either way behind the aircraft.</summary>
    public bool Handled => Passed || NotRecorded;

    /// <summary>Actual minus plan: negative = burning more than planned.</summary>
    public double? DeltaKg => ActualFobKg is { } a && PlannedFobKg is { } p ? Math.Round(a - p) : null;

    /// <summary>Planned clock time over the fix once the takeoff time is known.</summary>
    public DateTimeOffset? PlannedTimeUtc(DateTimeOffset? takeoffUtc)
        => takeoffUtc is { } t && PlannedTimeFromTakeoff is { } d ? t + d : null;

    /// <summary>Actual minus planned, in whole minutes, once both are known.</summary>
    public int? DeltaMinutes(DateTimeOffset? takeoffUtc)
        => ActualTimeUtc is { } a && PlannedTimeUtc(takeoffUtc) is { } p ? (int)Math.Round((a - p).TotalMinutes) : null;
}

/// <summary>The whole log for the loaded OFP. <see cref="PlanKey"/> ties it to one plan: a
/// different OFP starts a fresh log.</summary>
/// <param name="TakeoffUnknown">The leg is under way but its takeoff time is not known (the
/// app started in flight, issue #167): fixes still stamp as they are passed, the planned
/// clock times and the minute deltas stay empty.</param>
public sealed record FuelLogSnapshot(
    string PlanKey,
    string OriginIcao,
    string DestinationIcao,
    IReadOnlyList<FuelLogRow> Rows,
    double PlannedLandingKg,
    DateTimeOffset? TakeoffUtc,
    bool TakeoffUnknown = false)
{
    public static FuelLogSnapshot Empty { get; } = new("", "", "", [], 0, null);

    public bool HasPlan => Rows.Count >= 2;

    public int PassedCount => Rows.Count(r => r.Passed);

    /// <summary>The last row with an actual figure, if any.</summary>
    public FuelLogRow? LastPassed => Rows.LastOrDefault(r => r.Passed);

    /// <summary>The last row behind the aircraft — stamped or passed unrecorded.</summary>
    public FuelLogRow? LastHandled => Rows.LastOrDefault(r => r.Handled);
}

public sealed class FuelLogStore : SnapshotStore<FuelLogSnapshot>
{
    public FuelLogStore()
        : base(FuelLogSnapshot.Empty)
    {
    }
}

/// <summary>
/// The pure fuel-log rules (issue #154), clock and inputs passed in so a timer-free test can
/// drive them. A log is (re)built from the OFP navlog; rows are stamped as fixes are passed,
/// using <see cref="NavlogProgress.LastFixPassed"/> — the same answer the FO's fuel check
/// uses. Nothing is stamped before the takeoff time exists: on the ground the aircraft sits
/// "at" the first fix, and the fuel at the gate is not the fuel over the departure airport.
/// The origin row therefore carries the takeoff fuel at the takeoff time, as on paper.
/// <para>
/// A row is stamped "now" only when the aircraft is still at the fix (issue #167): the
/// planned leg time scaled by how far along the next leg it already is must be within
/// <see cref="MaxStampLag"/>. A fix further behind — passed during an app restart, a
/// reconnect gap, or any tick that jumped several fixes — is marked not recorded instead
/// of being stamped with a time and a fuel figure that belong to somewhere else.
/// </para>
/// </summary>
public static class FuelLogCore
{
    /// <summary>How far past a fix "now" may still stand for the fix's own time.</summary>
    public static readonly TimeSpan MaxStampLag = TimeSpan.FromMinutes(3);

    /// <summary>Without planned leg times, the share of the next leg within which a fix
    /// still counts as "just passed".</summary>
    private const double MaxStampFractionWithoutTimes = 0.1;

    /// <summary>What identifies a plan: a new key means a new log.</summary>
    public static string PlanKeyOf(OfpData? ofp)
        => ofp is null || ofp.Navlog.Count < 2 ? "" : $"{ofp.OriginIcao}-{ofp.DestinationIcao}-{ofp.Navlog.Count}-{ofp.RequestId}";

    /// <summary>A fresh log for the OFP: every fix a row, nothing passed. Empty without a navlog.</summary>
    public static FuelLogSnapshot FromOfp(OfpData? ofp)
    {
        var key = PlanKeyOf(ofp);
        if (key.Length == 0)
        {
            return FuelLogSnapshot.Empty;
        }

        var rows = ofp!.Navlog
            .Select((fix, i) => new FuelLogRow(i, fix.Ident, fix.TimeFromTakeoff, fix.PlannedFuelOnBoardKg, null, null))
            .ToList();
        return new FuelLogSnapshot(key, ofp.OriginIcao, ofp.DestinationIcao, rows, ofp.FuelPlanLandingKg, null);
    }

    /// <summary>
    /// One evaluation: rebuilds the log when the plan changed, clears the stamps when the
    /// flight was reset (takeoff time gone), and handles every fix passed since the last
    /// evaluation — the one the aircraft is at gets the present fuel and time, the ones
    /// further back are marked not recorded. Returns the next snapshot, the rows newly
    /// stamped and the rows newly marked not recorded, for the session log.
    /// </summary>
    /// <param name="takeoffUnknown">The leg is airborne with no takeoff time (app started in
    /// flight, issue #167): the log runs without planned clock times instead of waiting for
    /// a takeoff that already happened.</param>
    public static (FuelLogSnapshot Next, IReadOnlyList<FuelLogRow> Stamped, IReadOnlyList<FuelLogRow> Skipped) Advance(
        FuelLogSnapshot current,
        OfpData? ofp,
        GeoPoint? position,
        double? fuelOnBoardKg,
        DateTimeOffset? takeoffUtc,
        DateTimeOffset nowUtc,
        bool takeoffUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = PlanKeyOf(ofp);
        if (key != current.PlanKey)
        {
            current = FromOfp(ofp);
        }

        if (!current.HasPlan)
        {
            return (current, [], []);
        }

        if (takeoffUtc is null && !takeoffUnknown)
        {
            // Before takeoff (or after a reset): a clean log with the plan only.
            return current.PassedCount == 0 && current.TakeoffUtc is null && !current.TakeoffUnknown && current.LastHandled is null
                ? (current, [], [])
                : (FromOfp(ofp), [], []);
        }

        if (current.TakeoffUtc != takeoffUtc || current.TakeoffUnknown != takeoffUnknown)
        {
            current = current with { TakeoffUtc = takeoffUtc, TakeoffUnknown = takeoffUnknown };
        }

        if (position is not { } at || fuelOnBoardKg is not { } fob || fob <= 0
            || NavlogProgress.LastFixPassed(ofp!.Navlog, at) is not { } passed)
        {
            return (current, [], []);
        }

        // The destination counts as passed when the aircraft reaches the end of the final leg.
        var atDestination = passed.Fraction >= 0.99 && passed.Index == current.Rows.Count - 2;
        var lastIndex = atDestination ? passed.Index + 1 : passed.Index;
        var lastHandled = current.LastHandled?.Index ?? -1;
        if (lastIndex <= lastHandled)
        {
            return (current, [], []);
        }

        var rows = current.Rows.ToList();
        var stamped = new List<FuelLogRow>();
        var skipped = new List<FuelLogRow>();
        for (var i = lastHandled + 1; i <= lastIndex; i++)
        {
            if (i == 0)
            {
                // The origin row is the takeoff: its time is the takeoff time, not "now".
                rows[i] = takeoffUtc is { } takeoff
                    ? rows[i] with { ActualTimeUtc = takeoff, ActualFobKg = Math.Round(fob), NotRecorded = false }
                    : rows[i] with { NotRecorded = true };
            }
            else if (i == lastIndex && (atDestination || StillAtFix(rows, i, passed.Fraction)))
            {
                rows[i] = rows[i] with { ActualTimeUtc = nowUtc, ActualFobKg = Math.Round(fob), NotRecorded = false };
            }
            else
            {
                rows[i] = rows[i] with { NotRecorded = true };
            }

            (rows[i].Passed ? stamped : skipped).Add(rows[i]);
        }

        return (current with { Rows = rows }, stamped, skipped);
    }

    /// <summary>Whether "now" may stand for the time the aircraft passed fix
    /// <paramref name="index"/>, given how far along the following leg it is.</summary>
    private static bool StillAtFix(IReadOnlyList<FuelLogRow> rows, int index, double fraction)
    {
        if (index + 1 < rows.Count
            && rows[index].PlannedTimeFromTakeoff is { } from
            && rows[index + 1].PlannedTimeFromTakeoff is { } to
            && to > from)
        {
            return (to - from) * Math.Clamp(fraction, 0, 1) <= MaxStampLag;
        }

        return fraction <= MaxStampFractionWithoutTimes;
    }

    /// <summary>"losing" when the last three stamped deltas keep getting worse, "gaining"
    /// when they keep getting better, else "holding"; null with fewer than two stamps.</summary>
    public static string? Trend(FuelLogSnapshot snapshot, double stepKg = 20)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var deltas = snapshot.Rows.Where(r => r.DeltaKg is not null).Select(r => r.DeltaKg!.Value).TakeLast(3).ToList();
        if (deltas.Count < 2)
        {
            return null;
        }

        var worsening = true;
        var improving = true;
        for (var i = 1; i < deltas.Count; i++)
        {
            worsening &= deltas[i] <= deltas[i - 1] - stepKg;
            improving &= deltas[i] >= deltas[i - 1] + stepKg;
        }

        return worsening ? "losing" : improving ? "gaining" : "holding";
    }
}
