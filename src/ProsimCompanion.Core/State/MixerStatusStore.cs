namespace ProsimCompanion.Core.State;

/// <summary>Live state of one mapping row, for the settings page: the gain and mute last
/// sent for the channel's target, and how the agent answered the last write.</summary>
public sealed record MixerMappingStatus(
    Configuration.AcpSide Acp,
    Configuration.AudioChannel Channel,
    bool IsBus,
    int StripIndex,
    double? GainDb,
    bool? Muted,
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
