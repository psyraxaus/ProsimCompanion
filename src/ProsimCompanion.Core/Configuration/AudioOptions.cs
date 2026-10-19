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
    [OptionField(FieldKind.Bool, "setup.features.audio")]
    public bool Enabled { get; set; } = true;

    /// <summary>Active volume target. Switching at runtime hands control over cleanly:
    /// CoreAudio session volumes are restored, VoiceMeeter strips reset to 0 dB.</summary>
    [OptionField(FieldKind.Select, "audioSettings.general.backend", Choices = "audioBackend")]
    public AudioBackend Backend { get; set; } = AudioBackend.CoreAudio;

    /// <summary>Follow the knobs and latches only while their audio panel is powered (AC/DC
    /// ESS, DC1, audio switching — docs/integrations/audio.md). Off (owner option 2026-10-10,
    /// to match the remote mixer): the knobs drive the targets whatever the aircraft's
    /// electrical state, which a home sim without cold-and-dark usually wants.</summary>
    [OptionField(FieldKind.Bool, "audioSettings.general.requirePower")]
    public bool RequireAcpPower { get; set; } = true;

    // ---- CoreAudio backend ----

    /// <summary>Which ACP the CoreAudio mappings listen to (the VoiceMeeter backend instead
    /// supports multiple ACPs via <see cref="ActiveAcps"/>).</summary>
    [OptionField(FieldKind.Select, "audioSettings.coreAudio.acp", Choices = "coreAudioAcp")]
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
    [OptionField(FieldKind.Select, "audioSettings.coreAudio.deviceFlow", Choices = "deviceFlow", Advanced = true)]
    public string DeviceFilterFlow { get; set; } = "render";

    /// <summary>CoreAudio enumeration DeviceState filter: "active" | "disabled" |
    /// "notPresent" | "unplugged" | "all". Troubleshooting only (predecessor
    /// Device-Filter State).</summary>
    [OptionField(FieldKind.Select, "audioSettings.coreAudio.deviceState", Choices = "deviceState", Advanced = true)]
    public string DeviceFilterState { get; set; } = "active";

    // ---- VoiceMeeter backend ----

    /// <summary>Full path to VoicemeeterRemote64.dll (never redistributed — typically
    /// C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll). Empty (or a stale
    /// path) auto-detects the installed VoiceMeeter via its uninstall registry key and the
    /// default install folders; set explicitly only for non-standard installs.</summary>
    [OptionField(FieldKind.Text, "audioSettings.voiceMeeter.dllPath", TextKind = TextFieldKind.FilePath, BrowseStartIn = "VoiceMeeter", BrowseFilter = "VoicemeeterRemote64.dll|VoicemeeterRemote64.dll|DLL files (*.dll)|*.dll", Normalize = FieldNormalization.Trim, Advanced = true)]
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
    [OptionField(FieldKind.Number, "audioSettings.housekeeping.runInterval", Suffix = "ms", Min = "250", Advanced = true)]
    public int RunIntervalMs { get; set; } = 1000;

    /// <summary>Throttle for scanning mapped processes (Process.GetProcessesByName).</summary>
    [OptionField(FieldKind.Number, "audioSettings.housekeeping.processCheck", Suffix = "ms", Min = "500", Advanced = true)]
    public int ProcessCheckIntervalMs { get; set; } = 2500;

    /// <summary>Grace delay after a mapped process appears before its sessions are bound —
    /// a just-started app needs a moment to create its audio session.</summary>
    [OptionField(FieldKind.Number, "audioSettings.housekeeping.startupGrace", Suffix = "ms", Min = "0", Advanced = true)]
    public int ProcessStartupDelayMs { get; set; } = 2000;

    /// <summary>Throttle for full audio-device rescans.</summary>
    [OptionField(FieldKind.Number, "audioSettings.housekeeping.deviceRescan", Suffix = "ms", Min = "5000", Advanced = true)]
    public int DeviceCheckIntervalMs { get; set; } = 60_000;

    /// <summary>After this many consecutive failed session lookups for a running process,
    /// force a full device rescan (the session may live on a newly arrived device).</summary>
    [OptionField(FieldKind.Number, "audioSettings.housekeeping.maxSearches", Min = "1", Advanced = true)]
    public int ProcessMaxSearchCount { get; set; } = 30;

    // ---- ProSim coexistence ----

    /// <summary>Clear ProSim's native per-window audio bindings
    /// (aircraft.communication.windows.*) once per ProSim connection while this application
    /// drives the volumes — never let both control the same sessions.</summary>
    [OptionField(FieldKind.Bool, "audioSettings.general.disableProsimNative")]
    public bool DisableProsimNativeAudio { get; set; } = true;
}
