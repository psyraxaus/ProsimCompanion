namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// ActiveSky (HiFi) weather-source settings. ActiveSky carries the weather actually injected
/// into the sim, so when present it outranks every network source in the composite provider
/// chain. Both access paths are optional — a machine without ActiveSky just falls through to
/// the ProSim gateway / SayIntentions tiers.
/// </summary>
public sealed class WeatherOptions : IOptionSection
{
    public static string SectionName => "weather";

    /// <summary>Explicit path to ActiveSky's <c>current_wx_snapshot.txt</c>. When set it is
    /// authoritative: a missing file disables the snapshot tier rather than falling back to
    /// auto-detection (so a deliberate path never silently reads a different install). Blank
    /// (default) probes the known HiFi install locations.</summary>
    public string ActiveSkySnapshotPath { get; set; } = "";

    /// <summary>Also try ActiveSky's local HTTP API when the snapshot yields nothing.</summary>
    public bool UseActiveSkyApi { get; set; } = true;

    public string ActiveSkyApiHost { get; set; } = "localhost";

    public int ActiveSkyApiPort { get; set; } = 19285;

    /// <summary>Short by design — the API is a localhost call and the composite chain should
    /// fall through quickly when ActiveSky isn't running.</summary>
    public int ActiveSkyApiTimeoutSeconds { get; set; } = 3;
}
