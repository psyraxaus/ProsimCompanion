using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Everything a briefing may speak — null fields are simply omitted. ATIS letter and
/// active runway (SayIntentions-sourced, via the composite weather provider) feed the LLM
/// fact block only; the deterministic template deliberately ignores them so its clause
/// structure stays byte-stable. AirportName is the spoken name ("Sydney") resolved by
/// <see cref="Core.Airports.IAirportNames"/> — null falls back to the ICAO ident.</summary>
public sealed record BriefingFacts(
    bool IsDeparture,
    string? Airport,
    string? Runway,
    string? Sid,
    string? Star,
    string? Approach,
    NavDataFacts Nav,
    int? V1,
    int? Vr,
    int? V2,
    int? WindDirDeg,
    int? WindSpeedKt,
    int? QnhHpa,
    ArrivalMinima? Minima,
    string? AtisLetter = null,
    string? ActiveRunway = null,
    int? FlexTempC = null,
    int? VisibilityM = null,
    int? TemperatureC = null,
    string? AirportName = null);

/// <summary>
/// The deterministic briefing template (Prosim2FO's exact clause structure) plus the number
/// verifier that keeps optional LLM prose honest: every significant number in a narrative
/// (≥3 digits or decimal) must appear in the source facts within 0.06 — 1–2 digit tokens are
/// deliberately ignored (runways/flaps/ordinals false-positive).
/// </summary>
public static class BriefingComposer
{
    /// <summary>The whole template as one text: the sections joined by a space.</summary>
    public static string Template(BriefingFacts f)
        => string.Join(" ", Sections(f).Select(section => section.Text));

    /// <summary>The template clause by clause (issue #147), each with the figures and words
    /// that identify it in other wording — see <see cref="Core.Speech.NarrationSection"/>. A
    /// streamed LLM briefing that is cut short is finished with exactly the clauses the pilot
    /// has not heard yet. The clause texts and their order are the template, unchanged.</summary>
    public static IReadOnlyList<Core.Speech.NarrationSection> Sections(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var s = new List<Core.Speech.NarrationSection>();
        if (f.IsDeparture)
        {
            s.Add(new("header", "Departure briefing.") { IsOpening = true });
            if (f.Airport is { } airport)
            {
                s.Add(new("airport", $"Departing {f.AirportName ?? airport}.") { AnyOf = Names(f.AirportName, airport) });
            }

            if (f.Runway is { } runway)
            {
                s.Add(new("runway", $"Runway {runway}.") { Runway = runway });
            }

            // Identifiers render phonetically (issue #68): kokoro swallowed the designator
            // letter of "VOLA3V" — "VOLA three Victor" survives synthesis.
            if (f.Sid is { } sid)
            {
                var spoken = Core.Speech.NatoPhonetics.SpeakIdentifier(sid);
                s.Add(new("sid", $"Standard instrument departure {spoken}.") { AnyOf = LeadWord(spoken) });
            }

            if (f.Nav.RunwayTrueHeading is { } heading)
            {
                s.Add(new("track", $"Initial track {heading:0} degrees.") { Numbers = [Math.Round(heading)], AnyOf = ["track", "heading"] });
            }

            if (f.Nav.TransitionAltitudeFt is { } transitionAltitude)
            {
                s.Add(new("transition-altitude", $"Transition altitude {transitionAltitude:0} feet.")
                {
                    Numbers = [Math.Round(transitionAltitude)],
                    AnyOf = ["transition"],
                });
            }

            if (f is { V1: { } v1, Vr: { } vr, V2: { } v2 })
            {
                s.Add(new("v-speeds", $"V1 {v1}, rotate {vr}, V2 {v2}.") { Numbers = [v1, vr, v2] });
            }
        }
        else
        {
            s.Add(new("header", "Arrival briefing.") { IsOpening = true });
            if (f.Airport is { } airport)
            {
                s.Add(new("airport", $"Arriving {f.AirportName ?? airport}.") { AnyOf = Names(f.AirportName, airport) });
            }

            if (f.Runway is { } runway)
            {
                s.Add(new("runway", $"Runway {runway}.") { Runway = runway });
            }

            if (f.Approach is { } approach)
            {
                var spoken = SpokenApproach(approach, f.Runway);
                s.Add(new("approach", $"Approach {spoken}.") { AnyOf = [spoken] });
            }

            if (f.Nav is { IlsIdent: { } ilsIdent, IlsFrequencyMhz: { } ilsFrequency })
            {
                s.Add(new("ils", $"ILS {ilsIdent}, frequency {ilsFrequency:0.00}.") { Numbers = [ilsFrequency] });
            }

            if (f.Nav.GlideSlopeAngle is { } glideSlope)
            {
                s.Add(new("glideslope", $"Glideslope {glideSlope:0.0} degrees.") { Numbers = [glideSlope], AnyOf = ["glide"] });
            }

            if (f.Star is { } star)
            {
                var spoken = Core.Speech.NatoPhonetics.SpeakIdentifier(star);
                s.Add(new("star", $"Arrival via {spoken}.") { AnyOf = LeadWord(spoken) });
            }

            if (f.Nav.TransitionLevel is { } transitionLevel)
            {
                s.Add(new("transition-level", $"Transition level {transitionLevel:0}.")
                {
                    Numbers = [Math.Round(transitionLevel)],
                    AnyOf = ["transition"],
                });
            }
        }

        if (f is { WindDirDeg: { } windDir, WindSpeedKt: { } windSpeed })
        {
            s.Add(new("wind", $"Wind {windDir:000} at {windSpeed} knots.") { Numbers = [windDir, windSpeed], AnyOf = ["wind"] });
        }

        if (f.QnhHpa is { } qnh)
        {
            s.Add(new("qnh", $"QNH {qnh:0}.") { Numbers = [qnh], AnyOf = ["qnh", "altimeter"] });
        }

        if (!f.IsDeparture)
        {
            s.Add(f.Minima is { } m
                ? new("minimums", $"Minimums, {MinimaCallout(m)}.")
                {
                    Numbers = [Math.Round(m.AltitudeFt)],
                    AnyOf = ["minimum", "decision", "descent altitude"],
                }
                : new("minimums", "Minimums not briefed.") { AnyOf = ["minimum"] });
        }

        return s;
    }

    /// <summary>The names an airport may have been spoken by: its friendly name and its ident.</summary>
    private static string[] Names(string? name, string icao)
        => name is null ? [icao] : [name, icao];

    /// <summary>The leading word of a spoken identifier ("VOLA three Victor" → "VOLA"): the
    /// part a listener recognises the procedure by. Empty when there is none of useful length.</summary>
    private static string[] LeadWord(string spokenIdentifier)
    {
        var lead = spokenIdentifier.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return lead is { Length: >= 3 } ? [lead] : [];
    }

    /// <summary>Spoken form of the approach fact. DFD identifiers ("I16RY") decode through
    /// the same path as the "which approach" answer so both say "ILS Yankee"; anything else
    /// (manual entries like "ILS 16R") renders phonetically. Public so tests can pin both
    /// branches.</summary>
    public static string SpokenApproach(string approach, string? runway)
    {
        ArgumentNullException.ThrowIfNull(approach);
        return runway is not null
            && DfdNavDataProvider.TryDecodeApproachIdentifier(approach, runway, out var option, out _)
                ? option.Spoken
                : Core.Speech.NatoPhonetics.SpeakIdentifier(approach);
    }

    public static string MinimaCallout(ArrivalMinima minima)
    {
        ArgumentNullException.ThrowIfNull(minima);
        var kind = minima.Kind switch
        {
            ArrivalMinimumKind.DecisionAltitude => "decision altitude",
            ArrivalMinimumKind.DecisionHeight => "decision height",
            _ => "minimum descent altitude",
        };
        return $"{kind} {Callouts.Aviation.ToDigits(minima.AltitudeFt.ToString("F0", CultureInfo.InvariantCulture))} feet";
    }

    /// <summary>Checks a narrative's significant numbers against the allowed fact values
    /// (delegating to the shared <see cref="Llm.NumberVerifier"/>). Returns the offending
    /// tokens (empty = verified) and the allowed set for the retry prompt.</summary>
    public static (IReadOnlyList<string> Offending, IReadOnlyList<double> Allowed) VerifyNumbers(
        string narrative, BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(narrative);
        ArgumentNullException.ThrowIfNull(f);

        var check = Llm.NumberVerifier.Check(narrative, BuildAllowed(f));
        return (check.Offending, check.Allowed);
    }

    /// <summary>Every number a briefing built from these facts may speak — the set the
    /// streamed path verifies each sentence against (issue #147).</summary>
    public static IReadOnlyList<double> AllowedNumbers(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return BuildAllowed(f);
    }

    private static List<double> BuildAllowed(BriefingFacts f)
    {
        var allowed = new List<double>();

        void AddValue(double? value)
        {
            if (value is { } v)
            {
                allowed.Add(v);
                allowed.Add(Math.Round(v));
            }
        }

        void AddDigits(string? text)
        {
            if (text is null)
            {
                return;
            }

            foreach (Match match in Regex.Matches(text, @"\d+(?:\.\d+)?"))
            {
                allowed.Add(double.Parse(match.Value, CultureInfo.InvariantCulture));
            }
        }

        AddDigits(f.Runway);
        AddDigits(f.ActiveRunway);
        AddDigits(f.Sid);
        AddDigits(f.Star);
        AddDigits(f.Approach);
        AddDigits(f.Nav.AiracCycle);
        AddDigits(f.Nav.IlsIdent);
        AddValue(f.Nav.RunwayTrueHeading);
        AddValue(f.Nav.RunwayLengthFt);
        AddValue(f.Nav.RunwayElevationFt);
        AddValue(f.Nav.IlsFrequencyMhz);
        AddValue(f.Nav.GlideSlopeAngle);
        AddValue(f.Nav.TransitionAltitudeFt);
        AddValue(f.Nav.TransitionLevel);
        AddValue(f.WindDirDeg);
        AddValue(f.WindSpeedKt);
        AddValue(f.QnhHpa);
        AddValue(f.V1);
        AddValue(f.Vr);
        AddValue(f.V2);
        AddValue(f.FlexTempC);
        AddValue(f.VisibilityM);
        AddValue(f.TemperatureC);
        AddValue(f.Minima?.AltitudeFt);
        return allowed;
    }

    /// <summary>Builds the LLM system prompt (predecessor wording — facts-only, TTS-ready).</summary>
    public static string SystemPrompt(bool departure)
        => "You are the First Officer of an Airbus A320, giving a concise, professional spoken "
            + (departure ? "departure" : "arrival/approach")
            + " briefing to the Captain. Use ONLY the facts provided below — never invent or alter "
            + "runways, headings, frequencies, altitudes, speeds, levels, or weather. If a fact is "
            + "missing, simply omit it; do not guess or fill gaps. Keep it under about 120 words, "
            + "plain spoken English suitable for text-to-speech (no markdown, no lists, no headings). "
            + "Spell out aviation terms for the synthesizer: write 'flight level one one zero' (never "
            + "'FL' or 'FL110'); 'I L S', 'Q N H', 'R V R', 'V O R', 'D M E'; and units as words "
            + "('feet', 'knots', 'degrees'). Read flight levels, headings, frequencies, squawk codes, "
            + "wind direction and runway numbers digit by digit (e.g. 'heading one six three', 'one "
            + "one eight decimal one zero', 'runway one six right'); read altitudes, distances and "
            + "speeds normally. Speak single letters as their NATO phonetic words: 'information "
            + "Mike' (never 'information M'), 'VOLA three Victor' (never 'VOLA3V').";

    /// <summary>Builds the fact block ("- Label: value" lines, blanks omitted).</summary>
    public static string FactBlock(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var sb = new StringBuilder();
        sb.AppendLine("BRIEFING: " + (f.IsDeparture ? "Departure" : "Arrival/Approach"));

        void Line(string label, object? value)
        {
            var text = value?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {text}");
            }
        }

        // Identifiers/letters are handed to the LLM pre-formatted for speech (issue #68) —
        // asking the model to phoneticize "VOLA3V" itself proved unreliable.
        Line("Airport", f.AirportName is { } name ? $"{name} ({f.Airport})" : f.Airport);
        Line("Runway", f.Runway);
        Line("SID", Core.Speech.NatoPhonetics.SpeakIdentifier(f.Sid));
        Line("STAR", Core.Speech.NatoPhonetics.SpeakIdentifier(f.Star));
        Line("Approach", f.Approach is { } approach ? SpokenApproach(approach, f.Runway) : null);
        Line("AIRAC", f.Nav.AiracCycle);
        Line("Runway true heading (deg)", f.Nav.RunwayTrueHeading?.ToString("0", CultureInfo.InvariantCulture));
        Line("Runway length (ft)", f.Nav.RunwayLengthFt?.ToString("0", CultureInfo.InvariantCulture));
        Line("ILS identifier", f.Nav.IlsIdent);
        Line("ILS frequency (MHz)", f.Nav.IlsFrequencyMhz?.ToString("0.00", CultureInfo.InvariantCulture));
        Line("Glideslope (deg)", f.Nav.GlideSlopeAngle?.ToString("0.0", CultureInfo.InvariantCulture));
        Line("Transition altitude (ft)", f.Nav.TransitionAltitudeFt?.ToString("0", CultureInfo.InvariantCulture));
        Line("Transition level", f.Nav.TransitionLevel?.ToString("0", CultureInfo.InvariantCulture));
        Line("Runway elevation (ft)", f.Nav.RunwayElevationFt?.ToString("0", CultureInfo.InvariantCulture));
        if (f.IsDeparture)
        {
            Line("V1 (kt)", f.V1);
            Line("VR (kt)", f.Vr);
            Line("V2 (kt)", f.V2);
            Line("Flex temp (C)", f.FlexTempC);
        }

        if (f is { WindDirDeg: not null, WindSpeedKt: not null })
        {
            Line("Wind", $"{f.WindDirDeg:000} at {f.WindSpeedKt} kt");
        }

        Line("Visibility (m)", f.VisibilityM);
        Line("Temperature (C)", f.TemperatureC);
        Line("QNH (hPa)", f.QnhHpa);
        Line("ATIS information", Core.Speech.NatoPhonetics.Letter(f.AtisLetter)); // "M" → "Mike"
        Line("Active runway (per ATC)", f.ActiveRunway);
        if (!f.IsDeparture)
        {
            Line("Minimums", f.Minima is { } m ? MinimaCallout(m) : "NOT BRIEFED");
        }

        return sb.ToString();
    }
}
