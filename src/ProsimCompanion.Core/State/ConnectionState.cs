namespace ProsimCompanion.Core.State;

/// <summary>Connection lifecycle of an external subsystem (ProSim, SimConnect, GSX, …).</summary>
public enum ConnectionState
{
    /// <summary>The subsystem is turned off in settings or unavailable on this machine.</summary>
    Disabled,

    /// <summary>Not currently connected; the subsystem will keep retrying.</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>Connected and healthy.</summary>
    Connected,
}

/// <summary>Well-known subsystem names used as keys in the <see cref="ConnectionStatusStore"/>.</summary>
public static class Subsystems
{
    public const string Prosim = "ProSim";
    public const string SimConnect = "SimConnect";
    public const string Gsx = "GSX";

    /// <summary>LAN speech recognition (whisper on the voice box). Connected = the LAN engine
    /// answers its health check; Disconnected = the offline engine is covering and the
    /// controller re-probes; Disabled = no LAN ASR configured.</summary>
    public const string Asr = "ASR";

    /// <summary>Network text-to-speech (Kokoro). Connected = last synthesis or health probe
    /// succeeded; Disconnected = cooling down after a failure; Connecting = configured, not
    /// yet exercised; Disabled = not configured or local-only mode.</summary>
    public const string Tts = "TTS";
}
