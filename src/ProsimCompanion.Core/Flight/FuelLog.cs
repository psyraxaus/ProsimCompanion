using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>One line of the fuel log — the paper OFP's fuel column (issue #154): the plan for
/// a navlog fix and, once the fix is passed, what actually happened there.</summary>
public sealed record FuelLogRow(
    int Index,
    string Ident,
    TimeSpan? PlannedTimeFromTakeoff,
    double? PlannedFobKg,
    DateTimeOffset? ActualTimeUtc,
    double? ActualFobKg)
{
    public bool Passed => ActualFobKg is not null;

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
public sealed record FuelLogSnapshot(
    string PlanKey,
    string OriginIcao,
    string DestinationIcao,
    IReadOnlyList<FuelLogRow> Rows,
    double PlannedLandingKg,
    DateTimeOffset? TakeoffUtc)
{
    public static FuelLogSnapshot Empty { get; } = new("", "", "", [], 0, null);

    public bool HasPlan => Rows.Count >= 2;

    public int PassedCount => Rows.Count(r => r.Passed);

    /// <summary>The last row with an actual figure, if any.</summary>
    public FuelLogRow? LastPassed => Rows.LastOrDefault(r => r.Passed);
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
/// </summary>
public static class FuelLogCore
{
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
    /// flight was reset (takeoff time gone), and stamps every fix passed since the last
    /// evaluation with the present fuel and time. Several fixes passed in one tick (a long
    /// gap, a reconnect) all get the same stamp — the log says so by the identical times.
    /// Returns the next snapshot and the rows newly stamped, for the session log.
    /// </summary>
    public static (FuelLogSnapshot Next, IReadOnlyList<FuelLogRow> Stamped) Advance(
        FuelLogSnapshot current,
        OfpData? ofp,
        GeoPoint? position,
        double? fuelOnBoardKg,
        DateTimeOffset? takeoffUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = PlanKeyOf(ofp);
        if (key != current.PlanKey)
        {
            current = FromOfp(ofp);
        }

        if (!current.HasPlan)
        {
            return (current, []);
        }

        if (takeoffUtc is null)
        {
            // Before takeoff (or after a reset): a clean log with the plan only.
            return current.PassedCount == 0 && current.TakeoffUtc is null
                ? (current, [])
                : (FromOfp(ofp), []);
        }

        if (current.TakeoffUtc != takeoffUtc)
        {
            current = current with { TakeoffUtc = takeoffUtc };
        }

        if (position is not { } at || fuelOnBoardKg is not { } fob || fob <= 0
            || NavlogProgress.LastFixPassed(ofp!.Navlog, at) is not { } passed)
        {
            return (current, []);
        }

        // The destination counts as passed when the aircraft reaches the end of the final leg.
        var lastIndex = passed.Fraction >= 0.99 && passed.Index == current.Rows.Count - 2 ? passed.Index + 1 : passed.Index;
        var lastStamped = current.LastPassed?.Index ?? -1;
        if (lastIndex <= lastStamped)
        {
            return (current, []);
        }

        var rows = current.Rows.ToList();
        var stamped = new List<FuelLogRow>();
        for (var i = lastStamped + 1; i <= lastIndex; i++)
        {
            // The origin row is the takeoff: its time is the takeoff time, not "now".
            var when = i == 0 ? takeoffUtc.Value : nowUtc;
            rows[i] = rows[i] with { ActualTimeUtc = when, ActualFobKg = Math.Round(fob) };
            stamped.Add(rows[i]);
        }

        return (current with { Rows = rows }, stamped);
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
