namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Flight Status hero-card settings (owner request 2026-09-22: the weather cards and the gate
/// monitor) — the "flightStatus" section of config/settings.json. Every default is safe for a
/// missing section; each property has a control on Settings → Display &amp; Flight Data.
/// </summary>
public sealed class FlightStatusOptions : IOptionSection
{
    public static string SectionName => "flightStatus";

    /// <summary>The gate monitor goes FINAL CALL once this share of the planned passengers
    /// is on board (GSX boarding counters). 100 disables the pax trigger.</summary>
    [OptionField(FieldKind.Number, "appSettings.flightStatus.finalCallPax", Suffix = "%", Min = "1", Max = "100")]
    public int GateFinalCallPaxPercent { get; set; } = 90;

    /// <summary>The gate monitor also goes FINAL CALL this many minutes before the scheduled
    /// departure (OFP STD, or the Loadsheet page's manual STD). 0 disables the time trigger.</summary>
    [OptionField(FieldKind.Number, "appSettings.flightStatus.finalCallStd", Suffix = "min", Min = "0")]
    public int GateFinalCallMinutesBeforeStd { get; set; } = 10;

    /// <summary>How often the two weather cards re-probe the weather chain while a flight
    /// plan is loaded. The cards also refresh on every OFP change.</summary>
    [OptionField(FieldKind.Number, "appSettings.flightStatus.weatherRefresh", Suffix = "min", Min = "1", Max = "120")]
    public int WeatherRefreshMinutes { get; set; } = 10;

    /// <summary>How many minutes before the estimated top of descent the once-per-flight
    /// <c>tod-approaching</c> session event is published (issue #145; later packages hang a
    /// notification and an FO line off it). 0 disables the event; the figure on the Flight
    /// Monitor board and Flight Status is shown either way.</summary>
    [OptionField(FieldKind.Number, "appSettings.flightStatus.todNotice", Suffix = "min", Min = "0", Max = "60")]
    public int TodLeadMinutes { get; set; } = 10;

    /// <summary>The Flight Status tiles in the order the pilot dragged them into (issue #160):
    /// ids from <c>FlightStatusTiles</c> ("hero", "sequence", "sim", "app", "gsx",
    /// "services"). Written by the page itself on every drop — never through the dirty bar.
    /// Empty = the default order; a partial or stale list is folded into a complete one, so
    /// no tile can disappear.</summary>
    public List<string> TileOrder { get; set; } = [];
}
