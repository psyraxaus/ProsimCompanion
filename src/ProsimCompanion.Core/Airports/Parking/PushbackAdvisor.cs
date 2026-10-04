using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>What the pilot wants from the push, however it was said.</summary>
public enum PushbackWish
{
    /// <summary>No preference yet.</summary>
    None,

    /// <summary>GSX's LEFT slot — "Nose Right/Tail Left".</summary>
    TailLeft,

    /// <summary>GSX's RIGHT slot — "Nose Left/Tail Right".</summary>
    TailRight,

    /// <summary>"Straight pushback" (no turn).</summary>
    Straight,

    /// <summary>End up facing a compass direction ("push back facing north") —
    /// <see cref="PushbackChoice.HeadingDeg"/> carries the bearing.</summary>
    Heading,

    /// <summary>A named slot ("Facing South (V2)") — <see cref="PushbackChoice.Label"/> carries it.</summary>
    Slot,
}

/// <summary>The pilot's (or the advisor's) pushback decision for this flight.</summary>
/// <param name="Wish">What was asked.</param>
/// <param name="HeadingDeg">Compass bearing for <see cref="PushbackWish.Heading"/>.</param>
/// <param name="Label">Slot label for <see cref="PushbackWish.Slot"/>.</param>
/// <param name="Source">Who decided: "voice", "web", "suggestion", "api".</param>
/// <param name="Reason">One line for the decision log.</param>
public sealed record PushbackChoice(PushbackWish Wish, double? HeadingDeg, string? Label, string Source, string Reason)
{
    public static PushbackChoice TailLeft(string source, string reason) => new(PushbackWish.TailLeft, null, null, source, reason);
    public static PushbackChoice TailRight(string source, string reason) => new(PushbackWish.TailRight, null, null, source, reason);
    public static PushbackChoice Straight(string source, string reason) => new(PushbackWish.Straight, null, null, source, reason);
    public static PushbackChoice Facing(double headingDeg, string source, string reason) => new(PushbackWish.Heading, headingDeg, null, source, reason);
    public static PushbackChoice NamedSlot(string label, string source, string reason) => new(PushbackWish.Slot, null, label, source, reason);

    /// <summary>"tail left" / "tail right" / "straight" / "facing north-east" / the slot label.</summary>
    public string Spoken => Wish switch
    {
        PushbackWish.TailLeft => "tail left",
        PushbackWish.TailRight => "tail right",
        PushbackWish.Straight => "straight back",
        PushbackWish.Heading when HeadingDeg is { } h => $"facing {Compass.Name(h)}",
        PushbackWish.Slot when Label is { Length: > 0 } => Label,
        _ => "no preference",
    };
}

/// <summary>One line of the direction menu as the advisor understands it.</summary>
/// <param name="Label">Menu text (profile label or GSX default).</param>
/// <param name="Kind">Left/Right/Additional slot, or Straight.</param>
/// <param name="FinalHeadingDeg">Where the nose points at release, when it can be estimated.</param>
/// <param name="HeadingSource">"profile" (ini final position), "label" (compass word in the
/// text), "geometry" (±90° from the stand heading), or null when unknown.</param>
public sealed record PushbackOption(string Label, PushbackOptionKind Kind, double? FinalHeadingDeg, string? HeadingSource);

public enum PushbackOptionKind
{
    Left,
    Right,
    Additional,
    Straight,
}

public enum PushbackConfidence
{
    /// <summary>One option fits clearly (or it is the only one).</summary>
    High,

    /// <summary>Two options are close; the suggestion is a lean, not a call.</summary>
    Low,
}

/// <summary>The advisor's recommendation for this stand and runway.</summary>
public sealed record PushbackSuggestion(PushbackOption Option, PushbackConfidence Confidence, string Reason, double? BearingToRunwayDeg)
{
    /// <summary>The wish that selects this option.</summary>
    public PushbackChoice AsChoice(string source) => Option.Kind switch
    {
        PushbackOptionKind.Left => PushbackChoice.TailLeft(source, Reason),
        PushbackOptionKind.Right => PushbackChoice.TailRight(source, Reason),
        PushbackOptionKind.Straight => PushbackChoice.Straight(source, Reason),
        _ => PushbackChoice.NamedSlot(Option.Label, source, Reason),
    };
}

/// <summary>Compass words ↔ bearings.</summary>
public static class Compass
{
    private static readonly (string Word, double Deg)[] Points =
    [
        ("north", 0), ("north-east", 45), ("east", 90), ("south-east", 135),
        ("south", 180), ("south-west", 225), ("west", 270), ("north-west", 315),
    ];

    private static readonly Regex WordPattern = new(
        @"\b(?<w>north[\s-]?east|north[\s-]?west|south[\s-]?east|south[\s-]?west|north|south|east|west|NE|NW|SE|SW|N|S|E|W)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The eight-point name of a bearing ("north-east").</summary>
    public static string Name(double bearingDeg)
    {
        var index = (int)Math.Round(Normalize(bearingDeg) / 45.0) % 8;
        return Points[index].Word;
    }

    /// <summary>The first compass word in the text ("Facing SW on Taxi AV" → 225,
    /// "push back facing north east" → 45); null when none. Single letters count only in
    /// upper case ("S" in "Taxi S" would otherwise read as south; GSX's auto-labels write
    /// "facing S" in upper case).</summary>
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match m in WordPattern.Matches(text))
        {
            var w = m.Groups["w"].Value;
            var key = w.ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal);
            switch (key)
            {
                case "northeast": key = "north-east"; break;
                case "northwest": key = "north-west"; break;
                case "southeast": key = "south-east"; break;
                case "southwest": key = "south-west"; break;
            }

            if (w.Length <= 2)
            {
                if (!w.All(char.IsUpper))
                {
                    continue;
                }

                key = w switch
                {
                    "N" => "north", "S" => "south", "E" => "east", "W" => "west",
                    "NE" => "north-east", "NW" => "north-west", "SE" => "south-east", "SW" => "south-west",
                    _ => key,
                };
            }

            foreach (var (word, deg) in Points)
            {
                if (word == key)
                {
                    return deg;
                }
            }
        }

        return null;
    }

    /// <summary>Smallest angle between two bearings, 0–180.</summary>
    public static double Difference(double a, double b)
    {
        var d = Math.Abs(Normalize(a) - Normalize(b)) % 360;
        return d > 180 ? 360 - d : d;
    }

    public static double Normalize(double deg)
    {
        var d = deg % 360;
        return d < 0 ? d + 360 : d;
    }
}

/// <summary>
/// Pure pushback-direction logic. Three jobs: describe the options a stand offers (from the
/// GSX profile's slots or from the live menu lines), rank them against the departure runway,
/// and map a pilot's wish ("tail left", "facing north", a slot name) onto one of them.
/// Headings come from the profile's final position when it has one, else from a compass
/// word in the label ("Facing SW on Taxi AV", GSX's own auto-label "On Taxiway A, facing S"),
/// else from geometry: GSX's LEFT slot ("Nose Right/Tail Left") turns the nose right, so the
/// aircraft ends roughly 90° right of the stand heading; the RIGHT slot 90° left (manual p.23).
/// </summary>
public static class PushbackAdvisor
{
    /// <summary>GSX's default menu texts (manual p.24).</summary>
    public const string DefaultLeftLabel = "Nose Right/Tail Left (LEFT)";
    public const string DefaultRightLabel = "Nose Left/Tail Right (RIGHT)";

    /// <summary>Below this separation between the best and second-best option the suggestion
    /// is only a lean.</summary>
    public const double LowConfidenceSeparationDeg = 30;

    /// <summary>The options for a stand. The live menu lines win when given (they are what
    /// GSX will accept); the profile adds final headings to lines it knows by label.</summary>
    public static IReadOnlyList<PushbackOption> Options(AirportParking? stand, IReadOnlyList<string>? menuEntries)
    {
        var standHeading = stand?.Pose?.HeadingDeg;
        var slots = stand?.Pushback?.Slots ?? [];
        var options = new List<PushbackOption>();

        if (menuEntries is { Count: > 0 })
        {
            foreach (var entry in menuEntries)
            {
                if (string.IsNullOrWhiteSpace(entry) || IsMetaLine(entry))
                {
                    continue;
                }

                var kind = KindOf(entry);
                if (kind is null)
                {
                    continue;
                }

                var slot = slots.FirstOrDefault(s => ParkingText.Normalize(s.Label) == ParkingText.Normalize(entry));
                options.Add(Build(entry.Trim(), kind.Value, slot?.FinalPose?.HeadingDeg, standHeading));
            }

            return options;
        }

        foreach (var slot in slots)
        {
            var kind = slot.Kind switch
            {
                PushbackSlotKind.Left => PushbackOptionKind.Left,
                PushbackSlotKind.Right => PushbackOptionKind.Right,
                _ => PushbackOptionKind.Additional,
            };
            options.Add(Build(slot.Label, kind, slot.FinalPose?.HeadingDeg, standHeading));
        }

        if (options.Count == 0)
        {
            // Nothing known: GSX's two defaults.
            options.Add(Build(DefaultLeftLabel, PushbackOptionKind.Left, null, standHeading));
            options.Add(Build(DefaultRightLabel, PushbackOptionKind.Right, null, standHeading));
        }

        return options;
    }

    /// <summary>Ranks the options toward the runway. Null when there is nothing to choose
    /// (no options) or no bearing can be computed and no option is alone.</summary>
    public static PushbackSuggestion? Suggest(IReadOnlyList<PushbackOption> options, GeoPoint? standPosition, RunwayGeometry? runway)
    {
        ArgumentNullException.ThrowIfNull(options);
        var turning = options.Where(o => o.Kind != PushbackOptionKind.Straight).ToList();
        if (turning.Count == 0)
        {
            return null;
        }

        if (turning.Count == 1)
        {
            var only = turning[0];
            return new PushbackSuggestion(only, PushbackConfidence.High, $"the stand offers one direction: {only.Label}", null);
        }

        if (standPosition is null || runway is null)
        {
            return null;
        }

        var bearing = GreatCircle.InitialBearingDeg(standPosition.Value, runway.Threshold);
        var ranked = turning
            .Where(o => o.FinalHeadingDeg is not null)
            .Select(o => (Option: o, Off: Compass.Difference(o.FinalHeadingDeg!.Value, bearing)))
            .OrderBy(x => x.Off)
            .ToList();
        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        var confidence = ranked.Count > 1 && ranked[1].Off - best.Off < LowConfidenceSeparationDeg
            ? PushbackConfidence.Low
            : PushbackConfidence.High;
        var reason = string.Create(CultureInfo.InvariantCulture,
            $"faces {Compass.Name(best.Option.FinalHeadingDeg!.Value)} ({best.Option.FinalHeadingDeg:0}°), runway {runway.Ident} bears {bearing:0}° from the stand ({best.Option.HeadingSource} heading)");
        if (confidence == PushbackConfidence.Low)
        {
            reason += $"; {ranked[1].Option.Label} is close ({ranked[1].Off:0}° vs {best.Off:0}° off)";
        }

        return new PushbackSuggestion(best.Option, confidence, reason, bearing);
    }

    /// <summary>The option a wish selects, or null when none fits (then the menu stays with
    /// the pilot). Compass wishes accept an option within 45° of the asked bearing.</summary>
    public static PushbackOption? Match(IReadOnlyList<PushbackOption> options, PushbackChoice choice)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(choice);
        switch (choice.Wish)
        {
            case PushbackWish.TailLeft:
                return options.FirstOrDefault(o => o.Kind == PushbackOptionKind.Left);
            case PushbackWish.TailRight:
                return options.FirstOrDefault(o => o.Kind == PushbackOptionKind.Right);
            case PushbackWish.Straight:
                return options.FirstOrDefault(o => o.Kind == PushbackOptionKind.Straight);
            case PushbackWish.Slot:
                var wanted = ParkingText.Normalize(choice.Label);
                return wanted.Length == 0
                    ? null
                    : options.FirstOrDefault(o => ParkingText.Normalize(o.Label) == wanted)
                        ?? options.FirstOrDefault(o => ParkingText.Normalize(o.Label).Contains(wanted, StringComparison.Ordinal));
            case PushbackWish.Heading when choice.HeadingDeg is { } asked:
                var candidates = options
                    .Where(o => o.Kind != PushbackOptionKind.Straight && o.FinalHeadingDeg is not null)
                    .Select(o => (Option: o, Off: Compass.Difference(o.FinalHeadingDeg!.Value, asked)))
                    .OrderBy(x => x.Off)
                    .ToList();
                return candidates.Count > 0 && candidates[0].Off <= 45 ? candidates[0].Option : null;
            default:
                return null;
        }
    }

    /// <summary>The slot kind of a menu line: GSX's "(LEFT)"/"(RIGHT)" markers or
    /// "Tail Left"/"Tail Right" words, "Straight", else an additional (custom) slot. Null for
    /// lines that are not directions at all (QuickEdit, Customize…).</summary>
    public static PushbackOptionKind? KindOf(string entry)
    {
        var text = entry.Trim();
        if (text.Contains("Straight", StringComparison.OrdinalIgnoreCase))
        {
            return text.Contains("Pull", StringComparison.OrdinalIgnoreCase) ? null : PushbackOptionKind.Straight;
        }

        if (text.Contains("(LEFT)", StringComparison.OrdinalIgnoreCase) || text.Contains("Tail Left", StringComparison.OrdinalIgnoreCase))
        {
            return PushbackOptionKind.Left;
        }

        if (text.Contains("(RIGHT)", StringComparison.OrdinalIgnoreCase) || text.Contains("Tail Right", StringComparison.OrdinalIgnoreCase))
        {
            return PushbackOptionKind.Right;
        }

        return PushbackOptionKind.Additional;
    }

    private static readonly string[] MetaPrefixes =
        ["QuickEdit", "Customize", "GSX", "Restart", "SimBrief", "Use autosaved", "Back", "Next"];

    public static bool IsMetaLine(string line)
        => string.IsNullOrWhiteSpace(line)
            || MetaPrefixes.Any(prefix => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static PushbackOption Build(string label, PushbackOptionKind kind, double? profileHeading, double? standHeading)
    {
        if (profileHeading is { } fromProfile)
        {
            return new PushbackOption(label, kind, Compass.Normalize(fromProfile), "profile");
        }

        if (Compass.Parse(label) is { } fromLabel)
        {
            return new PushbackOption(label, kind, fromLabel, "label");
        }

        if (standHeading is { } h)
        {
            return kind switch
            {
                PushbackOptionKind.Left => new PushbackOption(label, kind, Compass.Normalize(h + 90), "geometry"),
                PushbackOptionKind.Right => new PushbackOption(label, kind, Compass.Normalize(h - 90), "geometry"),
                PushbackOptionKind.Straight => new PushbackOption(label, kind, Compass.Normalize(h), "geometry"),
                _ => new PushbackOption(label, kind, null, null),
            };
        }

        return new PushbackOption(label, kind, null, null);
    }
}
