namespace ProsimCompanion.Core.Weather;

/// <summary>
/// The reported wind resolved onto a runway (issue #182): head/tail and crosswind components
/// for the steady wind and, when the METAR carries one, for the gust. Knots; headwind positive,
/// tailwind negative; the crosswind is unsigned (which side is the page's arrow, not a limit).
/// </summary>
/// <param name="HeadwindKt">Along-runway component of the steady wind, headwind positive.</param>
/// <param name="CrosswindKt">Across-runway component of the steady wind, unsigned.</param>
/// <param name="GustHeadwindKt">The same for the gust, null without a gust group.</param>
/// <param name="GustCrosswindKt">The same for the gust, null without a gust group.</param>
/// <param name="DirectionUnknown">True for a variable wind with speed: the components are the
/// conservative worst case (the whole speed as tailwind and as crosswind).</param>
public sealed record WindComponents(
    double HeadwindKt,
    double CrosswindKt,
    double? GustHeadwindKt,
    double? GustCrosswindKt,
    bool DirectionUnknown)
{
    /// <summary>Steady tailwind, positive from behind (0 for a headwind).</summary>
    public double TailwindKt => Math.Max(0, -HeadwindKt);

    /// <summary>The worst tailwind the gust or the steady wind produces — what a limit is checked against.</summary>
    public double MaxTailwindKt => Math.Max(TailwindKt, GustHeadwindKt is { } g ? Math.Max(0, -g) : 0);

    /// <summary>The worst crosswind the gust or the steady wind produces.</summary>
    public double MaxCrosswindKt => Math.Max(CrosswindKt, GustCrosswindKt ?? 0);
}

/// <summary>Pure wind trigonometry (issue #182) — the one place a runway heading meets a METAR
/// wind. The destination weather watch's tailwind trigger and the performance pages consume it.</summary>
public static class WindMath
{
    /// <summary>Resolves a wind onto a runway heading. Null without a speed. Calm (speed 0) is
    /// all zeros. A variable wind with speed (direction null) is the conservative worst case.
    /// 360 and 000 are the same direction; headings and directions are taken modulo 360.</summary>
    public static WindComponents? Resolve(int? directionDeg, int? speedKt, int? gustKt, double runwayHeadingDeg)
    {
        if (speedKt is not { } speed)
        {
            return null;
        }

        if (speed <= 0)
        {
            return new WindComponents(0, 0, gustKt is { } calmGust and > 0 && directionDeg is { } d0 ? Along(d0, calmGust, runwayHeadingDeg) : null,
                gustKt is { } calmGust2 and > 0 && directionDeg is { } d1 ? Across(d1, calmGust2, runwayHeadingDeg) : null, false);
        }

        if (directionDeg is not { } direction)
        {
            var worstGust = gustKt is { } g and > 0 ? (double?)g : null;
            return new WindComponents(-speed, speed, worstGust is { } wg ? -wg : null, worstGust, DirectionUnknown: true);
        }

        var gust = gustKt is { } gk && gk > speed ? gk : (int?)null;
        return new WindComponents(
            Along(direction, speed, runwayHeadingDeg),
            Across(direction, speed, runwayHeadingDeg),
            gust is { } gh ? Along(direction, gh, runwayHeadingDeg) : null,
            gust is { } gc ? Across(direction, gc, runwayHeadingDeg) : null,
            false);
    }

    /// <summary>Along-runway component, headwind positive.</summary>
    public static double Along(double directionDeg, double speedKt, double runwayHeadingDeg)
        => Clean(speedKt * Math.Cos(Relative(directionDeg, runwayHeadingDeg)));

    /// <summary>Across-runway component, unsigned.</summary>
    public static double Across(double directionDeg, double speedKt, double runwayHeadingDeg)
        => Math.Abs(Clean(speedKt * Math.Sin(Relative(directionDeg, runwayHeadingDeg))));

    /// <summary>cos(90°) is not zero in floating point: a pure crosswind must read "headwind
    /// 0", never "tailwind 0".</summary>
    private static double Clean(double value) => Math.Abs(value) < 1e-9 ? 0 : value;

    private static double Relative(double directionDeg, double runwayHeadingDeg)
        => (((directionDeg - runwayHeadingDeg) % 360) + 360) % 360 * Math.PI / 180.0;
}
