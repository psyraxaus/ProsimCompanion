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
    public int GateFinalCallPaxPercent { get; set; } = 90;

    /// <summary>The gate monitor also goes FINAL CALL this many minutes before the scheduled
    /// departure (OFP STD, or the Loadsheet page's manual STD). 0 disables the time trigger.</summary>
    public int GateFinalCallMinutesBeforeStd { get; set; } = 10;

    /// <summary>How often the two weather cards re-probe the weather chain while a flight
    /// plan is loaded. The cards also refresh on every OFP change.</summary>
    public int WeatherRefreshMinutes { get; set; } = 10;
}
