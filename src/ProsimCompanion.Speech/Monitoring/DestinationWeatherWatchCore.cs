using System.Globalization;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>What changed at the destination.</summary>
public enum WeatherTrigger
{
    Visibility,
    Ceiling,
    Atis,
    Tailwind,
}

/// <summary>One announcement the watch wants to make.</summary>
/// <param name="BelowThreshold">The destination is now at or below a limit (visibility or
/// ceiling), or has the tailwind — the alternate is worth mentioning.</param>
public sealed record WeatherAnnouncement(WeatherTrigger Trigger, string Text, bool BelowThreshold);

/// <summary>
/// The destination weather watch rules (issue #148), pure and clock-passed-in. Every trigger
/// is an EDGE between the previous observation and this one — a threshold crossed down or
/// back up, a new ATIS letter, a tailwind component appearing or going away on the planned
/// landing runway — and each trigger type speaks at most once per
/// <see cref="DestinationWeatherWatchOptions.MinutesBetweenAnnouncements"/>. The first
/// observation of a flight is a baseline: only a reading already below a limit is announced.
/// </summary>
public sealed class DestinationWeatherWatchCore
{
    private readonly Dictionary<WeatherTrigger, DateTimeOffset> _lastAnnounced = [];
    private WxFacts? _previous;

    /// <summary>Forgets the baseline and the rate limits (new flight, new destination).</summary>
    public void Reset()
    {
        _previous = null;
        _lastAnnounced.Clear();
    }

    /// <summary>Compares the new observation with the previous one and returns what to say,
    /// honouring the per-trigger rate limit. The observation becomes the new baseline.</summary>
    public IReadOnlyList<WeatherAnnouncement> Observe(
        WxFacts facts, string? plannedRunway, DestinationWeatherWatchOptions options, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);

        var previous = _previous;
        _previous = facts;
        var announcements = new List<WeatherAnnouncement>();
        var window = TimeSpan.FromMinutes(Math.Max(1, options.MinutesBetweenAnnouncements));

        void Consider(WeatherTrigger trigger, WeatherAnnouncement? announcement)
        {
            if (announcement is null)
            {
                return;
            }

            if (_lastAnnounced.TryGetValue(trigger, out var last) && nowUtc - last < window)
            {
                return;
            }

            _lastAnnounced[trigger] = nowUtc;
            announcements.Add(announcement);
        }

        Consider(WeatherTrigger.Visibility, Visibility(previous, facts, options));
        Consider(WeatherTrigger.Ceiling, Ceiling(previous, facts, options));
        if (options.AnnounceAtisChange)
        {
            Consider(WeatherTrigger.Atis, Atis(previous, facts));
        }

        Consider(WeatherTrigger.Tailwind, Tailwind(previous, facts, plannedRunway, options));
        return announcements;
    }

    private static WeatherAnnouncement? Visibility(WxFacts? previous, WxFacts facts, DestinationWeatherWatchOptions options)
    {
        if (facts.VisibilityMeters is not { } now)
        {
            return null;
        }

        var below = now < options.VisibilityThresholdM;
        bool? wasBelow = previous?.VisibilityMeters is { } was ? was < options.VisibilityThresholdM : null;
        if (wasBelow == below || (wasBelow is null && !below))
        {
            return null;
        }

        var text = below
            ? $"Destination visibility now {Visibility(now)}, below {Visibility(options.VisibilityThresholdM)}."
            : $"Destination visibility improved to {Visibility(now)}.";
        return new WeatherAnnouncement(WeatherTrigger.Visibility, text, below);
    }

    private static WeatherAnnouncement? Ceiling(WxFacts? previous, WxFacts facts, DestinationWeatherWatchOptions options)
    {
        // No ceiling reported = sky clear enough: above the limit.
        var below = facts.CeilingFt is { } now && now < options.CeilingThresholdFt;
        bool? wasBelow = previous is null ? null : previous.CeilingFt is { } was && was < options.CeilingThresholdFt;
        if (wasBelow == below || (wasBelow is null && !below))
        {
            return null;
        }

        var text = below
            ? $"Destination ceiling now {facts.CeilingFt!.Value.ToString(CultureInfo.InvariantCulture)} feet, below {options.CeilingThresholdFt.ToString(CultureInfo.InvariantCulture)}."
            : facts.CeilingFt is { } lifted
                ? $"Destination ceiling lifted to {lifted.ToString(CultureInfo.InvariantCulture)} feet."
                : "Destination ceiling has lifted.";
        return new WeatherAnnouncement(WeatherTrigger.Ceiling, text, below);
    }

    private static WeatherAnnouncement? Atis(WxFacts? previous, WxFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.AtisLetter) || string.IsNullOrWhiteSpace(previous?.AtisLetter)
            || string.Equals(previous.AtisLetter, facts.AtisLetter, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var letter = Core.Speech.NatoPhonetics.Letter(facts.AtisLetter) ?? facts.AtisLetter;
        return new WeatherAnnouncement(WeatherTrigger.Atis, $"Destination ATIS now information {letter}.", false);
    }

    private static WeatherAnnouncement? Tailwind(WxFacts? previous, WxFacts facts, string? plannedRunway, DestinationWeatherWatchOptions options)
    {
        if (RunwayHeadingDeg(plannedRunway) is not { } heading)
        {
            return null;
        }

        var now = TailwindComponentKt(facts, heading);
        if (now is null)
        {
            return null;
        }

        var tail = now > options.TailwindThresholdKt;
        bool? wasTail = previous is null ? null : TailwindComponentKt(previous, heading) is { } was && was > options.TailwindThresholdKt;
        if (wasTail == tail || (wasTail is null && !tail))
        {
            return null;
        }

        var runway = Core.Speech.SpokenText.RunwayOrNull(plannedRunway) ?? plannedRunway;
        var text = tail
            ? $"Wind at destination {facts.WindDirDeg:000} at {facts.WindSpeedKt} knots: a {Math.Round(now.Value).ToString("0", CultureInfo.InvariantCulture)} knot tailwind on runway {runway}."
            : $"Wind at destination {facts.WindDirDeg:000} at {facts.WindSpeedKt} knots: no longer a tailwind on runway {runway}.";
        return new WeatherAnnouncement(WeatherTrigger.Tailwind, text, tail);
    }

    /// <summary>Tailwind component (kt, positive = from behind) of the reported wind on a
    /// runway heading; null without a wind. Calm is zero. Public for tests.</summary>
    public static double? TailwindComponentKt(WxFacts facts, double runwayHeadingDeg)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.WindSpeedKt is not { } speed)
        {
            return null;
        }

        if (speed == 0 || facts.WindDirDeg is not { } direction)
        {
            return speed == 0 ? 0 : null; // variable wind with speed: direction unknown
        }

        var relative = (direction - runwayHeadingDeg) * Math.PI / 180.0;
        return -speed * Math.Cos(relative);
    }

    /// <summary>"27R" → 270, "04" → 40; null when the designator has no number. Magnetic,
    /// like the designator; METAR winds are true — a few degrees of variation are inside the
    /// threshold's purpose.</summary>
    public static double? RunwayHeadingDeg(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return null;
        }

        var digits = new string(runway.Trim().TrimStart('R', 'W', 'r', 'w').TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 36
            ? number * 10.0
            : null;
    }

    /// <summary>"800 metres" / "1500 metres" / "10 kilometres" (METAR 9999 = ten kilometres or more).</summary>
    public static string Visibility(int metres)
        => metres >= 9999
            ? $"{Math.Round(metres / 1000.0).ToString("0", CultureInfo.InvariantCulture)} kilometres"
            : $"{metres.ToString(CultureInfo.InvariantCulture)} metres";

    /// <summary>"Alternate Schiphol: visibility 10 kilometres, ceiling 2500 feet." — appended
    /// when the destination drops below a limit; null without facts.</summary>
    public static string? AlternateLine(string? icaoOrName, WxFacts? alternate)
    {
        if (alternate is null || string.IsNullOrWhiteSpace(icaoOrName)
            || (alternate.VisibilityMeters is null && alternate.CeilingFt is null && alternate.WindSpeedKt is null))
        {
            return null;
        }

        var parts = new List<string>();
        if (alternate.VisibilityMeters is { } visibility)
        {
            parts.Add($"visibility {Visibility(visibility)}");
        }

        parts.Add(alternate.CeilingFt is { } ceiling
            ? $"ceiling {ceiling.ToString(CultureInfo.InvariantCulture)} feet"
            : "no ceiling");
        if (alternate is { WindSpeedKt: { } speed })
        {
            parts.Add(alternate.WindDirDeg is { } direction
                ? $"wind {direction:000} at {speed} knots"
                : $"wind {speed} knots");
        }

        return $"Alternate {icaoOrName}: {string.Join(", ", parts)}.";
    }
}
