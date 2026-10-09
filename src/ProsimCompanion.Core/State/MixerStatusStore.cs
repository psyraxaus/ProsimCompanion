namespace ProsimCompanion.Core.State;

/// <summary>Live state of one mapping row, for the settings page: what came in from ProSim,
/// what went out, and how the agent answered the last write.</summary>
public sealed record MixerMappingStatus(
    string Source,
    string Parameter,
    double? LastInput,
    double? LastOutput,
    string? LastResult);

/// <summary>Snapshot the web UI renders for the remote mixer.</summary>
public sealed record MixerStatusSnapshot(
    bool Enabled,
    MixerConnectionState State,
    MixerVoicemeeterStatus Voicemeeter,
    string Endpoint,
    string? LastError,
    DateTimeOffset? ConnectedSinceUtc,
    int FailedSets,
    IReadOnlyList<MixerMappingStatus> Mappings)
{
    public static MixerStatusSnapshot Empty { get; } = new(
        false, MixerConnectionState.Disabled, MixerVoicemeeterStatus.Unknown, "", null, null, 0, []);
}

/// <summary>Store behind the mixer status panel and the Remote Mixer settings section. The
/// client writes the connection half, the mapping service the rows.</summary>
public sealed class MixerStatusStore : SnapshotStore<MixerStatusSnapshot>
{
    public MixerStatusStore()
        : base(MixerStatusSnapshot.Empty)
    {
    }
}
