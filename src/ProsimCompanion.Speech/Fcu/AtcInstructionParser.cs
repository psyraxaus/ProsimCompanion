using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Fcu;

/// <summary>
/// Classifies an ATC-style utterance into an FCU instruction (Prosim2FO's keyword rules in
/// their proven order), with hard range checks — out-of-range values are NEVER clamped and
/// never written; the FO asks instead. Two predecessor gaps deliberately closed here:
/// "open descent"/"managed descent" now actually parse (they were dead grammar phrases), and
/// Arabic numerals work via the NumberExtractor backstop (whisper emits "flight level 120").
/// </summary>
public static class AtcInstructionParser
{
    private const int HeadingMin = 0;
    private const int HeadingMax = 360;
    private const int AltMinFt = 0;
    private const int AltMaxFt = 41_000;
    private const int FlMin = 0;
    private const int FlMax = 410;
    private const int SpeedMinKt = 100;
    private const int SpeedMaxKt = 399;
    private const int VsMinFpm = 100;
    private const int VsMaxFpm = 6000;

    public static FcuInstruction Parse(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        var t = " " + CommandMatcher.Normalize(utterance) + " ";

        // 1. QNH/altimeter — never an FCU action.
        if (Has(t, "qnh", "q n h", "altimeter"))
        {
            return new FcuInstruction(FcuInstructionType.Unknown, RawText: utterance);
        }

        // 2. Conditional — spoken relay only. A condition word ALONE is not an instruction:
        // "after start checklist" / "after takeoff checklist" were swallowed here and the FO
        // answered "Copied — conditional" instead of running the checklist (issue #47). Only
        // classify when actionable FCU content accompanies the condition.
        if (Has(t, "after ", "when ", "once ", "abeam", "passing", "reaching", "at time")
            && HasFcuContent(t))
        {
            return new FcuInstruction(FcuInstructionType.Conditional, RawText: utterance);
        }

        // 3. Engagements.
        if (Has(t, "autopilot", "auto pilot", "a p one", "a p two"))
        {
            return new FcuInstruction(FcuInstructionType.Autopilot, RawText: utterance);
        }

        if (Has(t, "autothrust", "auto thrust", "autothrottle", "a thr", "a t h r"))
        {
            return new FcuInstruction(FcuInstructionType.AutoThrust, RawText: utterance);
        }

        if (Has(t, "arm approach", "approach mode", "cleared approach", "clear approach", "cleared for the approach"))
        {
            return new FcuInstruction(FcuInstructionType.Approach, RawText: utterance);
        }

        if (Has(t, "arm localizer", "arm localiser", "localizer arm", "loc mode", "arm loc"))
        {
            return new FcuInstruction(FcuInstructionType.Localizer, RawText: utterance);
        }

        if (Has(t, "expedite"))
        {
            return new FcuInstruction(FcuInstructionType.Expedite, RawText: utterance);
        }

        // 4. Managed/selected (incl. the predecessor's dead "open descent" phrases, fixed).
        if (Has(t, "resume own navigation", "resume navigation", "own navigation", "cleared direct", "direct to"))
        {
            return new FcuInstruction(FcuInstructionType.Managed, FcuField.Heading, RawText: utterance);
        }

        if (Has(t, "managed speed"))
        {
            return new FcuInstruction(FcuInstructionType.Managed, FcuField.Speed, RawText: utterance);
        }

        if (Has(t, "managed heading"))
        {
            return new FcuInstruction(FcuInstructionType.Managed, FcuField.Heading, RawText: utterance);
        }

        if (Has(t, "selected speed", "select speed"))
        {
            return new FcuInstruction(FcuInstructionType.Selected, FcuField.Speed, RawText: utterance);
        }

        if (Has(t, "managed descent", "managed climb"))
        {
            return new FcuInstruction(FcuInstructionType.Managed, FcuField.Altitude, RawText: utterance);
        }

        if (Has(t, "open descent", "open climb"))
        {
            return new FcuInstruction(FcuInstructionType.Selected, FcuField.Altitude, RawText: utterance);
        }

        // 5. Mach — toggle only (value setting deferred, predecessor parity).
        if (Has(t, "mach"))
        {
            return new FcuInstruction(FcuInstructionType.SpeedMachToggle, RawText: utterance);
        }

        // 6–10. Value fields, evaluated in predecessor order.
        var negative = Has(t, "descend", "descent", "down");
        if (Has(t, "vertical speed", "per minute", "rate of descent", "rate of climb", "descend at", "climb at"))
        {
            return Value(utterance, FcuField.VerticalSpeed, useMagnitude: true, negative);
        }

        if (Has(t, "flight level") || HasToken(t, "fl") || HasToken(t, "level"))
        {
            return Value(utterance, FcuField.Altitude, useMagnitude: false, negative: false, isFlightLevel: true);
        }

        if (Has(t, "altitude")
            || (Has(t, "climb", "descend", "maintain", "descent") && Has(t, "thousand", "feet", "foot")))
        {
            return Value(utterance, FcuField.Altitude, useMagnitude: true, negative: false);
        }

        if (Has(t, "speed", "knots", "knot", "reduce", "increase"))
        {
            return Value(utterance, FcuField.Speed, useMagnitude: true, negative: false);
        }

        if (Has(t, "heading", "turn left", "turn right", "fly heading"))
        {
            return Value(utterance, FcuField.Heading, useMagnitude: false, negative: false);
        }

        // Fallback: a number with no value field. Only a clarifying query when the utterance
        // actually carries FCU content ("descend one two zero") — a bare number in a non-FCU
        // phrase must fall through as Unknown. 2026-08-16 (issue #66): this fallback used to
        // fire unconditionally, so "flaps two" and "starting engine one" were consumed here
        // and every one of them was answered "Say again — which field?" all flight.
        if (HasFcuContent(t) && NumberExtractor.TryExtract(utterance, out _))
        {
            return new FcuInstruction(FcuInstructionType.Query, Reason: "which field?", RawText: utterance);
        }

        return new FcuInstruction(FcuInstructionType.Unknown, RawText: utterance);
    }

    /// <summary>True when the utterance carries actionable FCU content — the same keyword
    /// surface the classifier matches below. A condition word without any of these is a non-FCU
    /// request (a checklist name, small talk), never an ATC instruction (issue #47).</summary>
    private static bool HasFcuContent(string t)
        => Has(t,
            "autopilot", "auto pilot", "autothrust", "auto thrust", "autothrottle",
            "approach", "localizer", "localiser", "loc mode", "expedite",
            "navigation", "cleared direct", "direct to",
            "managed", "selected", "open descent", "open climb", "mach",
            "flight level", "altitude", "climb", "descend", "descent",
            "vertical speed", "per minute", "speed", "knots", "knot",
            "reduce", "increase", "maintain", "heading", "turn left", "turn right",
            "thousand", "feet", "foot")
            || HasToken(t, "fl") || HasToken(t, "level");

    private static FcuInstruction Value(
        string utterance, FcuField field, bool useMagnitude, bool negative, bool isFlightLevel = false)
    {
        var run = SpokenNumber.ExtractNumberRun(utterance);
        int number;
        var parsed = useMagnitude ? SpokenNumber.TryParse(run, out number) : SpokenNumber.TryDigits(run, out number);
        if (!parsed)
        {
            // Arabic-numeral backstop — the predecessor's unclosed gap.
            if (NumberExtractor.TryExtract(utterance, out var numeric)
                && numeric == Math.Floor(numeric))
            {
                number = (int)numeric;
            }
            else
            {
                var name = isFlightLevel ? "flight level" : FieldName(field);
                return new FcuInstruction(FcuInstructionType.Query,
                    Reason: $"couldn't read the {name}", RawText: utterance);
            }
        }

        if (isFlightLevel)
        {
            if (number is < FlMin or > FlMax)
            {
                return new FcuInstruction(FcuInstructionType.Query,
                    Reason: $"flight level {number} out of range", RawText: utterance);
            }

            return new FcuInstruction(FcuInstructionType.SetValue, FcuField.Altitude, number * 100,
                Readback: $"flight level {Callouts.Aviation.ToDigits(number.ToString(System.Globalization.CultureInfo.InvariantCulture))}", RawText: utterance);
        }

        switch (field)
        {
            case FcuField.Heading when number is < HeadingMin or > HeadingMax:
                return Query($"heading {number} out of range");
            case FcuField.Altitude when number is < AltMinFt or > AltMaxFt:
                return Query($"altitude {number} out of range");
            case FcuField.Speed when number is < SpeedMinKt or > SpeedMaxKt:
                return Query($"speed {number} out of range");
            case FcuField.VerticalSpeed when number is < VsMinFpm or > VsMaxFpm:
                return Query($"vertical speed {number} out of range");
        }

        var value = field == FcuField.VerticalSpeed && negative ? -number : number;
        var readback = field switch
        {
            FcuField.Heading => $"heading {Callouts.Aviation.ToDigits(number.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))}",
            FcuField.Altitude => $"altitude {number} feet",
            FcuField.Speed => $"speed {number} knots",
            _ => $"vertical speed {(negative ? "minus " : "")}{number} feet per minute",
        };
        return new FcuInstruction(FcuInstructionType.SetValue, field, value, readback, RawText: utterance);

        FcuInstruction Query(string reason)
            => new(FcuInstructionType.Query, Reason: reason, RawText: utterance);
    }

    private static string FieldName(FcuField field) => field switch
    {
        FcuField.Heading => "heading",
        FcuField.Altitude => "altitude",
        FcuField.Speed => "speed",
        _ => "vertical speed",
    };

    private static bool Has(string paddedText, params string[] needles)
        => needles.Any(n => paddedText.Contains(n, StringComparison.Ordinal));

    private static bool HasToken(string paddedText, string token)
        => paddedText.Contains($" {token} ", StringComparison.Ordinal);
}
