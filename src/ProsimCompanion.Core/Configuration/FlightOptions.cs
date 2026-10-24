namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Flight-level features that follow the aircraft rather than the plan (the <c>flight</c>
/// section, Settings → Display &amp; Flight Data). Today: the diversion workflow (issue #184).
/// </summary>
public sealed class FlightOptions : IOptionSection
{
    public static string SectionName => "flight";

    /// <summary>"Divert to {alternate}" / "divert to {ICAO}" / the OFP page's Diversion card:
    /// the OFP's destination is overlaid with the new one, so the progress figures, the weather
    /// cards and watch, the arrival briefing, the GSX arrival gate, the logbook and the day
    /// engine follow the aircraft; the company is told by ACARS and answers. Off = the commands
    /// refuse and the card stays hidden.</summary>
    [OptionField(FieldKind.Bool, "appSettings.diversion.enabled")]
    public bool DiversionEnabled { get; set; }
}
