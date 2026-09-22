using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Weather;

/// <summary>Which airport a Flight Status weather card is showing.</summary>
public enum WeatherCardRole
{
    /// <summary>"LOCAL WEATHER": where the aircraft is — the origin until descent, the
    /// destination from descent onward.</summary>
    Local,

    /// <summary>"WEATHER AT DESTINATION".</summary>
    Destination,

    /// <summary>"WEATHER AT ALTERNATE": the second card once the local card has moved to the
    /// destination, when the OFP carries an alternate.</summary>
    Alternate,
}

/// <summary>One Flight Status weather card.</summary>
/// <param name="Role">Which label the card carries.</param>
/// <param name="Icao">The station, or empty with no flight plan.</param>
/// <param name="AirportName">Title-cased SimBrief airport name ("London Heathrow"); empty when unknown.</param>
/// <param name="Status">Whether the chain found an observation, and if not, why.</param>
/// <param name="Facts">The parsed observation (<see cref="WxFacts.None"/> when absent).</param>
/// <param name="Sky">The icon to show.</param>
/// <param name="SkyLabel">The caps word under the icon ("FEW CLOUDS").</param>
/// <param name="ObservedAt">"10:20Z" from the METAR, or null.</param>
/// <param name="Failure">The failure line when <paramref name="Status"/> is not Found.</param>
public sealed record WeatherCard(
    WeatherCardRole Role,
    string Icao,
    string AirportName,
    WxProbeStatus Status,
    WxFacts Facts,
    SkyCondition Sky,
    string SkyLabel,
    string? ObservedAt,
    string? Failure)
{
    /// <summary>A card with no flight plan behind it.</summary>
    public static WeatherCard Empty(WeatherCardRole role)
        => new(role, "", "", WxProbeStatus.NoData, WxFacts.None, SkyCondition.Unknown, "NO DATA", null, null);

    /// <summary>Builds the card from a probe result: classification and stamp are derived
    /// here once so the page never parses METAR text.</summary>
    public static WeatherCard From(WeatherCardRole role, string icao, string airportName, WxProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var (sky, label) = SkyConditionClassifier.Classify(probe.Status == WxProbeStatus.Found ? probe.Facts : null);
        return new WeatherCard(
            role, icao, airportName, probe.Status, probe.Facts, sky, label,
            SkyConditionClassifier.ObservationTime(probe.Facts.RawMetar),
            probe.FailureMessage(icao));
    }
}

/// <summary>The two cards plus fetch bookkeeping.</summary>
public sealed record HeroWeatherSnapshot(
    WeatherCard Local,
    WeatherCard Second,
    DateTimeOffset? FetchedAtUtc,
    bool IsRefreshing)
{
    public static HeroWeatherSnapshot Empty { get; } =
        new(WeatherCard.Empty(WeatherCardRole.Local), WeatherCard.Empty(WeatherCardRole.Destination), null, false);
}

/// <summary>
/// Live weather for the Flight Status hero cards (owner request 2026-09-22). Written by
/// <see cref="HeroWeatherService"/>; read by the page. Kept in Core so the Web project can
/// render it without a pillar reference.
/// </summary>
public sealed class HeroWeatherStore : SnapshotStore<HeroWeatherSnapshot>
{
    public HeroWeatherStore()
        : base(HeroWeatherSnapshot.Empty)
    {
    }
}

/// <summary>Which stations the two cards should show for the current OFP and phase.</summary>
/// <param name="LocalIcao">The "local" station.</param>
/// <param name="LocalName">Its airport name.</param>
/// <param name="SecondRole">Destination or Alternate.</param>
/// <param name="SecondIcao">The second station (may be empty).</param>
/// <param name="SecondName">Its airport name.</param>
public sealed record WeatherCardPlan(
    string LocalIcao, string LocalName, WeatherCardRole SecondRole, string SecondIcao, string SecondName)
{
    public static WeatherCardPlan None { get; } = new("", "", WeatherCardRole.Destination, "", "");
}

/// <summary>
/// Pure choice of stations (testable without timers). "Local" follows the aircraft: origin
/// while at the gate, pushing, taxiing and en route; destination from Descent onward. When
/// the local card moves to the destination, the second card shows the alternate if the OFP
/// has one — otherwise it keeps the destination (two identical cards are honest, not a bug).
/// </summary>
public static class WeatherCardPlanner
{
    public static WeatherCardPlan Plan(OfpData? ofp, FlightPhase phase)
    {
        if (ofp is null || ofp.OriginIcao.Length == 0)
        {
            return WeatherCardPlan.None;
        }

        var arriving = phase is FlightPhase.Descent or FlightPhase.Approach
            or FlightPhase.LandingRollout or FlightPhase.TaxiIn or FlightPhase.Shutdown;
        if (!arriving)
        {
            return new WeatherCardPlan(
                ofp.OriginIcao, AirportNameFormat.TitleCase(ofp.OriginName),
                WeatherCardRole.Destination, ofp.DestinationIcao, AirportNameFormat.TitleCase(ofp.DestinationName));
        }

        return ofp.AlternateIcao.Length > 0
            ? new WeatherCardPlan(
                ofp.DestinationIcao, AirportNameFormat.TitleCase(ofp.DestinationName),
                WeatherCardRole.Alternate, ofp.AlternateIcao, AirportNameFormat.TitleCase(ofp.AlternateName))
            : new WeatherCardPlan(
                ofp.DestinationIcao, AirportNameFormat.TitleCase(ofp.DestinationName),
                WeatherCardRole.Destination, ofp.DestinationIcao, AirportNameFormat.TitleCase(ofp.DestinationName));
    }
}

/// <summary>SimBrief ships airport names in capitals ("LONDON HEATHROW"); the card wants
/// "London Heathrow". Small connectives stay lower-case; a name that is already mixed-case
/// is left alone.</summary>
public static class AirportNameFormat
{
    private static readonly HashSet<string> Lower = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "da", "del", "di", "du", "la", "le", "of", "the", "and", "am", "an", "im", "in", "y",
    };

    public static string TitleCase(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var trimmed = name.Trim();
        if (trimmed.Any(char.IsLower))
        {
            return trimmed;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i].ToLowerInvariant();
            words[i] = i > 0 && Lower.Contains(word)
                ? word
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word);
        }

        return string.Join(' ', words);
    }
}
