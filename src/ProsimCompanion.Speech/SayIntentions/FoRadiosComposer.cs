using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>What kind of line the FO transmits in answer to ATC.</summary>
public enum ReadbackKind
{
    /// <summary>A plain read-back of the instruction.</summary>
    Readback,

    /// <summary>"Contacting &lt;station&gt; on &lt;freq&gt;." — followed by the tune and the check-in.</summary>
    Handoff,
}

/// <summary>One FO transmission: the text for sayAs and the text for the FO's own voice.</summary>
public sealed record Readback(string Transmit, string Spoken, ReadbackKind Kind, string? HandoffStation = null, int? HandoffKhz = null);

/// <summary>
/// Composes the FO's read-back of a SayIntentions ATC line (owner request 2026-10-25: the FO
/// works the radios in one voice instead of the SI copilot). Rule-based on the copilot's own
/// transcript of the 2026-10-25 EKCH→EFHK leg: the read-back is the instruction with the
/// callsign, the station's self-identification, "Identified", greetings and the winds removed,
/// "Flight-level-140" written FL140, "A1" spoken Alpha-one, "hold-short-of" un-hyphenated —
/// and a hand-off answered "Contacting Tower on 119.355." A line with nothing left to read
/// back ("Identified. Good evening") yields null. Pure and deterministic.
/// </summary>
public static partial class ReadbackComposer
{
    [GeneratedRegex(@"\bflight[- ]level[- ](\d{2,3})\b|\bFL\s?(\d{2,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlightLevel();

    [GeneratedRegex(@",?\s*\bwinds?\b[^,.]*?(\d{3})\s+at\s+\d+(?:\s+gusting\s+\d+)?(?:\s+knots)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Winds();

    // "Winds calm" / "wind light and variable" (EGLL 2026-10-29: "Runway 27R, Winds calm, cleared
    // for takeoff" was read back winds and all — the digit form above never matched).
    [GeneratedRegex(@",?\s*\bwinds?\s+(?:calm|light and variable|variable)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindsCalm();

    // A thousands separator ("6,000") must survive the comma split below (EGLL 2026-10-29:
    // "climb altitude 6,000" went out as "Climb altitude 6, 000.").
    [GeneratedRegex(@"(\d),(\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Thousands();

    // Broadcasts are not addressed to us and get no read-back (EGLL 2026-10-29: the FO read
    // "All aircraft be advised, information Sierra is now current. QNH 1028" back as "QNH 1028").
    [GeneratedRegex(@"^\s*(?:attention[,\s]+)?all\s+(?:aircraft|stations)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Broadcast();

    private const char ThousandsMark = '\u0001';

    [GeneratedRegex(@"\b([A-Z])(\d{1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Taxiway();

    [GeneratedRegex(@"\b(?:contact|monitor)\s+(?<station>[^,.;:\d]+?)\s+(?:on\s+)?(?<freq>1[1-3]\d\.\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Handoff();

    private static readonly string[] NoisePrefixes =
    [
        "identified", "radar contact", "good morning", "good afternoon", "good evening", "good day",
        "hello", "hej", "god kvall", "god kväll", "god morgon", "guten tag", "bonjour", "hallo",
        "have a good", "have a nice", "welcome", "roger", "thank you", "thanks", "copy",
    ];

    /// <summary>The read-back of <paramref name="atcText"/>, or null when nothing needs one.
    /// Every read-back and hand-off answer ENDS with the callsign (owner request 2026-10-29,
    /// standard RT: "Push and start approved, face South-East, Speedbird-eight-five-zero.") — a
    /// blank callsign leaves the line as it was.</summary>
    /// <param name="atcText">ATC's line as the comms history gives it (the English form).</param>
    /// <param name="callsign">Our callsign as SayIntentions writes it ("Finnair-niner-six-two").</param>
    /// <param name="station">The station's long name when known ("Kastrup Tower") — stripped
    /// from the front of the instruction, where SI ATC identifies itself on a check-in reply.</param>
    public static Readback? Compose(string atcText, string? callsign, string? station)
    {
        if (string.IsNullOrWhiteSpace(atcText))
        {
            return null;
        }

        var text = atcText.Trim();
        if (Broadcast().IsMatch(text))
        {
            return null; // "All aircraft be advised …" — an ATIS-style broadcast, not for us
        }

        if (Handoff().Match(text) is { Success: true } handoff
            && HandoffParser.ToKhz(handoff.Groups["freq"].Value) is { } khz)
        {
            var name = TitleCase(handoff.Groups["station"].Value.Trim());
            var transmit = WithCallsign($"Contacting {name} on {handoff.Groups["freq"].Value}.", callsign);
            return new Readback(transmit, RadioSpeech.Spoken(transmit), ReadbackKind.Handoff, name, khz);
        }

        var clauses = new List<string>();
        foreach (var raw in SplitClauses(text))
        {
            var clause = StripCallsign(raw, callsign);
            clause = StripStation(clause, station);
            clause = Winds().Replace(clause, "");
            clause = WindsCalm().Replace(clause, "");
            clause = Thousands().Replace(clause, $"$1{ThousandsMark}$2");

            // Segment by comma: greetings and "Identified" go; an unknown station's
            // self-identification ("Kastrup Tower", first, short, no digits) goes too.
            var segments = clause.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Replace(ThousandsMark, ','))
                .Where(s => s.Length > 0 && !IsNoise(s))
                .ToList();
            if (segments.Count > 1 && !LooksLikeInstruction(segments[0]) && segments[0].Split(' ').Length <= 3)
            {
                segments.RemoveAt(0);
            }

            if (segments.Count == 0)
            {
                continue;
            }

            var rejoined = string.Join(", ", segments);
            if (!LooksLikeInstruction(rejoined))
            {
                continue; // "Kastrup Departure" alone — a check-in reply, nothing to read back
            }

            clauses.Add(Normalize(rejoined));
        }

        if (clauses.Count == 0)
        {
            return null;
        }

        var joined = WithCallsign(string.Join(" ", clauses), callsign);
        return new Readback(joined, RadioSpeech.Spoken(joined), ReadbackKind.Readback);
    }

    /// <summary>"Climb FL140." + "Speedbird-eight-five-zero" → "Climb FL140, Speedbird-eight-five-zero."
    /// The callsign closes the transmission the way a crew does; a blank callsign changes nothing.</summary>
    internal static string WithCallsign(string transmit, string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign) || string.IsNullOrWhiteSpace(transmit))
        {
            return transmit;
        }

        var body = transmit.TrimEnd().TrimEnd('.', ',', ';');
        return $"{body}, {callsign.Trim()}.";
    }

    /// <summary>Splits on sentence ends; a clause that is only a greeting or "Identified" is
    /// dropped later, so "Kastrup Departure, Identified. climb FL070" keeps only the climb.</summary>
    private static IEnumerable<string> SplitClauses(string text)
    {
        foreach (var part in text.Split(['.', ';', '!'], StringSplitOptions.RemoveEmptyEntries))
        {
            // A frequency's decimal point splits too ("119.355") — re-join digit fragments.
            yield return part.Trim();
        }
    }

    private static string StripCallsign(string clause, string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return clause;
        }

        var index = clause.IndexOf(callsign.Trim(), StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return clause;
        }

        var without = clause.Remove(index, callsign.Trim().Length);
        return without.Trim().TrimStart(',').Trim();
    }

    private static string StripStation(string clause, string? station)
    {
        if (string.IsNullOrWhiteSpace(station))
        {
            return clause;
        }

        var trimmed = clause.Trim();
        var name = station.Trim();
        if (trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[name.Length..].TrimStart(',', ' ');
        }

        return trimmed;
    }

    private static readonly string[] InstructionWords =
    [
        "climb", "descend", "maintain", "taxi", "cross", "hold", "cleared", "contact", "monitor", "squawk", "turn",
        "reduce", "increase", "expect", "line up", "push", "runway", "heading", "speed", "direct", "proceed",
        "report", "continue", "stop", "give way", "follow", "face", "vacate", "expedite", "fly", "intercept",
        "qnh", "altimeter", "approved", "stand by", "standby", "hold short", "backtrack", "join", "leave", "resume",
    ];

    /// <summary>A digit or an ATC verb — the mark of something to read back, as opposed to a
    /// station naming itself or a pleasantry.</summary>
    private static bool LooksLikeInstruction(string text)
    {
        if (text.Any(char.IsDigit))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        return InstructionWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }

    private static bool IsNoise(string clause)
    {
        var lower = clause.ToLowerInvariant();
        foreach (var prefix in NoisePrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                // "Good evening" alone is noise; "good evening, climb FL070" is not — the
                // instruction after the greeting survives in the remainder.
                var rest = lower[prefix.Length..].Trim(',', ' ', '.');
                if (rest.Length == 0)
                {
                    return true;
                }

                if (!rest.Any(char.IsDigit) && rest.Split(' ').Length <= 3 && !rest.Contains("runway", StringComparison.Ordinal))
                {
                    return true; // "Identified Hej" / "have a good evening"
                }
            }
        }

        return false;
    }

    /// <summary>The copilot's spellings: FL140, "hold short of", "Cross runway", "Alpha-one".</summary>
    private static string Normalize(string clause)
    {
        var text = clause;
        // A greeting or "Identified" glued to the front of the instruction ("Identified. climb"
        // splits at the dot; "Identified, climb FL070" does not).
        foreach (var prefix in NoisePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = text[prefix.Length..].TrimStart(',', ' ');
                if (rest.Length > 0)
                {
                    text = rest;
                }

                break;
            }
        }

        text = FlightLevel().Replace(text, m =>
        {
            var digits = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return "FL" + int.Parse(digits, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        });
        text = text.Replace("hold-short-of", "hold short of", StringComparison.OrdinalIgnoreCase)
            .Replace("hold-short", "hold short", StringComparison.OrdinalIgnoreCase)
            .Replace("Cross-runway", "Cross runway", StringComparison.OrdinalIgnoreCase)
            .Replace("cross-runway", "cross runway", StringComparison.OrdinalIgnoreCase)
            .Replace("line-up", "line up", StringComparison.OrdinalIgnoreCase);
        text = Taxiway().Replace(text, m =>
        {
            var letter = Core.Speech.NatoPhonetics.Word(m.Groups[1].Value[0]) ?? m.Groups[1].Value;
            var digits = string.Join("-", m.Groups[2].Value.Select(c => Core.Speech.NatoPhonetics.DigitWords[c - '0']));
            return letter + "-" + digits;
        });
        text = text.Trim();
        if (text.Length > 0)
        {
            text = char.ToUpperInvariant(text[0]) + text[1..];
        }

        if (!text.EndsWith('.'))
        {
            text += ".";
        }

        return text;
    }

    private static string TitleCase(string station)
        => station.Length > 1 && station.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(station.ToLowerInvariant())
            : station;
}

/// <summary>The last level ATC cleared us to, read out of its lines — the "for FL210" half of
/// the FO's check-in. "FL140" / "5,000 feet"; null when the line clears no level.</summary>
public static partial class ClearedLevelParser
{
    [GeneratedRegex(@"\b(?:climb|descend|maintain|initial climb)(?:\s+and\s+maintain)?(?:\s+to)?\s+(?:flight[- ]level[- ](\d{2,3})|FL\s?(\d{2,3})|(\d{1,2}[,.]?\d{3})\s*(?:feet|ft))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Level();

    public static string? Parse(string? atcText)
    {
        if (string.IsNullOrWhiteSpace(atcText))
        {
            return null;
        }

        var match = Level().Match(atcText);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups[1].Success || match.Groups[2].Success)
        {
            var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return "FL" + int.Parse(digits, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        var feet = int.Parse(match.Groups[3].Value.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return feet.ToString("N0", CultureInfo.InvariantCulture) + " feet";
    }
}

/// <summary>What the FO says when it first calls a new station.</summary>
/// <param name="Greeting">"Good evening" by the sim's local clock, or null for none.</param>
/// <param name="Callsign">As SayIntentions writes it ("Finnair-niner-six-two").</param>
/// <param name="AltitudeFt">Current altitude.</param>
/// <param name="ClearedLevel">The last cleared level as <see cref="ClearedLevelParser"/> writes it, or null.</param>
public sealed record CheckInContext(
    string? Greeting,
    string Callsign,
    FlightPhase Phase,
    bool OnGround,
    double AltitudeFt,
    string? ClearedLevel,
    string? Sid,
    string? Star,
    string? DepartureRunway,
    string? ArrivalRunway,
    string? Gate);

/// <summary>
/// The check-in call after a hand-off, shaped on the SI copilot's own (2026-10-25 transcript):
/// "Good evening, climbing through 2,500 on KEMA5C." / "… climbing through FL180 for FL210 on
/// KEMA5C." / "… maintaining FL370." The arrival wording was not in that transcript; descent
/// and approach mirror the climb with the STAR and the runway. Pure.
/// </summary>
public static class CheckInComposer
{
    public static Readback? Compose(CheckInContext c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (string.IsNullOrWhiteSpace(c.Callsign))
        {
            return null;
        }

        var head = c.Greeting is { Length: > 0 } greeting ? $"{greeting}, {c.Callsign}, " : $"{c.Callsign}, ";
        string body;
        if (c.OnGround && c.Phase is FlightPhase.TaxiIn or FlightPhase.LandingRollout or FlightPhase.Shutdown)
        {
            var runway = c.ArrivalRunway is { Length: > 0 } rwy ? $"runway {rwy} vacated, " : "";
            var stand = c.Gate is { Length: > 0 } gate ? $"request taxi to stand {RadioSpeech.Stand(gate)}." : "request taxi.";
            body = runway + stand;
        }
        else if (c.OnGround && c.Phase == FlightPhase.TaxiOut)
        {
            body = c.DepartureRunway is { Length: > 0 } rwy
                ? $"taxiing to holding point runway {rwy}."
                : "taxiing for departure.";
        }
        else if (c.OnGround)
        {
            body = c.Gate is { Length: > 0 } gate ? $"stand {RadioSpeech.Stand(gate)}." : "on the ground.";
        }
        else
        {
            var level = LevelText(c.AltitudeFt);
            var cleared = c.ClearedLevel is { Length: > 0 } cl && !string.Equals(cl, level, StringComparison.Ordinal) ? $" for {cl}" : "";
            switch (c.Phase)
            {
                case FlightPhase.Cruise:
                    body = $"maintaining {level}.";
                    break;
                case FlightPhase.Descent:
                case FlightPhase.Approach:
                    var star = c.Star is { Length: > 0 } s ? $" on {s}" : "";
                    var inbound = c.Phase == FlightPhase.Approach && c.ArrivalRunway is { Length: > 0 } r ? $", inbound runway {r}" : "";
                    body = $"descending through {level}{cleared}{star}{inbound}.";
                    break;
                default:
                    var sid = c.Sid is { Length: > 0 } d ? $" on {d}" : "";
                    body = $"climbing through {level}{cleared}{sid}.";
                    break;
            }
        }

        var transmit = head + body;
        return new Readback(transmit, RadioSpeech.Spoken(transmit), ReadbackKind.Readback);
    }

    /// <summary>"2,500" below 10,000 ft (nearest 100), "FL180" from 10,000 ft (nearest 10).</summary>
    public static string LevelText(double altitudeFt)
    {
        if (altitudeFt >= 10_000)
        {
            var fl = (int)Math.Round(altitudeFt / 1000.0) * 10;
            return "FL" + fl.ToString(CultureInfo.InvariantCulture);
        }

        var feet = (int)Math.Round(Math.Max(0, altitudeFt) / 100.0) * 100;
        return feet.ToString("N0", CultureInfo.InvariantCulture);
    }
}

/// <summary>Turns a transmit text into TTS-safe speech: FL140 → "flight level one four zero",
/// 119.355 → "one one niner decimal three five five", 22R → "two two right", squawk and QNH
/// digit by digit, and the callsign's hyphens → spaces. Everything else is left to the voice.</summary>
public static partial class RadioSpeech
{
    [GeneratedRegex(@"\bFL(\d{2,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Fl();

    [GeneratedRegex(@"\b(1[1-3]\d\.\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Frequency();

    [GeneratedRegex(@"\b(\d{2})([LRC])\b", RegexOptions.CultureInvariant)]
    private static partial Regex RunwayDesignator();

    [GeneratedRegex(@"\b(squawk|QNH|heading)\s+(\d{3,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigitGroups();

    [GeneratedRegex(@"^[A-Z]{0,2}\d{1,3}[A-Z]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactStand();

    /// <summary>A stand or gate designator spelled digit by digit for the radio (owner request
    /// 2026-10-29: SayIntentions' ATC voice and the FO both read "stand 311" as "three hundred
    /// and eleven"): "311" → "three-one-one", "546R" → "five-four-six-Romeo", "B16" →
    /// "Bravo-one-six" — SI's own hyphenated callsign style, which <see cref="Spoken"/> turns
    /// into spaces for the FO voice. Anything longer than a compact designator (SI's
    /// "PIER 2 GATE STAND 208", a blank) is returned as written.</summary>
    public static string Stand(string? stand)
    {
        if (string.IsNullOrWhiteSpace(stand))
        {
            return stand ?? "";
        }

        var compact = stand.Trim().ToUpperInvariant();
        if (!CompactStand().IsMatch(compact))
        {
            return stand.Trim();
        }

        var words = new List<string>(compact.Length);
        foreach (var c in compact)
        {
            words.Add(c is >= '0' and <= '9'
                ? Core.Speech.NatoPhonetics.DigitWords[c - '0']
                : Core.Speech.NatoPhonetics.Word(c) ?? c.ToString());
        }

        return string.Join("-", words);
    }

    public static string Spoken(string transmit)
    {
        ArgumentNullException.ThrowIfNull(transmit);

        var text = Fl().Replace(transmit, m => "flight level " + Callouts.Aviation.ToDigits(m.Groups[1].Value));
        text = Frequency().Replace(text, m => Callouts.Aviation.ToDigits(m.Groups[1].Value));
        text = RunwayDesignator().Replace(text, m => Callouts.Aviation.ToDigits(m.Groups[1].Value) + " " + m.Groups[2].Value switch
        {
            "L" => "left",
            "R" => "right",
            _ => "center",
        });
        text = DigitGroups().Replace(text, m => m.Groups[1].Value + " " + Callouts.Aviation.ToDigits(m.Groups[2].Value));

        // The callsign as SI writes it is already spoken digits joined by hyphens.
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sb.Append(c == '-' && i > 0 && i + 1 < text.Length && char.IsLetter(text[i - 1]) && char.IsLetter(text[i + 1]) ? ' ' : c);
        }

        return sb.ToString();
    }
}
