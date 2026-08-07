namespace ProsimCompanion.Core.Weather;

/// <summary>Dominant precipitation at an airport, classified from the METAR present-weather
/// groups for gross change detection (obscuration-only, e.g. BR/FG, counts as
/// <see cref="None"/> because it is not precipitation).</summary>
public enum PrecipKind
{
    None,
    Rain,
    Snow,
    Thunderstorm,
    Freezing,
    Other,
}

/// <summary>
/// Weather facts for one airport, from whichever provider won the composite chain
/// (ActiveSky, ProSim gateway, or SayIntentions). Every field is optional — a source that
/// cannot supply a value leaves it null so consumers simply omit it, mirroring the
/// degrade-not-fail rule for the providers themselves.
/// </summary>
public sealed record WxFacts(
    string? RawMetar,
    int? WindDirDeg,
    int? WindSpeedKt,
    int? VisibilityMeters,
    double? QnhHpa,
    int? TemperatureC,
    string? AtisLetter,
    string? ActiveRunway,
    int? CeilingFt = null,
    int? WindGustKt = null,
    PrecipKind Precip = PrecipKind.None)
{
    /// <summary>The "no observation" value — providers return this instead of throwing.</summary>
    public static WxFacts None { get; } = new(null, null, null, null, null, null, null, null);
}
