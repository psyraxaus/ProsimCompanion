namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the remote mixer client (<c>mixer</c>, 2026-10-10): a VoicemeeterBridge agent
/// on another PC exposes that PC's Voicemeeter over a WebSocket (protocol v1,
/// <c>claude/mixer-protocol.md</c>). Off by default; while off the client opens no socket and
/// the mapping layer subscribes nothing, so the feature costs nothing at startup. The local
/// VoiceMeeter backend of the audio pillar is a different thing: it drives the DLL on THIS PC.
/// </summary>
public sealed class MixerOptions : IOptionSection
{
    public static string SectionName => "mixer";

    /// <summary>Master switch. Turning it on in the web UI starts the client without a restart.</summary>
    public bool Enabled { get; set; }

    /// <summary>Host name or IP of the PC running VoicemeeterBridge. Empty = not configured.</summary>
    public string Host { get; set; } = "";

    /// <summary>Agent listening port (the agent's default is 5088).</summary>
    public int Port { get; set; } = 5088;

    /// <summary>Shared secret from the agent's tray menu ("Copy token"). DPAPI-protected on
    /// disk (<see cref="SecretProtector.SecretPaths"/>) and never logged or wire-traced.</summary>
    public string Token { get; set; } = "";

    /// <summary>First reconnect delay after a drop or a refused connection.</summary>
    public int ReconnectDelayMs { get; set; } = 3000;

    /// <summary>Ceiling of the doubling reconnect backoff (an agent that is off for the
    /// whole flight is retried at this cadence, not every three seconds).</summary>
    public int ReconnectMaxDelayMs { get; set; } = 30_000;

    /// <summary>How long a <c>set</c> waits for its <c>result</c> before reporting a timeout.</summary>
    public int SetTimeoutMs { get; set; } = 3000;

    /// <summary>Strip indices (0-based) the mixer status panel shows and watches.</summary>
    public List<int> PanelStrips { get; set; } = [];

    /// <summary>Bus indices (0-based) the mixer status panel shows and watches.</summary>
    public List<int> PanelBuses { get; set; } = [];

    /// <summary>Gain sent with the knob fully down. −60 dB is as good as silent.</summary>
    public double GainMinDb { get; set; } = -60;

    /// <summary>Gain sent with the knob fully up. +12 dB mirrors the local VoiceMeeter backend
    /// (past unity; 0 dB sits near 83 % of the knob). Set 0 for a strip that must never go
    /// past unity.</summary>
    public double GainMaxDb { get; set; } = 12;

    /// <summary>Audio-panel channel → strip/bus bindings, one row per channel. Empty by default.</summary>
    public List<MixerMapping> Mappings { get; set; } = [];
}

/// <summary>
/// One audio-panel channel bound to one strip or bus on the mixer PC, shaped like the local
/// <see cref="VoiceMeeterTargetMapping"/> (owner request 2026-10-10: same editor as the
/// VoiceMeeter page, a Latch tick instead of separate mute rows). The knob drives the
/// target's Gain over <see cref="MixerOptions.GainMinDb"/>…<see cref="MixerOptions.GainMaxDb"/>;
/// with <see cref="UseLatch"/> the REC push-button drives its Mute (the loudspeaker dial,
/// which has no button, mutes fully down).
/// </summary>
public sealed class MixerMapping
{
    /// <summary>Off keeps the row without subscribing its knob.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which audio panel the channel is read from.</summary>
    public AcpSide Acp { get; set; } = AcpSide.Captain;

    public AudioChannel Channel { get; set; } = AudioChannel.Vhf1;

    /// <summary>0-based strip/bus index as the Remote API counts (UI shows the mixer PC's names).</summary>
    public int StripIndex { get; set; }

    /// <summary>Target a bus instead of a strip.</summary>
    public bool IsBus { get; set; }

    /// <summary>Drive the target's Mute from the channel's REC push-button.</summary>
    public bool UseLatch { get; set; } = true;
}
