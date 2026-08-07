using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Everything a briefing may speak — null fields are simply omitted. ATIS letter and
/// active runway (SayIntentions-sourced, via the composite weather provider) feed the LLM
/// fact block only; the deterministic template deliberately ignores them so its clause
/// structure stays byte-stable.</summary>
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
    string? ActiveRunway = null);

/// <summary>
/// The deterministic briefing template (Prosim2FO's exact clause structure) plus the number
/// verifier that keeps optional LLM prose honest: every significant number in a narrative
/// (≥3 digits or decimal) must appear in the source facts within 0.06 — 1–2 digit tokens are
/// deliberately ignored (runways/flaps/ordinals false-positive).
/// </summary>
public static class BriefingComposer
{
    public static string Template(BriefingFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var s = new List<string>();
        if (f.IsDeparture)
        {
            s.Add("Departure briefing.");
            Add(s, f.Airport, a => $"Departing {a}.");
            Add(s, f.Runway, r => $"Runway {r}.");
            Add(s, f.Sid, x => $"Standard instrument departure {x}.");
            Add(s, f.Nav.RunwayTrueHeading, h => $"Initial track {h:0} degrees.");
            Add(s, f.Nav.TransitionAltitudeFt, t => $"Transition altitude {t:0} feet.");
            if (f is { V1: not null, Vr: not null, V2: not null })
            {
                s.Add($"V1 {f.V1}, rotate {f.Vr}, V2 {f.V2}.");
            }
        }
        else
        {
            s.Add("Arrival briefing.");
            Add(s, f.Airport, a => $"Arriving {a}.");
            Add(s, f.Runway, r => $"Runway {r}.");
            Add(s, f.Approach, a => $"Approach {a}.");
            if (f.Nav is { IlsIdent: not null, IlsFrequencyMhz: not null })
            {
                s.Add($"ILS {f.Nav.IlsIdent}, frequency {f.Nav.IlsFrequencyMhz:0.00}.");
            }

            Add(s, f.Nav.GlideSlopeAngle, g => $"Glideslope {g:0.0} degrees.");
            Add(s, f.Star, x => $"Arrival via {x}.");
            Add(s, f.Nav.TransitionLevel, t => $"Transition level {t:0}.");
        }

        if (f is { WindDirDeg: not null, WindSpeedKt: not null })
        {
            s.Add($"Wind {f.WindDirDeg:000} at {f.WindSpeedKt} knots.");
        }

        Add(s, f.QnhHpa, q => $"QNH {q:0}.");

        if (!f.IsDeparture)
        {
            s.Add(f.Minima is { } m
                ? $"Minimums, {MinimaCallout(m)}."
                : "Minimums not briefed.");
        }

        return string.Join(" ", s);
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
            + "speeds normally.";

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

        Line("Airport", f.Airport);
        Line("Runway", f.Runway);
        Line("SID", f.Sid);
        Line("STAR", f.Star);
        Line("Approach", f.Approach);
        Line("AIRAC", f.Nav.AiracCycle);
        Line("Runway true heading (deg)", f.Nav.RunwayTrueHeading?.ToString("0", CultureInfo.InvariantCulture));
        Line("Runway length (ft)", f.Nav.RunwayLengthFt?.ToString("0", CultureInfo.InvariantCulture));
        Line("ILS identifier", f.Nav.IlsIdent);
        Line("ILS frequency (MHz)", f.Nav.IlsFrequencyMhz?.ToString("0.00", CultureInfo.InvariantCulture));
        Line("Glideslope (deg)", f.Nav.GlideSlopeAngle?.ToString("0.0", CultureInfo.InvariantCulture));
        Line("Transition altitude (ft)", f.Nav.TransitionAltitudeFt?.ToString("0", CultureInfo.InvariantCulture));
        Line("Transition level", f.Nav.TransitionLevel?.ToString("0", CultureInfo.InvariantCulture));
        if (f.IsDeparture)
        {
            Line("V1 (kt)", f.V1);
            Line("VR (kt)", f.Vr);
            Line("V2 (kt)", f.V2);
        }

        if (f is { WindDirDeg: not null, WindSpeedKt: not null })
        {
            Line("Wind", $"{f.WindDirDeg:000} at {f.WindSpeedKt} kt");
        }

        Line("QNH (hPa)", f.QnhHpa);
        Line("ATIS information", f.AtisLetter);
        Line("Active runway (per ATC)", f.ActiveRunway);
        if (!f.IsDeparture)
        {
            Line("Minimums", f.Minima is { } m ? MinimaCallout(m) : "NOT BRIEFED");
        }

        return sb.ToString();
    }

    private static void Add<T>(List<string> sentences, T? value, Func<T, string> format)
    {
        if (value is not null)
        {
            sentences.Add(format(value));
        }
    }
}
