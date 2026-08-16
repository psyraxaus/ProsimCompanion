using System.Globalization;
using System.Text;

namespace ProsimCompanion.Core.Debrief;

/// <summary>
/// The LLM-facing view of <see cref="DebriefFacts"/>: the system prompt (predecessor wording,
/// verbatim), the fact block ("- Label: value" lines, blanks omitted) and the allowed-number
/// set the number verifier holds the model's output to. Pure and Core-resident so the Speech
/// project's debrief service composes without Core knowing anything about LLMs — the same
/// split as <see cref="DebriefTemplate"/>.
/// </summary>
public static class DebriefLlm
{
    /// <summary>The debrief system prompt (Prosim2FO wording, verbatim — only the target
    /// length varies with verbosity).</summary>
    public static string SystemPrompt(DebriefVerbosity verbosity)
    {
        var length = verbosity == DebriefVerbosity.Brief ? "about 30 words" : "about 80 words";
        return
            "You are the First Officer of an Airbus A320 giving a short, friendly spoken post-flight debrief to the Captain. " +
            "Use ONLY the facts provided — never invent or alter any number (times, speeds, fuel, counts). If a fact is missing, omit it. " +
            "If an abnormal or failure was handled, lead with it briefly and say whether it was resolved. " +
            $"Keep it to {length}, plain spoken English for text-to-speech: no markdown, no lists, no headings. Warm and professional; " +
            "include one or two brief constructive observations only if the facts support them. End on a positive note.";
    }

    /// <summary>Builds the fact block: one "- Label: value" line per non-empty fact. The
    /// optional <paramref name="airportName"/> resolver (ICAO → spoken name, null = unknown)
    /// presents stations as "London Heathrow (EGLL)" so the LLM speaks the name instead of a
    /// garbled ICAO word (issue #70) — a Func rather than the interface keeps this class pure
    /// and Core-resident with no service dependency.</summary>
    public static string FactBlock(DebriefFacts f, Func<string, string?>? airportName = null)
    {
        ArgumentNullException.ThrowIfNull(f);

        var sb = new StringBuilder();
        sb.AppendLine("POST-FLIGHT FACTS:");

        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {value}");
            }
        }

        Line("Origin", Station(f.Origin, f.DepartureRunway, airportName));
        Line("Destination", Station(f.Destination, f.ArrivalRunway, airportName));
        Line("Block time (minutes)", f.BlockMinutes?.ToString(CultureInfo.InvariantCulture));
        Line("Airborne time (minutes)", f.FlightMinutes?.ToString(CultureInfo.InvariantCulture));
        Line("Lift-off speed (knots)", f.LiftoffIasKt?.ToString("0", CultureInfo.InvariantCulture));
        Line("Touchdown ground speed (knots)", f.TouchdownGroundSpeedKt?.ToString("0", CultureInfo.InvariantCulture));

        foreach (var gate in f.Gates)
        {
            Line($"Approach gate {gate.Name} ft",
                gate.Result + (gate.FailingCriterion is { } criterion ? $" ({criterion})" : ""));
        }

        foreach (var abnormal in f.Abnormals)
        {
            Line("Abnormal handled", abnormal.Title + (abnormal.Cleared ? ", cleared" : ", not cleared"));
        }

        Line("Checklists completed", Count(f.ChecklistsCompleted));
        if (f.ChecklistNames.Count > 0)
        {
            Line("Checklists", string.Join(", ", f.ChecklistNames));
        }

        Line("Callouts made", Count(f.CalloutsFired));
        if (f.Advisories.Count > 0)
        {
            Line("Advisories raised", string.Join(", ", f.Advisories));
        }

        Line("Degradations", Count(f.Degradations));
        Line("Cabin reports", Count(f.CabinReports));
        if (f.DefectsRaised > 0 || f.DefectsRectified > 0)
        {
            var techLog = new List<string>();
            if (f.DefectsRaised > 0)
            {
                techLog.Add($"{f.DefectsRaised} raised");
            }

            if (f.DefectsRectified > 0)
            {
                techLog.Add($"{f.DefectsRectified} rectified");
            }

            Line("Tech log this flight (defects)", string.Join(", ", techLog));
        }

        Line("Radio tunes handled", Count(f.RadioTunes));
        Line("Memory-item drills called", Count(f.MemoryDrills));
        Line("Fuel on board (tonnes)", Tonnes(f.FinalFobKg));
        Line("Fuel used (tonnes)", Tonnes(f.FuelUsedKg));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Every number the styled debrief is allowed to speak: block/airborne minutes, the
    /// rounded speeds (the prompt presents them rounded, so the raw value is not offered),
    /// gate AGLs, the activity counts, and fuel in tonnes to one decimal — exactly the
    /// precision the fact block shows. Zero counts are harmless members: verification only
    /// checks 3+ digit or decimal tokens.
    /// </summary>
    public static IReadOnlyList<double> AllowedNumbers(DebriefFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var allowed = new List<double>();

        void Add(double? value)
        {
            if (value is { } v)
            {
                allowed.Add(v);
            }
        }

        Add(f.BlockMinutes);
        Add(f.FlightMinutes);
        if (f.LiftoffIasKt is { } liftoff)
        {
            allowed.Add(Math.Round(liftoff));
        }

        if (f.TouchdownGroundSpeedKt is { } touchdown)
        {
            allowed.Add(Math.Round(touchdown));
        }

        foreach (var gate in f.Gates)
        {
            allowed.Add(gate.AglFt);
        }

        allowed.Add(f.CalloutsFired);
        allowed.Add(f.SpeechSuppressed);
        allowed.Add(f.ChecklistsCompleted);
        allowed.Add(f.Advisories.Count);
        allowed.Add(f.Degradations);
        allowed.Add(f.CabinReports);
        allowed.Add(f.DefectsRaised);
        allowed.Add(f.DefectsRectified);
        allowed.Add(f.DefectsCarried);
        allowed.Add(f.RadioTunes);
        allowed.Add(f.MemoryDrills);

        if (f.FinalFobKg is { } fob)
        {
            allowed.Add(Math.Round(fob / 1000.0, 1));
        }

        if (f.StartFobKg is { } start)
        {
            allowed.Add(Math.Round(start / 1000.0, 1));
        }

        if (f.FuelUsedKg is { } used)
        {
            allowed.Add(Math.Round(used / 1000.0, 1));
        }

        return allowed;
    }

    private static string? Station(string? icao, string? runway, Func<string, string?>? airportName)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var name = airportName?.Invoke(icao);
        var station = name is null ? icao : $"{name} ({icao})";
        return station + (string.IsNullOrWhiteSpace(runway) ? "" : $" runway {runway}");
    }

    private static string? Count(int value)
        => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : null;

    private static string? Tonnes(double? kg)
        => kg is { } v ? (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) : null;
}
