using System.Globalization;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Speech.Persona;

/// <summary>What one small-talk line may draw on (issue #195). Everything optional; a
/// missing value picks a plainer line. Nothing here is operational.</summary>
/// <param name="FoName">The roster FO's name (Crew).</param>
/// <param name="FoBase">The roster FO's base ICAO, spoken form (Crew).</param>
/// <param name="FoHours">Hours on type to the nearest hundred; 0 = unknown (Crew).</param>
/// <param name="Confidence">nervous | steady | assured — the bank picks its register by it.</param>
/// <param name="DestinationCity">The destination's name (Layover).</param>
/// <param name="TownName">A town near the aircraft (View).</param>
/// <param name="TownSide">"left" / "right" / "ahead" / "behind" for that town (View).</param>
/// <param name="SunSide">Which side the sun is on (View); null without a sim clock.</param>
/// <param name="SunElevationDeg">The sun's elevation; low and positive is the golden hour.</param>
/// <param name="Morning">The sim's local morning (sunrise rather than sunset words).</param>
/// <param name="Sky">The local weather card's sky.</param>
public sealed record SmallTalkContext(
    string? FoName = null,
    string? FoBase = null,
    int FoHours = 0,
    string Confidence = "steady",
    string? DestinationCity = null,
    string? TownName = null,
    string? TownSide = null,
    string? SunSide = null,
    double? SunElevationDeg = null,
    bool Morning = false,
    SkyCondition Sky = SkyCondition.Unknown)
{
    public static SmallTalkContext Empty { get; } = new();
}

/// <summary>
/// The deterministic bank behind each small-talk kind (issue #195) and the model's prompt
/// families. The bank line is the floor: it speaks when the model is off, refused, slow or
/// wrong about a number. Pure.
/// </summary>
public static class SmallTalkBank
{
    /// <summary>The FO's one-sentence floor after a layover reply when the model has nothing.</summary>
    public const string FollowUpFloor = "Good to know, captain.";

    /// <summary>The purser's line a yes to the offer ends in, on the interphone.</summary>
    public const string PurserCoffeeLine = "Flight deck, cabin — coffee's on its way up to you.";

    private static readonly string[] OfferLines =
    [
        "Coffee, captain?",
        "Fancy a coffee, captain? I'll get the cabin to send something up.",
        "Shall I get the cabin to send a couple of coffees up, captain?",
    ];

    private static readonly string[] LayoverLines =
    [
        "Do you know {0} at all, captain?",
        "Been to {0} before, captain?",
        "{0} tonight, captain — any tips for the layover?",
    ];

    /// <summary>The bank line for the kind — a random pick where there are several, the
    /// context's plainer form where a value is missing.</summary>
    public static string Line(SmallTalkKind kind, SmallTalkContext context, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roll);
        return kind switch
        {
            SmallTalkKind.Offer => Pick(OfferLines, roll),
            SmallTalkKind.View => ViewLine(context),
            SmallTalkKind.Layover => string.Format(CultureInfo.InvariantCulture, Pick(LayoverLines, roll), context.DestinationCity ?? "the destination"),
            SmallTalkKind.Crew => CrewLine(context),
            _ => "",
        };
    }

    /// <summary>The figures a bank line carries (the allowed set the model's version must keep to).</summary>
    public static IReadOnlyList<double> Numbers(SmallTalkKind kind, SmallTalkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return kind == SmallTalkKind.Crew && context.FoHours > 0
            ? [context.FoHours, Math.Round(context.FoHours / 1000.0, 1), Math.Round(context.FoHours / 100.0)]
            : [];
    }

    /// <summary>"Nice view off the left today" and its better-informed variants.</summary>
    public static string ViewLine(SmallTalkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SunElevationDeg is { } elevation && elevation > -3 && elevation < 10 && context.SunSide is { } sunSide)
        {
            return context.Morning
                ? $"Nice sunrise coming up off the {sunSide}, captain."
                : $"Sun's getting low off the {sunSide} — nice light on the clouds.";
        }

        if (context.Sky is SkyCondition.Overcast or SkyCondition.Rain)
        {
            return "Solid deck below us today — nothing to see down there.";
        }

        if (context.TownName is { } town)
        {
            var side = context.TownSide is "left" or "right" ? "off the " + context.TownSide : "down there";
            return $"That'll be {town} {side}, captain — nice view today.";
        }

        return "Nice view off the left today, captain.";
    }

    /// <summary>The FO about themselves — from the roster record and nothing else.</summary>
    public static string CrewLine(SmallTalkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hours = context.FoHours > 0 ? $"about {context.FoHours.ToString(CultureInfo.InvariantCulture)} hours on the type now" : null;
        var based = context.FoBase is { Length: > 0 } b ? $"I'm based at {b}" : null;
        var nervous = string.Equals(context.Confidence, "nervous", StringComparison.OrdinalIgnoreCase);
        return (based, hours) switch
        {
            (not null, not null) => nervous
                ? $"{based}, captain — {hours}, still learning something every sector."
                : $"{based}, captain — {hours}.",
            (not null, null) => $"{based}, captain.",
            (null, not null) => nervous
                ? $"{char.ToUpperInvariant(hours[0]) + hours[1..]} for me, captain — still learning something every sector."
                : $"{char.ToUpperInvariant(hours[0]) + hours[1..]} for me, captain.",
            _ => "Not much to tell about me, captain — happy to be here.",
        };
    }

    /// <summary>The model's instructions per kind: in character, one or two short spoken
    /// sentences, nothing about this flight beyond the facts given, never an instruction to the
    /// captain; the Crew record is the whole biography.</summary>
    public static string SystemPrompt(SmallTalkKind kind, string personaFragment)
    {
        var family = kind switch
        {
            SmallTalkKind.Offer => "Offer the Captain a coffee or a drink from the cabin, as a light aside. ONE short question.",
            SmallTalkKind.View => "Remark on the view outside — the place, the light, the clouds — from the NOTES given. One or two short sentences.",
            SmallTalkKind.Layover => "Ask the Captain, in one short sentence, whether they know the destination city named in the NOTES, or what it is like.",
            SmallTalkKind.Crew => "Say something about yourself. Your ENTIRE biography is the RECORD in the NOTES — base and hours on type — and nothing else; invent no past, no family, no other airline. One or two short sentences.",
            _ => "Say something light.",
        };
        return (personaFragment ?? "")
            + "You are the First Officer of an Airbus A320 in the cruise, making light conversation with the "
            + "Captain. " + family + " Stay in character; plain spoken English for text-to-speech — no markdown, "
            + "no lists, no emoji, no preamble. Use only the FACTS and NOTES given; never add a figure of your "
            + "own, and never say anything about this flight's fuel, weights, speeds, altitudes, times, weather "
            + "or route unless it is in the FACTS. Never tell the Captain to do anything.";
    }

    /// <summary>The user turn: the notes for the kind and the bank line as the shape to keep.</summary>
    public static string UserPrompt(SmallTalkKind kind, SmallTalkContext context, string bankLine, string facts)
    {
        ArgumentNullException.ThrowIfNull(context);
        var notes = kind switch
        {
            SmallTalkKind.View => string.Join("; ", new[]
            {
                context.TownName is { } t ? $"town nearby: {t}" + (context.TownSide is { } s ? $" ({s})" : "") : null,
                context.SunElevationDeg is { } e ? $"sun elevation {Math.Round(e)} degrees" + (context.SunSide is { } ss ? $", off the {ss}" : "") : null,
                context.Sky != SkyCondition.Unknown ? $"sky: {context.Sky}" : null,
                context.Morning ? "local morning" : "local afternoon or evening",
            }.Where(n => n is not null)),
            SmallTalkKind.Layover => $"destination city: {context.DestinationCity ?? "(unknown)"}",
            SmallTalkKind.Crew => $"RECORD — name: {context.FoName ?? "(unnamed)"}; base: {context.FoBase ?? "(none on record)"}; hours on type: {(context.FoHours > 0 ? context.FoHours.ToString(CultureInfo.InvariantCulture) : "(none on record)")}",
            _ => "(none)",
        };
        return "NOTES: " + (string.IsNullOrWhiteSpace(notes) ? "(none)" : notes)
            + (string.IsNullOrWhiteSpace(facts) ? "" : "\nFACTS:\n" + facts)
            + "\nSHAPE (say it your own way, keep the meaning): " + bankLine
            + "\n\nSay it now.";
    }

    /// <summary>The one-sentence follow-up after the captain's reply to a layover line.</summary>
    public static string FollowUpSystemPrompt(string personaFragment)
        => (personaFragment ?? "")
            + "You are the First Officer of an Airbus A320 in the cruise. The Captain just answered your "
            + "small talk. Reply with EXACTLY ONE short spoken sentence — warm, in character, no question "
            + "back, no figures, nothing about this flight. Plain English for text-to-speech.";

    public static string FollowUpUserPrompt(string yourLine, string captainSaid)
        => "YOU SAID: " + yourLine + "\nCAPTAIN SAID: " + captainSaid + "\n\nReply with one sentence now.";

    private static string Pick(string[] lines, Func<double> roll)
        => lines[Math.Min(lines.Length - 1, (int)(Math.Clamp(roll(), 0, 0.999999) * lines.Length))];
}
