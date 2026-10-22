using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.State;

/// <summary>Live state of one mapping row, for the settings page: which agent the row names
/// (as written — empty = the first agent), the gain and mute last sent for the channel's
/// target, and how the agent answered the last write (<c>ok</c>, <c>not_connected</c>,
/// <c>no_agent</c> for a name no agent block carries, <c>agent_off</c> for a disabled one).</summary>
public sealed record MixerMappingStatus(
    string Agent,
    AcpSide Acp,
    AudioChannel Channel,
    bool IsBus,
    int StripIndex,
    double? GainDb,
    bool? Muted,
    string? LastResult);

/// <summary>The link to one VoicemeeterBridge agent (#178): one entry per configured agent,
/// in the order of the settings list. A disabled agent, or one without a host, sits in the
/// list as <see cref="MixerConnectionState.Disabled"/> so the status card can say so.</summary>
public sealed record MixerAgentStatus(
    string Name,
    string Endpoint,
    MixerConnectionState State,
    MixerVoicemeeterStatus Voicemeeter,
    string? LastError,
    DateTimeOffset? ConnectedSinceUtc,
    int FailedSets)
{
    /// <summary>An agent the host is not running (feature off, block off, no host).</summary>
    public static MixerAgentStatus Off(string name, string endpoint, string? reason = null) =>
        new(name, endpoint, MixerConnectionState.Disabled, MixerVoicemeeterStatus.Unknown, reason, null, 0);
}

/// <summary>Snapshot the web UI renders for the remote mixer: the agents and the mapping rows.</summary>
public sealed record MixerStatusSnapshot(
    bool Enabled,
    IReadOnlyList<MixerAgentStatus> Agents,
    IReadOnlyList<MixerMappingStatus> Mappings)
{
    public static MixerStatusSnapshot Empty { get; } = new(false, [], []);

    /// <summary>The entry for an agent name (compared like <see cref="MixerAgentOptions.SameName"/>);
    /// an empty name means the first agent. Null when the roster has no such agent.</summary>
    public MixerAgentStatus? Agent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Agents.Count > 0 ? Agents[0] : null;
        }

        return Agents.FirstOrDefault(a => MixerAgentOptions.SameName(a.Name, name));
    }

    /// <summary>Agents the host is running (anything but Disabled).</summary>
    public int ActiveAgents => Agents.Count(a => a.State != MixerConnectionState.Disabled);

    public int ConnectedAgents => Agents.Count(a => a.State == MixerConnectionState.Connected);

    /// <summary>Total of the agents' failed writes — the status card's single pill.</summary>
    public int FailedSets => Agents.Sum(a => a.FailedSets);

    /// <summary>Replaces the entry with the same name, or appends when the roster lacks it
    /// (a client publishing before the host rebuilt the roster).</summary>
    public MixerStatusSnapshot WithAgent(MixerAgentStatus agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var index = -1;
        for (var i = 0; i < Agents.Count; i++)
        {
            if (MixerAgentOptions.SameName(Agents[i].Name, agent.Name))
            {
                index = i;
                break;
            }
        }

        var list = new List<MixerAgentStatus>(Agents);
        if (index < 0)
        {
            list.Add(agent);
        }
        else
        {
            list[index] = agent;
        }

        return this with { Agents = list };
    }
}

/// <summary>Store behind the mixer status panel and the Remote Mixer settings section. The
/// host writes the roster and the enabled flag, each client its own agent entry, the mapping
/// service the rows.</summary>
public sealed class MixerStatusStore : SnapshotStore<MixerStatusSnapshot>
{
    public MixerStatusStore()
        : base(MixerStatusSnapshot.Empty)
    {
    }
}
