namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The captain's fuel decision and the holding-time advice (issue #183; the <c>fuel</c>
/// section, Settings → Display &amp; Flight Data → Fuel decision). Advisory only: the
/// recommendation becomes fuel only through the captain's yes (the INIT block-fuel override).
/// </summary>
public sealed class FuelOptions : IOptionSection
{
    public static string SectionName => "fuel";

    /// <summary>After the OFP import, before refuelling, the FO reads the destination TAF (or
    /// METAR) at the ETA and the alternate, and — when it recommends extra fuel — asks the
    /// captain "take it?". Yes raises the INIT block fuel by the figure. Off = "fuel decision" on
    /// request only.</summary>
    [OptionField(FieldKind.Bool, "appSettings.fuelDecision.advice")]
    public bool DecisionAdvice { get; set; }

    /// <summary>A320 holding burn, kg per hour (FL100 clean, mid-weight) — behind every
    /// "minutes ↔ kilos" the advice and the holding time use.</summary>
    [OptionField(FieldKind.Number, "appSettings.fuelDecision.holdingBurn", Suffix = "kg/h", Min = "500", Max = "5000", Step = "50")]
    public int HoldingBurnKgPerHour { get; set; } = 2200;

    /// <summary>Holding recommended when thunderstorms are forecast at the destination around the ETA.</summary>
    [OptionField(FieldKind.Number, "appSettings.fuelDecision.thunderstormMinutes", Suffix = "min", Min = "0", Max = "60", DependsOn = "DecisionAdvice")]
    public int ThunderstormHoldingMinutes { get; set; } = 20;

    /// <summary>Holding recommended when the destination is marginal at the ETA (ceiling within
    /// 500 ft or visibility within 1000 m of the minima).</summary>
    [OptionField(FieldKind.Number, "appSettings.fuelDecision.marginalMinutes", Suffix = "min", Min = "0", Max = "60", DependsOn = "DecisionAdvice")]
    public int MarginalHoldingMinutes { get; set; } = 15;

    /// <summary>Holding recommended when the destination is below minima at the ETA.</summary>
    [OptionField(FieldKind.Number, "appSettings.fuelDecision.belowMinimaMinutes", Suffix = "min", Min = "0", Max = "90", DependsOn = "DecisionAdvice")]
    public int BelowMinimaHoldingMinutes { get; set; } = 30;
}
