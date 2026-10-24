using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Callouts;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>One ADIRU as read.</summary>
public sealed record AdiruState(int Number, bool Aligning, double MinutesLeft, bool AttitudeAvailable)
{
    public bool Aligned => !Aligning && AttitudeAvailable;
}

/// <summary>The FMGC figures as read from the datarefs (null / 0 = not entered).</summary>
public sealed record FmgcFacts(string? Origin, string? Destination, int CruiseAltitudeFt, int V1, int Vr, int V2, int FlexTempC);

/// <summary>The composed cross-check: the spoken text, the mismatches and every number spoken.</summary>
public sealed record CrossCheckReadout(string Text, IReadOnlyList<string> Mismatches, IReadOnlyList<double> Numbers, bool Complete);

/// <summary>
/// Pure composer of the ADIRS / FMGC cross-check (issue #188): ADIRS alignment, the INIT page
/// (origin, destination, cruise level, and the cost index "per the plan" — it has no dataref)
/// against the OFP, the PERF page V-speeds and flex against the app's computed takeoff
/// performance when one exists. Deterministic text, digits read ICAO-style; every number is
/// returned for the <c>NumberVerifier</c> guard.
/// </summary>
public static class CrossCheckComposer
{
    public static CrossCheckReadout Compose(IReadOnlyList<AdiruState> adirus, FmgcFacts fmgc, OfpData? ofp, TakeoffPerfSnapshot? perf)
    {
        ArgumentNullException.ThrowIfNull(adirus);
        ArgumentNullException.ThrowIfNull(fmgc);
        var parts = new List<string>();
        var mismatches = new List<string>();
        var numbers = new List<double>();

        // ---- ADIRS ----
        var aligned = adirus.Where(a => a.Aligned).Select(a => a.Number).ToList();
        if (aligned.Count == adirus.Count && adirus.Count > 0)
        {
            parts.Add("ADIRS one, two and three aligned.");
        }
        else
        {
            foreach (var a in adirus)
            {
                if (a.Aligned)
                {
                    parts.Add($"ADIRS {Word(a.Number)} aligned.");
                }
                else if (a.Aligning)
                {
                    var minutes = Math.Max(0, (int)Math.Ceiling(a.MinutesLeft));
                    numbers.Add(minutes);
                    parts.Add($"ADIRS {Word(a.Number)} aligning, {Aviation.ToDigits(minutes.ToString(CultureInfo.InvariantCulture))} minute{(minutes == 1 ? "" : "s")} to go.");
                    mismatches.Add($"ADIRS {a.Number} aligning");
                }
                else
                {
                    parts.Add($"ADIRS {Word(a.Number)} — no attitude.");
                    mismatches.Add($"ADIRS {a.Number} no attitude");
                }
            }
        }

        // ---- INIT ----
        var complete = true;
        if (string.IsNullOrWhiteSpace(fmgc.Origin) || string.IsNullOrWhiteSpace(fmgc.Destination))
        {
            parts.Add("INIT page: no route entered.");
            complete = false;
        }
        else
        {
            parts.Add($"INIT page: {NatoPhonetics.SpeakIdentifier(fmgc.Origin)} to {NatoPhonetics.SpeakIdentifier(fmgc.Destination)}.");
            if (ofp is not null && (!Same(fmgc.Origin, ofp.OriginIcao) || !Same(fmgc.Destination, ofp.DestinationIcao)))
            {
                parts.Add($"The plan says {NatoPhonetics.SpeakIdentifier(ofp.OriginIcao)} to {NatoPhonetics.SpeakIdentifier(ofp.DestinationIcao)}.");
                mismatches.Add($"route {fmgc.Origin}/{fmgc.Destination} vs plan {ofp.OriginIcao}/{ofp.DestinationIcao}");
            }
        }

        if (ofp is { CostIndex.Length: > 0 } && int.TryParse(ofp.CostIndex, NumberStyles.None, CultureInfo.InvariantCulture, out var ci))
        {
            numbers.Add(ci);
            parts.Add($"Cost index per the plan {Aviation.ToDigits(ofp.CostIndex)}.");
        }

        if (fmgc.CruiseAltitudeFt > 0)
        {
            var fl = fmgc.CruiseAltitudeFt / 100;
            numbers.Add(fl);
            parts.Add($"Cruise flight level {Aviation.ToDigits(fl.ToString(CultureInfo.InvariantCulture))}.");
            if (ofp is { CruiseFlightLevel: > 0 } && ofp.CruiseFlightLevel != fl)
            {
                numbers.Add(ofp.CruiseFlightLevel);
                parts.Add($"The plan has flight level {Aviation.ToDigits(ofp.CruiseFlightLevel.ToString(CultureInfo.InvariantCulture))}.");
                mismatches.Add($"cruise FL{fl} vs plan FL{ofp.CruiseFlightLevel}");
            }
        }
        else
        {
            parts.Add("No cruise level entered.");
            complete = false;
        }

        // ---- PERF ----
        if (fmgc.V1 > 0 && fmgc.Vr > 0 && fmgc.V2 > 0)
        {
            numbers.AddRange([fmgc.V1, fmgc.Vr, fmgc.V2]);
            var perfLine = $"PERF: V one {Digits(fmgc.V1)}, V R {Digits(fmgc.Vr)}, V two {Digits(fmgc.V2)}";
            if (fmgc.FlexTempC > 0)
            {
                numbers.Add(fmgc.FlexTempC);
                perfLine += $", flex {Digits(fmgc.FlexTempC)}";
            }
            else
            {
                perfLine += ", TOGA";
            }

            parts.Add(perfLine + ".");

            if (perf is not null)
            {
                Compare("V one", fmgc.V1, perf.V1);
                Compare("V R", fmgc.Vr, perf.Vr);
                Compare("V two", fmgc.V2, perf.V2);
                if (perf.FlexTempC is { } flex && fmgc.FlexTempC > 0 && flex != fmgc.FlexTempC)
                {
                    Compare("flex", fmgc.FlexTempC, flex);
                }
            }
        }
        else
        {
            parts.Add("PERF: V speeds not entered.");
            complete = false;
        }

        parts.Add(mismatches.Count == 0 ? "Cross-check complete." : "Cross-check complete — check the differences.");
        return new CrossCheckReadout(string.Join(" ", parts), mismatches, numbers, complete);

        void Compare(string label, int entered, int computed)
        {
            if (entered == computed)
            {
                return;
            }

            numbers.Add(computed);
            parts.Add($"{label} {Digits(entered)} — the computed figure is {Digits(computed)}.");
            mismatches.Add($"{label.Replace(" ", "", StringComparison.Ordinal)} {entered} vs computed {computed}");
        }
    }

    private static string Digits(int value) => Aviation.ToDigits(value.ToString(CultureInfo.InvariantCulture));

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Word(int n) => n switch { 1 => "one", 2 => "two", 3 => "three", _ => n.ToString(CultureInfo.InvariantCulture) };
}
