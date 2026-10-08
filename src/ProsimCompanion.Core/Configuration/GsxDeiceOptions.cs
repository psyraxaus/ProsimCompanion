namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The de-icing auto-request policy (<c>gsx.deice.*</c>, 2026-10-09). Until now only GSX could
/// start a de-ice (its "Ice warning" offer, answered per <see cref="GsxOptions.AutoDeIce"/>) or
/// the crew (voice / <c>gsx.requestDeice</c>). This policy lets the app decide from the
/// outside air temperature and the departure METAR whether THIS departure needs it, and either
/// put de-icing on the departure sequence itself or ask the captain first. Pure decision in
/// <c>DeiceRequestPolicy</c>; every verdict is decision-logged.
/// </summary>
public sealed class GsxDeiceOptions
{
    /// <summary><c>"off"</c> (default) never requests; <c>"ask"</c> raises a crew question
    /// ("Captain, conditions call for de-icing — request it?") the FO voices and the Status
    /// board shows; <c>"auto"</c> puts the DeIce step on the departure sequence for this cycle
    /// without asking. Unknown values read as off.</summary>
    public string AutoRequest { get; set; } = AutoRequestOff;

    /// <summary>De-icing is considered at or below this outside air temperature (°C). The
    /// ProSim OAT dataref is used when ProSim reports one, else the METAR temperature.</summary>
    public double OatThresholdC { get; set; } = 3;

    /// <summary>With this on (default), a cold OAT alone is not enough: the departure METAR
    /// must also show precipitation (snow, freezing precipitation, ice pellets, rain at or
    /// below the threshold), freezing fog, or a frost-likely dew-point spread (≤ 3 °C). Off
    /// requests on the temperature alone — the "cold-soaked wing" rule some operators use.
    /// With no METAR for the departure the policy makes no request at all (no data, no call).</summary>
    public bool RequirePrecipitation { get; set; } = true;

    public const string AutoRequestOff = "off";
    public const string AutoRequestAsk = "ask";
    public const string AutoRequestAuto = "auto";
}
