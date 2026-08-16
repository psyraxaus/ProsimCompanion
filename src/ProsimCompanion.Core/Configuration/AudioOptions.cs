namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the cockpit audio-control pillar: ACP volume knobs / REC latches driving either
/// Windows per-app session volumes (CoreAudio) or VoiceMeeter strips/buses. Knob range is
/// 0–1024 (see docs/integrations/audio.md); ProSim is strictly the source of truth — knob and
/// latch values are never written back.
/// </summary>
public sealed class AudioOptions : IOptionSection
{
    public static string SectionName => "audio";

    /// <summary>Master switch for the audio-control pillar.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Active volume target. Switching at runtime hands control over cleanly:
    /// CoreAudio session volumes are restored, VoiceMeeter strips reset to 0 dB.</summary>
    public AudioBackend Backend { get; set; } = AudioBackend.CoreAudio;

    // ---- CoreAudio backend ----

    /// <summary>Which ACP the CoreAudio mappings listen to (the VoiceMeeter backend instead
    /// supports multiple ACPs via <see cref="ActiveAcps"/>).</summary>
    public AcpSide CoreAudioAcp { get; set; } = AcpSide.Captain;

    /// <summary>The out-of-the-box app mappings (fresh instances per call — the entries are
    /// mutable): VHF1 → ATC clients, INT → GSX (Couatl), CAB → the simulator itself.</summary>
    public static IReadOnlyList<AudioAppMapping> DefaultAppMappings =>
    [
        new(AudioChannel.Vhf1, "vPilot"),
        new(AudioChannel.Vhf1, "BeyondATC"),
        new(AudioChannel.Vhf1, "Pilot2ATC_2021"),
        new(AudioChannel.Intercom, "Couatl64_MSFS"),
        new(AudioChannel.Intercom, "Couatl64_MSFS2024"),
        new(AudioChannel.Cabin, "FlightSimulator"),
        new(AudioChannel.Cabin, "FlightSimulator2024"),
    ];

    /// <summary>Per-process CoreAudio mappings.</summary>
    public List<AudioAppMapping> AppMappings { get; set; } = [.. DefaultAppMappings];

    /// <summary>Audio devices to skip during enumeration, matched on the START of the device
    /// friendly name (case-insensitive). Devices whose session enumeration reproducibly fails
    /// belong here.</summary>
    public List<string> DeviceBlacklist { get; set; } = [];

    /// <summary>CoreAudio enumeration DataFlow scope: "render" | "capture" | "all".
    /// Troubleshooting only (predecessor Device-Filter DataFlow) — change as advised.</summary>
    public string DeviceFilterFlow { get; set; } = "render";

    /// <summary>CoreAudio enumeration DeviceState filter: "active" | "disabled" |
    /// "notPresent" | "unplugged" | "all". Troubleshooting only (predecessor
    /// Device-Filter State).</summary>
    public string DeviceFilterState { get; set; } = "active";

    // ---- VoiceMeeter backend ----

    /// <summary>Full path to VoicemeeterRemote64.dll (never redistributed — typically
    /// C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll). Empty (or a stale
    /// path) auto-detects the installed VoiceMeeter via its uninstall registry key and the
    /// default install folders; set explicitly only for non-standard installs.</summary>
    public string VoiceMeeterDllPath { get; set; } = "";

    /// <summary>Which ACPs drive VoiceMeeter targets (each with its own mapping list).</summary>
    public List<AcpSide> ActiveAcps { get; set; } = [AcpSide.Captain];

    /// <summary>Per-ACP VoiceMeeter mappings. Keys are ACP names ("captain", "firstOfficer",
    /// "observer"). Invalid combinations (duplicate channel within an ACP, duplicate strip/bus
    /// target across ACPs) fall back to Captain-only for the session without touching this
    /// config — fix and save to rebind.</summary>
    public Dictionary<string, List<VoiceMeeterTargetMapping>> VoiceMeeterMappings { get; set; } = [];

    // ---- Housekeeping cadences (predecessor-proven values) ----

    /// <summary>Main housekeeping tick (backend switch detection, process/session upkeep).</summary>
    public int RunIntervalMs { get; set; } = 1000;

    /// <summary>Throttle for scanning mapped processes (Process.GetProcessesByName).</summary>
    public int ProcessCheckIntervalMs { get; set; } = 2500;

    /// <summary>Grace delay after a mapped process appears before its sessions are bound —
    /// a just-started app needs a moment to create its audio session.</summary>
    public int ProcessStartupDelayMs { get; set; } = 2000;

    /// <summary>Throttle for full audio-device rescans.</summary>
    public int DeviceCheckIntervalMs { get; set; } = 60_000;

    /// <summary>After this many consecutive failed session lookups for a running process,
    /// force a full device rescan (the session may live on a newly arrived device).</summary>
    public int ProcessMaxSearchCount { get; set; } = 30;

    // ---- ProSim coexistence ----

    /// <summary>Clear ProSim's native per-window audio bindings
    /// (aircraft.communication.windows.*) once per ProSim connection while this application
    /// drives the volumes — never let both control the same sessions.</summary>
    public bool DisableProsimNativeAudio { get; set; } = true;
}
