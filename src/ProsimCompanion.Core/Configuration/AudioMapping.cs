namespace ProsimCompanion.Core.Configuration;

/// <summary>Which Audio Control Panel a knob/latch reading comes from.</summary>
public enum AcpSide
{
    Captain,
    FirstOfficer,
    Observer,
}

/// <summary>The eight ACP channels — every one is wired end-to-end on all three ACPs.
/// Intercom/Cabin are the panel's INT/CAB knobs (spelled out — analyzer rules).</summary>
public enum AudioChannel
{
    Vhf1,
    Vhf2,
    Vhf3,
    Hf1,
    Hf2,
    Intercom,
    Cabin,
    Pa,
}

/// <summary>Which volume target the ACP knobs drive.</summary>
public enum AudioBackend
{
    /// <summary>Windows per-app session volumes (WASAPI).</summary>
    CoreAudio,

    /// <summary>VoiceMeeter strips/buses via the runtime-loaded VoicemeeterRemote64.dll.</summary>
    VoiceMeeter,
}

/// <summary>
/// One CoreAudio mapping: an ACP channel drives the Windows session volume of every audio
/// session belonging to a process. Multiple mappings may share a channel (e.g. VHF1 driving
/// vPilot and BeyondATC together).
/// </summary>
public sealed class AudioAppMapping
{
    public AudioAppMapping()
    {
    }

    public AudioAppMapping(AudioChannel channel, string binary)
    {
        Channel = channel;
        Binary = binary;
    }

    public AudioChannel Channel { get; set; } = AudioChannel.Vhf1;

    /// <summary>Process name without extension (e.g. "vPilot"). Matched via
    /// Process.GetProcessesByName, with a session-instance-identifier fallback.</summary>
    public string Binary { get; set; } = "";

    /// <summary>Audio device friendly name to restrict the mapping to; empty = all devices.</summary>
    public string Device { get; set; } = "";

    /// <summary>Drive the session's mute from the knob's REC latch. When off, mute is NEVER
    /// written (not "always unmuted") — a session muted elsewhere stays muted.</summary>
    public bool UseLatch { get; set; } = true;

    /// <summary>Only control sessions in the Active state (skip expired/inactive ones).</summary>
    public bool OnlyActive { get; set; } = true;
}

/// <summary>One VoiceMeeter mapping: an ACP channel drives a strip or bus gain (and optionally
/// mute from the REC latch).</summary>
public sealed class VoiceMeeterTargetMapping
{
    public VoiceMeeterTargetMapping()
    {
    }

    public VoiceMeeterTargetMapping(AudioChannel channel, int stripIndex, bool isBus)
    {
        Channel = channel;
        StripIndex = stripIndex;
        IsBus = isBus;
    }

    public AudioChannel Channel { get; set; } = AudioChannel.Vhf1;

    /// <summary>0-based strip/bus index (UI shows 1-based).</summary>
    public int StripIndex { get; set; }

    /// <summary>Target a bus instead of a strip.</summary>
    public bool IsBus { get; set; }

    /// <summary>Drive the strip/bus mute from the knob's REC latch.</summary>
    public bool UseLatch { get; set; }
}
