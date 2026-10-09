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

    /// <summary>ProSim value → Voicemeeter parameter bindings. Empty by default.</summary>
    public List<MixerMapping> Mappings { get; set; } = [];
}

/// <summary>How a mapping turns its ProSim value into the parameter value it sends.</summary>
public enum MixerMappingKind
{
    /// <summary>Linear scale from the input range onto the output dB range (knob → gain).</summary>
    Level,

    /// <summary>Input at or above the threshold sends 1, below sends 0 (latch → mute).</summary>
    Toggle,
}

/// <summary>
/// One ProSim value bound to one Voicemeeter parameter. The source is a raw ProSim dataref
/// name (the page offers the ACP knob and REC-latch names; any readable dataref works).
/// Input/output ranges are plain numbers so the maths is testable and the JSON is readable.
/// </summary>
public sealed class MixerMapping
{
    /// <summary>Off keeps the row without subscribing its source.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ProSim dataref name, e.g. <c>system.analog.A_ASP_VHF_1_VOLUME</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>Voicemeeter Remote API parameter name, e.g. <c>Strip[2].Gain</c> or
    /// <c>Bus[0].Mute</c>. The agent validates the spelling.</summary>
    public string Parameter { get; set; } = "";

    public MixerMappingKind Kind { get; set; } = MixerMappingKind.Level;

    /// <summary>Input value that maps to <see cref="OutputMinDb"/> (ACP knobs run 0–1024).</summary>
    public double InputMin { get; set; }

    /// <summary>Input value that maps to <see cref="OutputMaxDb"/>.</summary>
    public double InputMax { get; set; } = 1024;

    /// <summary>Level only: gain sent at <see cref="InputMin"/>.</summary>
    public double OutputMinDb { get; set; } = -60;

    /// <summary>Level only: gain sent at <see cref="InputMax"/> (+12 dB mirrors the local
    /// VoiceMeeter backend: full knob sits past unity, 0 dB near 83 %).</summary>
    public double OutputMaxDb { get; set; } = 12;

    /// <summary>Toggle only: input at or above this sends 1 (before <see cref="Invert"/>).</summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>Flip the result: a level runs max→min across the input range; a toggle sends
    /// 0 where it would send 1. A REC latch (1 = unmuted) driving a Mute parameter needs this.</summary>
    public bool Invert { get; set; }
}
