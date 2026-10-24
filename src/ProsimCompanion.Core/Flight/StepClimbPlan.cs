using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.Flight;

/// <summary>One planned step climb: at <paramref name="Ident"/> the plan goes from <paramref name="FromFt"/> to <paramref name="ToFt"/>.</summary>
public sealed record StepPoint(int FixIndex, string Ident, GeoPoint Position, double FromFt, double ToFt)
{
    public int ToFlightLevel => (int)Math.Round(ToFt / 100);
}

/// <summary>
/// Pure derivation of the planned step climbs from the navlog's altitude per fix (issue #189):
/// once the plan is in the cruise (a fix at or above <see cref="CruiseFloorFt"/>), every fix
/// whose planned altitude rises by at least <see cref="MinStepFt"/> over the previous fix and
/// holds at the next one is a step. Procedure fixes (SID/STAR) never count; the climb to the
/// initial cruise level is not a step.
/// </summary>
public static class StepClimbPlan
{
    public const double CruiseFloorFt = 18_000;
    public const double MinStepFt = 900;

    public static IReadOnlyList<StepPoint> Derive(IReadOnlyList<OfpFix> navlog)
    {
        ArgumentNullException.ThrowIfNull(navlog);
        var steps = new List<StepPoint>();
        double? previous = null;
        for (var i = 0; i < navlog.Count; i++)
        {
            var fix = navlog[i];
            if (fix.AltitudeFt is not { } alt || alt <= 0)
            {
                continue;
            }

            if (previous is { } prev && prev >= CruiseFloorFt && alt - prev >= MinStepFt && !fix.IsProcedure && Holds(navlog, i, alt))
            {
                steps.Add(new StepPoint(i, fix.Ident, fix.Position, prev, alt));
            }

            previous = alt;
        }

        return steps;
    }

    /// <summary>The next step still ahead of the aircraft: the first whose fix the aircraft has
    /// not passed (by <see cref="NavlogProgress.LastFixPassed"/>) and whose level is above the
    /// current one by more than half a step.</summary>
    public static StepPoint? Next(IReadOnlyList<StepPoint> steps, IReadOnlyList<OfpFix> navlog, GeoPoint? position, double currentAltitudeFt)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(navlog);
        var passed = position is { } p ? NavlogProgress.LastFixPassed(navlog, p)?.Index ?? -1 : -1;
        return steps.FirstOrDefault(s => s.FixIndex > passed && s.ToFt > currentAltitudeFt + MinStepFt / 2);
    }

    private static bool Holds(IReadOnlyList<OfpFix> navlog, int index, double alt)
    {
        for (var j = index + 1; j < navlog.Count; j++)
        {
            if (navlog[j].AltitudeFt is { } next && next > 0)
            {
                return next >= alt - 100;
            }
        }

        return true; // last fix with an altitude — the plan ends at that level
    }
}
