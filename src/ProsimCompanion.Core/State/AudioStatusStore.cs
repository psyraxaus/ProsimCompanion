using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.State;

/// <summary>Lifecycle of one CoreAudio app mapping.</summary>
public enum AudioMappingState
{
    /// <summary>The mapped process is not running.</summary>
    NotRunning,

    /// <summary>Process found, but no matching audio session yet (an app creates its session
    /// on first sound; some never do until configured).</summary>
    Searching,

    /// <summary>At least one Windows audio session is under control.</summary>
    Bound,

    /// <summary>The process runs at higher integrity than this application — its sessions are
    /// invisible to us. Run ProsimCompanion as administrator to control it.</summary>
    Elevated,
}

/// <summary>Point-in-time view of one CoreAudio app mapping.</summary>
public sealed record AudioMappingView(
    string Binary,
    AudioChannel Channel,
    string Device,
    AudioMappingState State,
    int SessionCount,
    double? Volume,
    bool? Muted);

/// <summary>Point-in-time view of one bound VoiceMeeter target.</summary>
public sealed record VoiceMeeterBindingView(
    AcpSide Acp,
    AudioChannel Channel,
    int StripIndex,
    bool IsBus,
    double? GainDb,
    bool? Muted);

/// <summary>A strip or bus reported by a running VoiceMeeter (for mapping pickers).</summary>
public sealed record VoiceMeeterTargetView(int Index, bool IsBus, string Label);

/// <summary>Everything the web Audio page renders.</summary>
public sealed record AudioStatusSnapshot(
    bool Enabled,
    AudioBackend ActiveBackend,
    bool VoiceMeeterAvailable,
    string VoiceMeeterFallbackReason,
    IReadOnlyDictionary<AcpSide, bool> AcpPowered,
    IReadOnlyList<AudioMappingView> Mappings,
    IReadOnlyList<VoiceMeeterBindingView> VoiceMeeterBindings)
{
    public static AudioStatusSnapshot Empty { get; } = new(
        Enabled: false,
        ActiveBackend: AudioBackend.CoreAudio,
        VoiceMeeterAvailable: false,
        VoiceMeeterFallbackReason: "",
        AcpPowered: new Dictionary<AcpSide, bool>(),
        Mappings: [],
        VoiceMeeterBindings: []);
}

/// <summary>
/// Live status of the audio-control pillar. Kept in Core so the Web project (which references
/// only Core) can render it. Written by ProsimCompanion.Audio.
/// </summary>
public sealed class AudioStatusStore : SnapshotStore<AudioStatusSnapshot>
{
    public AudioStatusStore()
        : base(AudioStatusSnapshot.Empty)
    {
    }
}

/// <summary>Commands the web UI can issue to the audio pillar (implemented by
/// ProsimCompanion.Audio; registered only when the pillar is).</summary>
public interface IAudioControl
{
    /// <summary>Queries a running VoiceMeeter for its strip/bus inventory — empty when the
    /// DLL is not loaded or VoiceMeeter is not running. Synchronous and sub-ms.</summary>
    IReadOnlyList<VoiceMeeterTargetView> GetVoiceMeeterTargets();

    /// <summary>Writes a mapping/process/device/session snapshot to AudioDebug.txt in the
    /// logs folder (predecessor "Write Debug Info"). Returns the file path, or null when the
    /// dump could not be written. Each dump section degrades independently — an enumeration
    /// failure is recorded inside the dump instead of aborting it.</summary>
    Task<string?> WriteDebugDumpAsync(CancellationToken cancellationToken = default);
}
