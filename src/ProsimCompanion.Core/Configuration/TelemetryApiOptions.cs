namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the read-only telemetry API (<c>/api/telemetry/*</c>, issue #94) — the seam
/// the flight-verification workflow (docs/agents/flight-verification.md) pulls session event
/// logs and log tails through, instead of hand-copying files off the sim PC.
/// </summary>
public sealed class TelemetryApiOptions : IOptionSection
{
    public static string SectionName => "telemetryApi";

    /// <summary>
    /// On by default — unlike the command API this surface is strictly read-only (it serves
    /// files any local process could already read) and the verification workflow should work
    /// out of the box. When disabled every telemetry route answers 404, as if it did not
    /// exist. LAN callers always need the web access token regardless.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Off by default, mirroring the page-serving middleware rather than the command API:
    /// loopback readers of log files gain nothing an on-machine process couldn't get from
    /// the files directly, so demanding the token locally would be ceremony, not security.
    /// </summary>
    public bool RequireTokenOnLoopback { get; set; }
}
