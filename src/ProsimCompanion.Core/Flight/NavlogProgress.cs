using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Where the aircraft is along the OFP navlog. Written for the FO's fuel check (issue #148)
/// and moved into Core for the Fuel Log page (issue #154) so both read the same answer.
/// </summary>
public static class NavlogProgress
{
    /// <summary>How far off the direct leg the aircraft may be and still count as on it.</summary>
    public const double CorridorNm = 30;

    /// <summary>
    /// The last navlog fix the aircraft has passed and how far along the next leg it is
    /// (0–1). The aircraft is "on" a leg when its along-track position lies inside the leg
    /// and it is within <see cref="CorridorNm"/> of it; the leg it is closest to wins. Off
    /// every leg (a direct, a hold), the nearest fix decides: passed if the aircraft is
    /// closer to the destination than that fix is. Null with fewer than two fixes.
    /// </summary>
    public static (int Index, double Fraction)? LastFixPassed(IReadOnlyList<OfpFix> navlog, GeoPoint position)
    {
        ArgumentNullException.ThrowIfNull(navlog);
        if (navlog.Count < 2)
        {
            return null;
        }

        (int Index, double Fraction, double Cross)? best = null;
        for (var i = 0; i < navlog.Count - 1; i++)
        {
            var from = navlog[i].Position;
            var to = navlog[i + 1].Position;
            var length = GreatCircle.DistanceNm(from, to);
            if (length < 0.5)
            {
                continue;
            }

            var (along, cross) = GreatCircle.AlongCrossTrackNm(from, to, position);
            if (along < 0 || along > length || Math.Abs(cross) > CorridorNm)
            {
                continue;
            }

            // Later legs win ties (over a fix both legs meet, the aircraft is passing it, not
            // finishing the previous leg); half a mile covers float noise.
            if (best is null || Math.Abs(cross) <= best.Value.Cross + 0.5)
            {
                best = (i, along / length, Math.Abs(cross));
            }
        }

        if (best is { } onLeg)
        {
            return (onLeg.Index, onLeg.Fraction);
        }

        var destination = navlog[^1].Position;
        var nearest = 0;
        var nearestNm = double.MaxValue;
        for (var i = 0; i < navlog.Count; i++)
        {
            var d = GreatCircle.DistanceNm(position, navlog[i].Position);
            if (d < nearestNm)
            {
                nearestNm = d;
                nearest = i;
            }
        }

        if (nearest == navlog.Count - 1)
        {
            return (navlog.Count - 2, 1.0); // Nearest the destination itself, off the final leg: at the plan's end.
        }

        var passedNearest = GreatCircle.DistanceNm(position, destination) <= GreatCircle.DistanceNm(navlog[nearest].Position, destination);
        var index = passedNearest ? nearest : nearest - 1;
        if (index < 0)
        {
            return (0, 0); // Not yet at the first fix: approaching it.
        }

        return index >= navlog.Count - 1 ? (navlog.Count - 2, 1.0) : (index, 0.0);
    }

    /// <summary>Planned fuel on board at <paramref name="fraction"/> of the leg after fix
    /// <paramref name="index"/>; the fix's own figure when the next one has none.</summary>
    public static double? PlannedFuelAt(IReadOnlyList<OfpFix> navlog, int index, double fraction)
    {
        ArgumentNullException.ThrowIfNull(navlog);
        if (index < 0 || index >= navlog.Count || navlog[index].PlannedFuelOnBoardKg is not { } at)
        {
            return null;
        }

        if (index + 1 < navlog.Count && navlog[index + 1].PlannedFuelOnBoardKg is { } next)
        {
            return Math.Round(at + ((next - at) * Math.Clamp(fraction, 0, 1)));
        }

        return at;
    }
}
