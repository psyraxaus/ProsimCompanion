using System.Globalization;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Weather;

/// <summary>Takeoff or landing — which limits apply.</summary>
public enum WindPhase
{
    Takeoff,
    Landing,
}

/// <summary>The traffic-light tone of a wind verdict.</summary>
public enum WindLimitTone
{
    /// <summary>Within limits by more than the amber band.</summary>
    Green,

    /// <summary>Within limits, but inside the amber band of one of them.</summary>
    Amber,

    /// <summary>Beyond a limit.</summary>
    Red,
}

/// <summary>One wind-against-limits verdict (issue #182).</summary>
/// <param name="Wind">The components, steady and gust.</param>
/// <param name="Phase">Takeoff or landing.</param>
/// <param name="TailwindLimitKt">The tailwind limit applied.</param>
/// <param name="CrosswindLimitKt">The crosswind limit applied.</param>
/// <param name="CrosswindLimitName">"dry" / "wet-runway" / "RWYCC 3" — which crosswind limit it was.</param>
/// <param name="Tone">Green / amber / red.</param>
/// <param name="TailwindExceeded">The worst tailwind (gust included) is beyond its limit.</param>
/// <param name="CrosswindExceeded">The worst crosswind (gust included) is beyond its limit.</param>
public sealed record WindLimitVerdict(
    WindComponents Wind,
    WindPhase Phase,
    int TailwindLimitKt,
    int CrosswindLimitKt,
    string CrosswindLimitName,
    WindLimitTone Tone,
    bool TailwindExceeded,
    bool CrosswindExceeded)
{
    public bool Exceeded => TailwindExceeded || CrosswindExceeded;
}

/// <summary>
/// The pure wind-limit rule (issue #182): the resolved components against
/// <see cref="AircraftLimitsOptions"/> for the phase and the runway condition. The limits are
/// checked gust-included (the worst of steady and gust), like the FCOM's "gust included"
/// figures; the tone turns amber inside <see cref="AmberBandKt"/> of a limit and red beyond it.
/// Pure, so the tables are the tests; the pages, the briefing clause, the FO's "wind check" and
/// the approach advisory all read it.
/// </summary>
public static class WindLimitCheck
{
    /// <summary>Within this many knots of a limit the tone is amber.</summary>
    public const double AmberBandKt = 5;

    /// <summary>The crosswind limit for a phase and a runway condition: dry → the phase's
    /// limit, wet → the wet-runway limit, RWYCC n → the contaminated table. No condition = dry.</summary>
    public static (int Kt, string Name) CrosswindLimit(AircraftLimitsOptions limits, WindPhase phase, RunwayCondition? condition)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var kt = condition switch
        {
            null or RunwayCondition.Dry => phase == WindPhase.Takeoff ? limits.MaxCrosswindTakeoffKt : limits.MaxCrosswindLandingKt,
            RunwayCondition.Wet => limits.MaxCrosswindWetKt,
            RunwayCondition.Rwycc5 => limits.CrosswindRwycc5Kt,
            RunwayCondition.Rwycc4 => limits.CrosswindRwycc4Kt,
            RunwayCondition.Rwycc3 => limits.CrosswindRwycc3Kt,
            RunwayCondition.Rwycc2 => limits.CrosswindRwycc2Kt,
            _ => limits.CrosswindRwycc1Kt,
        };
        return (kt, RunwayConditions.LimitName(condition));
    }

    public static WindLimitVerdict Evaluate(WindComponents wind, AircraftLimitsOptions limits, WindPhase phase, RunwayCondition? condition)
    {
        ArgumentNullException.ThrowIfNull(wind);
        ArgumentNullException.ThrowIfNull(limits);
        var tailLimit = phase == WindPhase.Takeoff ? limits.MaxTailwindTakeoffKt : limits.MaxTailwindLandingKt;
        var (crossLimit, name) = CrosswindLimit(limits, phase, condition);

        var tail = wind.MaxTailwindKt;
        var cross = wind.MaxCrosswindKt;
        var tailExceeded = tail > tailLimit + 0.5;
        var crossExceeded = cross > crossLimit + 0.5;
        var tone = tailExceeded || crossExceeded ? WindLimitTone.Red
            : tail > tailLimit - AmberBandKt || cross > crossLimit - AmberBandKt ? WindLimitTone.Amber
            : WindLimitTone.Green;
        return new WindLimitVerdict(wind, phase, tailLimit, crossLimit, name, tone, tailExceeded, crossExceeded);
    }

    /// <summary>The FO's line: "Crosswind component 24 knots, tailwind 3, within limits." /
    /// "Crosswind component 36 knots, gusting 41 — exceeds the wet-runway crosswind limit of
    /// 33 knots." Digits, invariant culture; the TTS reads them.</summary>
    public static string Spoken(WindLimitVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var w = verdict.Wind;
        var parts = new List<string>();
        if (w.DirectionUnknown)
        {
            parts.Add($"Variable wind {Kt(w.CrosswindKt)} knots, taken as crosswind and tailwind");
        }
        else
        {
            var cross = $"Crosswind component {Kt(w.CrosswindKt)} knots";
            if (w.GustCrosswindKt is { } gc && Math.Round(gc) > Math.Round(w.CrosswindKt))
            {
                cross += $", gusting {Kt(gc)}";
            }

            parts.Add(cross);
            var along = w.HeadwindKt >= 0 ? $"headwind {Kt(w.HeadwindKt)}" : $"tailwind {Kt(w.TailwindKt)}";
            if (w.GustHeadwindKt is { } gh && -gh > w.TailwindKt + 0.5)
            {
                along += $", gusting {Kt(-gh)}";
            }

            parts.Add(along);
        }

        var body = string.Join(", ", parts);
        if (verdict.CrosswindExceeded)
        {
            return $"{body} — exceeds the {verdict.CrosswindLimitName} crosswind limit of {verdict.CrosswindLimitKt} knots.";
        }

        if (verdict.TailwindExceeded)
        {
            return $"{body} — exceeds the tailwind limit of {verdict.TailwindLimitKt} knots.";
        }

        return verdict.Tone == WindLimitTone.Amber
            ? $"{body}, within limits but close — {verdict.CrosswindLimitName} crosswind limit {verdict.CrosswindLimitKt}, tailwind limit {verdict.TailwindLimitKt}."
            : $"{body}, within limits.";
    }

    /// <summary>The page's one-line verdict.</summary>
    public static string Summary(WindLimitVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.CrosswindExceeded)
        {
            return $"EXCEEDS {verdict.CrosswindLimitName.ToUpperInvariant()} CROSSWIND LIMIT {verdict.CrosswindLimitKt} KT";
        }

        if (verdict.TailwindExceeded)
        {
            return $"EXCEEDS TAILWIND LIMIT {verdict.TailwindLimitKt} KT";
        }

        return verdict.Tone == WindLimitTone.Amber
            ? $"WITHIN {AmberBandKt:0} KT OF A LIMIT (XW {verdict.CrosswindLimitKt} · TW {verdict.TailwindLimitKt})"
            : $"WITHIN LIMITS (XW {verdict.CrosswindLimitKt} · TW {verdict.TailwindLimitKt})";
    }

    /// <summary>Every figure the spoken line may contain, for a number verifier.</summary>
    public static IEnumerable<double> Numbers(WindLimitVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        yield return Math.Round(verdict.Wind.CrosswindKt);
        yield return Math.Round(verdict.Wind.HeadwindKt);
        yield return Math.Round(verdict.Wind.TailwindKt);
        if (verdict.Wind.GustCrosswindKt is { } gc)
        {
            yield return Math.Round(gc);
        }

        if (verdict.Wind.GustHeadwindKt is { } gh)
        {
            yield return Math.Round(Math.Abs(gh));
        }

        yield return verdict.CrosswindLimitKt;
        yield return verdict.TailwindLimitKt;
    }

    private static string Kt(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
