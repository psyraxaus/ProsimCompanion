using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>The values an ATC request template can name. Null or blank = unknown.</summary>
/// <param name="Callsign">flight.json <c>callsign</c>.</param>
/// <param name="Gate">The departure stand: flight.json <c>assigned_gate</c>, else the stand GSX
/// loaded (the parking catalogue's display name, "Gate 35" / "Stand 208").</param>
/// <param name="Runway">flight.json <c>flight_plan_departing_runway</c>.</param>
/// <param name="Airport">flight.json <c>current_airport</c>.</param>
/// <param name="Origin">flight.json <c>flight_origin</c>.</param>
/// <param name="Diversion">The diversion destination ICAO while one is declared (issue #184); blank otherwise, so a
/// "request diversion to {diversion}" clause is dropped whole when there is none.</param>
/// <param name="StepLevel">The planned level at the next navlog step ("370") from the step-climb advisor (issue #189); null/empty = no step ahead.</param>
internal sealed record AtcRequestValues(string? Callsign, string? Gate, string? Runway, string? Airport, string? Origin, string? Diversion = null, string? StepLevel = null);

/// <summary>
/// Fills an atc-requests.json template (<c>{callsign} {gate} {runway} {airport} {origin} {diversion}</c>).
/// Issue #140: every request of the 2026-10-10 flight went out as "Ground, Finnair-one-three-
/// six-one, stand , request push and start" because SayIntentions had no stand text yet and
/// the old fill left the label and its comma behind. The rule now: a template is a list of
/// comma-separated clauses, and a clause whose token has no value is dropped whole — label,
/// token and separator — so "stand ," or ", ," can never be transmitted. A stand label
/// ("stand {gate}" / "gate {gate}") also drops a leading "Gate" / "Stand" word from the
/// value, so a GSX-sourced "Gate 208" reads "gate 208", not "gate Gate 208".
/// </summary>
internal static partial class AtcRequestTemplate
{
    [GeneratedRegex(@"\{(?<name>[a-z]+)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ManySpaces();

    public static string Fill(string template, AtcRequestValues values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var clauses = new List<string>();
        foreach (var raw in template.Split(','))
        {
            var clause = raw.Trim();
            if (clause.Length == 0)
            {
                continue;
            }

            var missing = false;
            var filled = TokenPattern().Replace(clause, match =>
            {
                var name = match.Groups["name"].Value.ToLowerInvariant();
                var value = Value(values, name);
                if (value is null)
                {
                    // Unknown token: leave the text as written (the file is user-editable).
                    return match.Value;
                }

                if (value.Length == 0)
                {
                    missing = true;
                    return "";
                }

                // The stand is spelled digit by digit (owner request 2026-10-29: "stand 311"
                // was voiced "three hundred and eleven" by SI's ATC and by the FO alike).
                return name == "gate" ? RadioSpeech.Stand(StripStandLabel(clause, match.Index, value)) : value;
            });

            if (missing)
            {
                continue;
            }

            filled = ManySpaces().Replace(filled, " ").Trim();
            if (filled.Length > 0)
            {
                clauses.Add(filled);
            }
        }

        return string.Join(", ", clauses);
    }

    /// <summary>Known token values; null = not a token this templater knows; "" = unknown value.</summary>
    private static string? Value(AtcRequestValues values, string name) => name switch
    {
        "callsign" => values.Callsign?.Trim() ?? "",
        "gate" => values.Gate?.Trim() ?? "",
        "runway" => values.Runway?.Trim() ?? "",
        "airport" => values.Airport?.Trim() ?? "",
        "origin" => values.Origin?.Trim() ?? "",
        "diversion" => values.Diversion?.Trim() ?? "",
        "steplevel" => values.StepLevel?.Trim() ?? "",
        _ => null,
    };

    /// <summary>"stand {gate}" with "Stand 208" → "208"; "gate {gate}" with "Gate 35" → "35".
    /// Only the label word right before the token counts, and only a leading Gate/Stand word
    /// is removed — "PIER 2 GATE STAND 208" (SayIntentions' own text) is left alone.</summary>
    private static string StripStandLabel(string clause, int tokenIndex, string value)
    {
        var before = clause[..tokenIndex].TrimEnd();
        var lastSpace = before.LastIndexOf(' ');
        var label = lastSpace >= 0 ? before[(lastSpace + 1)..] : before;
        if (!label.Equals("stand", StringComparison.OrdinalIgnoreCase)
            && !label.Equals("gate", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        foreach (var prefix in new[] { "gate ", "stand " })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.Length > prefix.Length)
            {
                return value[prefix.Length..].Trim();
            }
        }

        return value;
    }
}
