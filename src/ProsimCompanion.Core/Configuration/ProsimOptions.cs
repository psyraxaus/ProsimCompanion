namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Connection settings for the ProSim system. The SDK itself is loaded at runtime from the user's
/// ProSim installation and is never redistributed (see docs/integrations/prosim.md).
/// </summary>
public sealed class ProsimOptions
{
    public const string SectionName = "prosim";

    /// <summary>Hostname or IP of the machine running ProSim System.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Directory containing ProSimSDK.dll (a full dll path is also accepted). No default on
    /// purpose: the installer prompts for it and writes it here; it can also be set on the web
    /// Settings page. While empty the ProSim subsystem stays disabled with guidance in the log.
    /// </summary>
    public string SdkPath { get; set; } = "";

    /// <summary>Optional SDK API key (newer ProSim builds); null/empty when not used.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Delay between reconnect attempts when the connection is lost.</summary>
    public int ReconnectIntervalMs { get; set; } = 2000;

    /// <summary>Hold time for a momentary switch press (write 1 → hold → write 0).</summary>
    public int MomentaryPressHoldMs { get; set; } = 150;

    /// <summary>Minimum gap between serialized momentary presses.</summary>
    public int MomentaryPressGapMs { get; set; } = 120;

    /// <summary>
    /// Seconds after which a blocked SDK registration round-trip or a blocked subscriber callback
    /// is declared wedged. A wedged registration rebuilds the whole SDK session; a wedged
    /// subscriber is only logged, because no reconnect can free a stuck callback (issue #35: the
    /// 2026-08-09 wedge froze every dataref cache silently for 12 minutes).
    /// </summary>
    public int SdkStallSeconds { get; set; } = 15;

    /// <summary>
    /// Seconds of dataref-push silence while connected before a single warning is logged. Silence
    /// can be legitimate (sim paused, ProSim idle), so this warns for visibility and never forces
    /// a reconnect.
    /// </summary>
    public int PushSilenceWarnSeconds { get; set; } = 30;
}
