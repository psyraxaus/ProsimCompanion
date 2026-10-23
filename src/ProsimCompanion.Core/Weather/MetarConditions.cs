namespace ProsimCompanion.Core.Weather;

/// <summary>The runway surface state a briefing infers from the METAR (issue #180). The
/// METAR reports precipitation, not the runway: this is the crew's usual inference, never a
/// runway condition report — the Landing page's own selector (package 3) is the source when
/// the pilot has one.</summary>
public enum RunwaySurfaceState
{
    /// <summary>No observation at all.</summary>
    Unknown,

    /// <summary>No precipitation reported.</summary>
    Dry,

    /// <summary>Rain, thunderstorms or other liquid precipitation reported.</summary>
    Wet,

    /// <summary>Snow or freezing precipitation reported — contamination likely.</summary>
    Contaminated,
}

/// <summary>
/// Pure ground-weather inferences over <see cref="WxFacts"/> for the briefings (issue #180):
/// the runway surface from the precipitation groups and the "icing conditions on the ground"
/// predicate. The in-flight icing advisory in the flow monitor works from live TAT and visible
/// moisture, which a briefing at the gate does not have; this is its METAR counterpart and
/// borrows the de-icing policy's thresholds (temperature at or below +10 °C with precipitation,
/// low visibility or a frost-likely dew-point spread).
/// </summary>
public static class MetarConditions
{
    /// <summary>Icing is a concern at or below this temperature with moisture present
    /// (the engine anti-ice rule of thumb).</summary>
    public const int IcingTemperatureMaxC = 10;

    /// <summary>Visibility at or below which fog or mist counts as visible moisture.</summary>
    public const int IcingVisibilityMaxM = 1500;

    /// <summary>Temperature/dew-point spread at or below which frost is likely on a cold
    /// airframe — the same figure the de-icing policy uses.</summary>
    public const int FrostSpreadMaxC = 3;

    /// <summary>The runway surface state the precipitation groups imply.</summary>
    public static RunwaySurfaceState Surface(WxFacts weather)
    {
        ArgumentNullException.ThrowIfNull(weather);
        if (!HasObservation(weather))
        {
            return RunwaySurfaceState.Unknown;
        }

        return weather.Precip switch
        {
            PrecipKind.None => RunwaySurfaceState.Dry,
            PrecipKind.Snow or PrecipKind.Freezing => RunwaySurfaceState.Contaminated,
            _ => RunwaySurfaceState.Wet,
        };
    }

    /// <summary>True when the observation suggests icing conditions for the takeoff: cold
    /// enough and moisture present as precipitation, low visibility or a frost-likely spread.</summary>
    public static bool GroundIcingLikely(WxFacts weather)
    {
        ArgumentNullException.ThrowIfNull(weather);
        if (weather.TemperatureC is not { } temperature || temperature > IcingTemperatureMaxC)
        {
            return false;
        }

        return weather.Precip != PrecipKind.None
            || weather.VisibilityMeters is <= IcingVisibilityMaxM
            || (weather.DewPointC is { } dewPoint && temperature - dewPoint <= FrostSpreadMaxC);
    }

    /// <summary>True when any field says a provider actually observed something.</summary>
    public static bool HasObservation(WxFacts weather)
    {
        ArgumentNullException.ThrowIfNull(weather);
        return weather.RawMetar is not null || weather.WindDirDeg is not null || weather.TemperatureC is not null
            || weather.VisibilityMeters is not null || weather.QnhHpa is not null;
    }
}
