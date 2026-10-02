using System.Globalization;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>Everything one gross-error check reads. Nulls = not available.</summary>
/// <param name="LiveZfwKg">The aircraft's zero fuel weight as ProSim computes it from the
/// load actually aboard. The FMS INIT B figures are write-only in the SDK, so this stands
/// in for "FMS ZFW": it is what the FMS value must agree with.</param>
/// <param name="FuelOnBoardKg">Live total fuel — likewise the stand-in for the FMS block.</param>
/// <param name="Final">The final loadsheet slot; a check needs it Sent.</param>
/// <param name="Perf">The last takeoff performance result, if one was calculated.</param>
/// <param name="FmsFlapsConf">FMS PERF TO flaps (CONF 1–3); 0 = not entered.</param>
/// <param name="FmsFlexTempC">FMS PERF TO flex; 0 = not entered (TOGA).</param>
public sealed record GrossErrorInputs(
    double? LiveZfwKg,
    double? FuelOnBoardKg,
    LoadsheetSlotView? Final,
    TakeoffPerfSnapshot? Perf,
    int FmsFlapsConf,
    int FmsFlexTempC,
    int FmsV1,
    int FmsVr,
    int FmsV2);

/// <summary>The outcome: one line to speak, the mismatches behind it, and what could not be
/// compared (no performance result, no loadsheet…).</summary>
public sealed record GrossErrorReport(bool Checked, IReadOnlyList<string> Mismatches, IReadOnlyList<string> Skipped, string Text);

/// <summary>
/// The takeoff gross-error check (issue #148): the aircraft's weights against the final
/// loadsheet, and the FMS PERF TO entries against the last takeoff performance calculation.
/// "Checked" when everything that could be compared agrees within the tolerances; otherwise
/// one line naming each mismatch. Pure.
/// </summary>
public static class GrossErrorCheckCore
{
    public static GrossErrorReport Evaluate(GrossErrorInputs inputs, GrossErrorCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        var mismatches = new List<string>();
        var skipped = new List<string>();

        if (inputs.Final is { Status: LoadsheetSlotStatus.Sent } final)
        {
            if (inputs.LiveZfwKg is { } zfw && final.ZfwKg > 0)
            {
                var delta = zfw - final.ZfwKg;
                if (Math.Abs(delta) > options.ZfwToleranceKg)
                {
                    mismatches.Add($"zero fuel weight is {Kilos(Math.Abs(delta))} kilos {AboveBelow(delta)} the loadsheet");
                }
            }
            else
            {
                skipped.Add("zero fuel weight");
            }

            if (inputs.FuelOnBoardKg is { } fob && final.FuelKg > 0)
            {
                var delta = fob - final.FuelKg;
                if (Math.Abs(delta) > options.BlockFuelToleranceKg)
                {
                    mismatches.Add($"fuel on board is {Kilos(Math.Abs(delta))} kilos {AboveBelow(delta)} the loadsheet block fuel");
                }
            }
            else
            {
                skipped.Add("block fuel");
            }
        }
        else
        {
            skipped.Add("final loadsheet");
        }

        if (inputs.Perf is { } perf)
        {
            if (inputs.FmsFlapsConf > 0 && inputs.FmsFlapsConf != perf.FlapsConf)
            {
                mismatches.Add($"flaps: FMS config {inputs.FmsFlapsConf}, performance config {perf.FlapsConf}");
            }

            if (perf.FlexTempC is { } flex && inputs.FmsFlexTempC > 0
                && Math.Abs(inputs.FmsFlexTempC - flex) > options.FlexToleranceC)
            {
                mismatches.Add($"flex: FMS {inputs.FmsFlexTempC}, performance {flex}");
            }

            var speeds = new List<string>();
            Speed(speeds, "V one", inputs.FmsV1, perf.V1, options.VSpeedToleranceKt);
            Speed(speeds, "rotate", inputs.FmsVr, perf.Vr, options.VSpeedToleranceKt);
            Speed(speeds, "V two", inputs.FmsV2, perf.V2, options.VSpeedToleranceKt);
            if (speeds.Count > 0)
            {
                mismatches.Add(string.Join(", ", speeds));
            }
        }
        else
        {
            skipped.Add("takeoff performance");
        }

        var text = mismatches.Count == 0
            ? "Gross error check: checked." + (skipped.Count > 0 ? $" {Capitalize(Join(skipped))} not compared." : "")
            : $"Gross error check: {string.Join("; ", mismatches)}.";
        return new GrossErrorReport(mismatches.Count == 0, mismatches, skipped, text);
    }

    private static void Speed(List<string> into, string name, int fms, int perf, int tolerance)
    {
        if (fms > 0 && perf > 0 && Math.Abs(fms - perf) > tolerance)
        {
            into.Add($"{name}: FMS {fms.ToString(CultureInfo.InvariantCulture)}, performance {perf.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static string Kilos(double kg) => (Math.Round(kg / 100) * 100).ToString("0", CultureInfo.InvariantCulture);

    private static string AboveBelow(double delta) => delta >= 0 ? "above" : "below";

    private static string Join(List<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
