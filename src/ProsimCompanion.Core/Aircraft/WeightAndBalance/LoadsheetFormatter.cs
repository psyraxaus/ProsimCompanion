using System.Globalization;
using System.Text;

namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>
/// Formats loadsheet data into airline-style ACARS text, mirroring the predecessor's layout
/// (itself modelled on ProSim's stock loadsheets so pilots see a familiar shape). Spacing is
/// deliberate and pilots read these verbatim — treat the templates as fixed-format documents,
/// not display strings to tidy.
/// </summary>
public static class LoadsheetFormatter
{
    private const double WeightLimitThresholdKg = 1000.0;

    public static string FormatPreliminary(LoadsheetContext ctx, LoadsheetData data)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(data);

        var (zfwLim, towLim, lawLim) = ComputeWeightLimitations(ctx, data);

        // Whole-kg rounding — what pilots type into MCDU INIT-B.
        var zfwWhole = (int)Math.Round(data.ZeroFuelWeight);
        var towWhole = (int)Math.Round(data.TakeoffWeight);
        var lawWhole = (int)Math.Round(data.LandingWeight);
        var maxZfwWhole = (int)Math.Round(ctx.MaxZfwKg);
        var maxTowWhole = (int)Math.Round(ctx.MaxTowKg);
        var maxLawWhole = (int)Math.Round(ctx.MaxLawKg);
        var tofWhole = (int)Math.Round(data.TakeoffWeight - data.ZeroFuelWeight);
        var tifWhole = (int)Math.Round(data.TakeoffWeight - data.LandingWeight);
        var undldWhole = (int)Math.Round(ctx.MaxLawKg - data.LandingWeight);
        var fuelInTanksWhole = (int)Math.Round(data.FuelWeight);
        var waterWasteWhole = (int)Math.Round(ctx.WaterWasteKg);

        // Sections: A/B are zones 1/2; C aggregates zones 3+4 (three-section loadsheet shape).
        var paxA = data.PassengersByZone.ElementAtOrDefault(0);
        var paxB = data.PassengersByZone.ElementAtOrDefault(1);
        var paxC = data.PassengersByZone.ElementAtOrDefault(2) + data.PassengersByZone.ElementAtOrDefault(3);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- LOADSHEET PRELIM {ctx.Ident} EDNO {ctx.EditionNumber}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.Callsign}/{ctx.ScheduledDepartureTime.ToUpperInvariant()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.DepartureIata} {ctx.ArrivalIata} {ctx.AircraftTailNumber} 2/4");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ZFW  {zfwWhole}  MAX  {maxZfwWhole}  {zfwLim}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"TOF  {tofWhole}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"TOW  {towWhole}  MAX  {maxTowWhole}  {towLim}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"TIF  {tifWhole}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"LAW  {lawWhole}  MAX  {maxLawWhole}  {lawLim}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"UNDLD  {undldWhole}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PAX/{ctx.Infants}/{data.TotalPassengers} TTL {ctx.Infants + data.TotalPassengers}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"MACZFW  {data.ZeroFuelWeightMac.ToString("F1", CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"MACTOW  {data.TakeoffWeightMac.ToString("F1", CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"A{paxA}  B{paxB}  C{paxC}");
        sb.AppendLine("CABIN SECTION TRIM");
        sb.AppendLine("SI SERVICE WEIGHT ADJUSTMENT WEIGHT/INDEX");
        sb.AppendLine("ADD");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.ArrivalIata} POTABLE WATER 10/10 100PCT");
        sb.AppendLine(CultureInfo.InvariantCulture, $" {waterWasteWhole} 0.5-");
        sb.AppendLine(" DEDUCTIONS");
        sb.AppendLine("NIL");
        sb.AppendLine(" PANTRY EFFECT 1198 / 3.5");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PREPARED BY {ctx.DispatcherFirstName.ToUpperInvariant()}/{ctx.DispatcherLastName.ToUpperInvariant()} {ctx.DispatcherPhone}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"LICENCE {ctx.DispatcherLicense}");
        sb.Append(CultureInfo.InvariantCulture, $"FUEL IN TANKS {fuelInTanksWhole}");
        return sb.ToString();
    }

    public static string FormatFinal(LoadsheetContext ctx, LoadsheetData prelim, LoadsheetData final)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(prelim);
        ArgumentNullException.ThrowIfNull(final);

        var diffs = ComputeDifferences(ctx, prelim, final);
        var title = diffs.AnyChanged
            ? $"REVISIONS TO EDNO {ctx.EditionNumber}"
            : $"COMPLIANCE WITH EDNO {ctx.EditionNumber}";

        var paxDiff = final.TotalPassengers - prelim.TotalPassengers;
        var paxDiffString = paxDiff switch
        {
            > 0 => $"{final.TotalPassengers} plus {paxDiff}",
            < 0 => $"{final.TotalPassengers} minus {-paxDiff}",
            _ => $"{final.TotalPassengers} no change",
        };

        var finalZfwWhole = (int)Math.Round(final.ZeroFuelWeight);
        var finalTowWhole = (int)Math.Round(final.TakeoffWeight);
        var finalFuelWhole = (int)Math.Round(final.FuelWeight);

        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.Callsign}/{ctx.ScheduledDepartureTime.ToUpperInvariant()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.DepartureIata}  {ctx.ArrivalIata}  {ctx.AircraftTailNumber}  2/4");
        sb.AppendLine("........................");
        sb.AppendLine(CultureInfo.InvariantCulture, $"ZFW  {finalZfwWhole}  {diffs.ZfwFlag}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"TOW  {finalTowWhole}  {diffs.TowFlag}");
        sb.AppendLine($"PAX  {paxDiffString} {ctx.InfantsOutput}".TrimEnd());
        sb.AppendLine(CultureInfo.InvariantCulture, $"MACZFW  {final.ZeroFuelWeightMac.ToString("F1", CultureInfo.InvariantCulture)}  {diffs.MacZfwFlag}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"MACTOW  {final.TakeoffWeightMac.ToString("F1", CultureInfo.InvariantCulture)}  {diffs.MacTowFlag}");
        // LIZFW/LITOW reuse the MAC flags — the index is a linear function of the MAC, so if
        // the MAC changed the index changed with it.
        sb.AppendLine(CultureInfo.InvariantCulture, $"LIZFW   {final.LizfwIndex.ToString("F1", CultureInfo.InvariantCulture)}  {diffs.MacZfwFlag}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"LITOW   {final.LitowIndex.ToString("F1", CultureInfo.InvariantCulture)}  {diffs.MacTowFlag}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"FUEL IN TANKS  {finalFuelWhole}  {diffs.FuelFlag}");
        sb.AppendLine("........................");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{ctx.Time:HH:mm}Z");
        sb.Append("END");
        return sb.ToString();
    }

    /// <summary>PRELIM "L" markers next to weights at or within 1000 kg of their max — bare
    /// "L" here; the JSON envelope's variants carry a leading space (" L").</summary>
    internal static (string ZfwLim, string TowLim, string LawLim) ComputeWeightLimitations(
        LoadsheetContext ctx, LoadsheetData data)
    {
        var zfw = data.ZeroFuelWeight > ctx.MaxZfwKg || ctx.MaxZfwKg - data.ZeroFuelWeight <= WeightLimitThresholdKg;
        var tow = data.TakeoffWeight > ctx.MaxTowKg || ctx.MaxTowKg - data.TakeoffWeight <= WeightLimitThresholdKg;
        var law = data.LandingWeight > ctx.MaxLawKg || ctx.MaxLawKg - data.LandingWeight <= WeightLimitThresholdKg;
        return (zfw ? "L" : "", tow ? "L" : "", law ? "L" : "");
    }

    /// <summary>FINAL "//" change flags vs the cached prelim. Strictly greater-than: a delta
    /// equal to the tolerance is not a change. Fuel participates in the title decision but not
    /// in the JSON CHK flags (deliberate predecessor asymmetry).</summary>
    internal static FinalDifferences ComputeDifferences(
        LoadsheetContext ctx, LoadsheetData prelim, LoadsheetData final)
    {
        var zfwChanged = Math.Abs(prelim.ZeroFuelWeight - final.ZeroFuelWeight) > ctx.WeightChangeToleranceKg;
        var towChanged = Math.Abs(prelim.TakeoffWeight - final.TakeoffWeight) > ctx.WeightChangeToleranceKg;
        var macZfwChanged = Math.Abs(prelim.ZeroFuelWeightMac - final.ZeroFuelWeightMac) > ctx.MacChangeTolerancePercent;
        var macTowChanged = Math.Abs(prelim.TakeoffWeightMac - final.TakeoffWeightMac) > ctx.MacChangeTolerancePercent;
        var fuelChanged = Math.Abs(prelim.FuelWeight - final.FuelWeight) > ctx.WeightChangeToleranceKg;

        return new FinalDifferences(
            zfwChanged ? "//" : "",
            towChanged ? "//" : "",
            macZfwChanged ? "//" : "",
            macTowChanged ? "//" : "",
            fuelChanged ? "//" : "",
            zfwChanged || towChanged || macZfwChanged || macTowChanged || fuelChanged);
    }

    internal sealed record FinalDifferences(
        string ZfwFlag, string TowFlag, string MacZfwFlag, string MacTowFlag, string FuelFlag, bool AnyChanged);
}
