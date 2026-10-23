using System.Globalization;

namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>What the last-minute-change evaluation found (issue #181).</summary>
public enum LmcVerdict
{
    /// <summary>The boarded load matches the final loadsheet — nothing to say.</summary>
    NoChange,

    /// <summary>The load differs, but within the LMC tolerances: the crew applies the change
    /// by hand and flies on the final with an LMC block.</summary>
    WithinTolerance,

    /// <summary>The load differs beyond a tolerance: a revised final (EDNO +1) is required —
    /// or the captain flies on their own authority.</summary>
    RevisedLoadsheetRequired,
}

/// <summary>The LMC tolerances an operator allows before a revised loadsheet is required
/// (<c>flightData.lmcMaxPax</c> / <c>lmcMaxWeightKg</c> / <c>lmcMaxMacShift</c>).</summary>
public sealed record LmcTolerances(int MaxPax, double MaxWeightKg, double MaxMacShift)
{
    public static LmcTolerances Default { get; } = new(4, 300, 0.5);
}

/// <summary>One last-minute-change evaluation: the final loadsheet against the load actually
/// aboard, with the new figures the crew applies by hand. Weights in kg, MAC in percent;
/// the deltas are actual minus final (a no-show is a negative passenger delta).</summary>
public sealed record LmcResult(
    LmcVerdict Verdict,
    int FinalPax,
    int ActualPax,
    int DeltaPax,
    double DeltaZfwKg,
    double DeltaCargoKg,
    double DeltaFuelKg,
    double DeltaTowKg,
    double NewZfwKg,
    double NewTowKg,
    double NewLawKg,
    double NewMacZfw,
    double NewMacTow,
    double MacZfwShift,
    double MacTowShift,
    IReadOnlyList<string> Reasons);

/// <summary>
/// The last-minute-change rule (issue #181): the final loadsheet's figures against the load
/// actually aboard, as the bit-exact W&amp;B computes it from the live aircraft state (the
/// ProSim pax zones, holds and tanks already reflect what boarded, so the "actual" side is
/// <see cref="A320WeightAndBalance.CalculateFinal"/> over the live state at the moment of the
/// check — no second W&amp;B model). Pure, so the verdict tables are the tests. Children and
/// adults are not distinguished: the ProSim seat map carries pax counts only.
/// </summary>
public static class LmcEvaluator
{
    /// <summary>Weight differences below this are dataref jitter, not a change.</summary>
    public const double NoChangeWeightKg = 10;

    /// <summary>Fuel differences below this are gauge noise, not a change.</summary>
    public const double NoChangeFuelKg = 50;

    /// <summary>MAC differences below this are rounding, not a change.</summary>
    public const double NoChangeMacShift = 0.05;

    public static LmcResult Evaluate(LoadsheetData final, LoadsheetData actual, LmcTolerances tolerances)
    {
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerances);

        var deltaPax = actual.TotalPassengers - final.TotalPassengers;
        var deltaZfw = actual.ZeroFuelWeight - final.ZeroFuelWeight;
        var deltaCargo = (actual.ForwardCargoWeight + actual.AftCargoWeight) - (final.ForwardCargoWeight + final.AftCargoWeight);
        var deltaFuel = actual.FuelWeight - final.FuelWeight;
        var deltaTow = actual.TakeoffWeight - final.TakeoffWeight;
        var macZfwShift = Math.Abs(actual.ZeroFuelWeightMac - final.ZeroFuelWeightMac);
        var macTowShift = Math.Abs(actual.TakeoffWeightMac - final.TakeoffWeightMac);

        // LAW = new TOW − the final's trip fuel (a dispatch-time quantity, same rule as the
        // final itself).
        var tripFuel = final.TakeoffWeight - final.LandingWeight;
        var newLaw = tripFuel > 0 ? actual.TakeoffWeight - tripFuel : actual.TakeoffWeight;

        var reasons = new List<string>();
        if (Math.Abs(deltaPax) > tolerances.MaxPax)
        {
            reasons.Add($"passengers differ by {Math.Abs(deltaPax)} (limit {tolerances.MaxPax})");
        }

        if (Math.Abs(deltaZfw) > tolerances.MaxWeightKg)
        {
            reasons.Add($"zero fuel weight differs by {Math.Abs(deltaZfw):0} kg (limit {tolerances.MaxWeightKg:0})");
        }

        if (Math.Abs(deltaTow) > tolerances.MaxWeightKg)
        {
            reasons.Add($"take-off weight differs by {Math.Abs(deltaTow):0} kg (limit {tolerances.MaxWeightKg:0})");
        }

        var macShift = Math.Max(macZfwShift, macTowShift);
        if (macShift > tolerances.MaxMacShift)
        {
            reasons.Add($"MAC shifted {macShift.ToString("0.0", CultureInfo.InvariantCulture)} % (limit {tolerances.MaxMacShift.ToString("0.0", CultureInfo.InvariantCulture)})");
        }

        var noChange = deltaPax == 0
            && Math.Abs(deltaZfw) < NoChangeWeightKg
            && Math.Abs(deltaCargo) < NoChangeWeightKg
            && Math.Abs(deltaFuel) < NoChangeFuelKg
            && macShift < NoChangeMacShift;

        var verdict = noChange ? LmcVerdict.NoChange
            : reasons.Count > 0 ? LmcVerdict.RevisedLoadsheetRequired
            : LmcVerdict.WithinTolerance;

        return new LmcResult(
            verdict, final.TotalPassengers, actual.TotalPassengers, deltaPax,
            deltaZfw, deltaCargo, deltaFuel, deltaTow,
            actual.ZeroFuelWeight, actual.TakeoffWeight, newLaw, actual.ZeroFuelWeightMac, actual.TakeoffWeightMac,
            macZfwShift, macTowShift, reasons);
    }

    /// <summary>The FO's spoken LMC for a within-tolerance change: the deltas, the new
    /// figures, and the reminder that the FMS is not re-synced on its own. Tonnes to 1 dp,
    /// invariant culture — a European locale must never emit "58,9". Every number in it is
    /// the allowed set when the persona restyles it.</summary>
    public static string SpokenWithinLimits(LmcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Last minute change: {SpokenDeltas(result)}, zero fuel weight {Tonnes(result.NewZfwKg)} tonnes, "
            + $"take-off weight {Tonnes(result.NewTowKg)} tonnes, MAC {Mac(result.NewMacTow)} percent. "
            + "Within limits, no new loadsheet. Update the FMS zero fuel weight when ready, captain.";
    }

    /// <summary>The captain's question for a change beyond tolerance: what differs and by how
    /// much, then "issue a revised final?".</summary>
    public static string QuestionPrompt(LmcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var differs = result.DeltaPax != 0
            ? $"by {Math.Abs(result.DeltaPax)} {(Math.Abs(result.DeltaPax) == 1 ? "passenger" : "passengers")}"
            : $"by {Math.Abs(result.DeltaTowKg):0} kilos";
        return $"Boarding differs from the final loadsheet {differs} — beyond the last minute change limit. Issue a revised final?";
    }

    /// <summary>"minus 2 passengers, cargo plus 120 kilos" — the delta words shared by the
    /// spoken line and the fact sheet.</summary>
    public static string SpokenDeltas(LmcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = new List<string>
        {
            result.DeltaPax switch
            {
                0 => "no passenger change",
                1 => "plus 1 passenger",
                -1 => "minus 1 passenger",
                > 0 => $"plus {result.DeltaPax} passengers",
                _ => $"minus {-result.DeltaPax} passengers",
            },
        };
        if (Math.Abs(result.DeltaCargoKg) >= NoChangeWeightKg)
        {
            parts.Add($"cargo {Signed(result.DeltaCargoKg)} kilos");
        }

        if (Math.Abs(result.DeltaFuelKg) >= NoChangeFuelKg)
        {
            parts.Add($"fuel {Signed(result.DeltaFuelKg)} kilos");
        }

        return string.Join(", ", parts);
    }

    private static string Signed(double kg) => (kg >= 0 ? "plus " : "minus ") + Math.Abs(kg).ToString("0", CultureInfo.InvariantCulture);

    private static string Tonnes(double kg) => (kg / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Mac(double percent) => percent.ToString("0.0", CultureInfo.InvariantCulture);
}
