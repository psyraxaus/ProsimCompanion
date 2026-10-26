using System.Globalization;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Diagnostics;

/// <summary>
/// The REMOTE page's audio half: one numbered box per configured mixer agent (the
/// VoicemeeterBridge on each PC) and one audio-routing column per enabled mapping. The
/// agent roster and the mapping values come from <see cref="MixerStatusStore"/>, which the
/// client host publishes on every change; this collector only adds the "lost for nn s" clock,
/// which no store keeps because nothing else needs it.
/// </summary>
public sealed class RemoteAudioDiagnosticsCollector : IDiagnosticsCollector
{
    private const string System = "REMOTE";

    private readonly MixerStatusStore _store;
    private readonly IOptionsMonitor<MixerOptions> _options;
    private readonly Dictionary<string, DateTimeOffset> _lostSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly StateTransitions<string> _edges = new();

    public RemoteAudioDiagnosticsCollector(MixerStatusStore store, IOptionsMonitor<MixerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
    }

    public string Page => DiagPageIds.Remote;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var now = page.Events.Now;
        var snap = _store.Snapshot();
        var mappings = _options.CurrentValue.Mappings.Where(m => m.Enabled).ToList();
        page.Readout("mixer_count", snap.Agents.Count.ToString(CultureInfo.InvariantCulture));

        if (!snap.Enabled)
        {
            page.Readout("mixer_off", "OFF");
        }

        var lostAgents = new List<string>();
        var states = new List<KeyValuePair<string, DiagState>>();
        for (var i = 0; i < snap.Agents.Count; i++)
        {
            var agent = snap.Agents[i];
            var id = $"mixer_{i + 1}";
            var (state, value, detail) = agent.State switch
            {
                MixerConnectionState.Connected => (DiagState.Normal, agent.Name.ToUpperInvariant(), agent.Voicemeeter.Kind?.ToUpperInvariant() ?? EcamTokens.Conn),
                MixerConnectionState.Connecting => (DiagState.Caution, agent.Name.ToUpperInvariant(), "CONNECTING"),
                MixerConnectionState.Disconnected => (DiagState.Caution, agent.Name.ToUpperInvariant(), $"{EcamTokens.Lost} {LostFor(agent.Name, now)} S"),
                _ => (DiagState.NotApplicable, agent.Name.ToUpperInvariant(), agent.LastError?.ToUpperInvariant() ?? EcamTokens.Off),
            };
            if (agent.State != MixerConnectionState.Disconnected)
            {
                _lostSince.Remove(agent.Name);
            }

            page.Node(id, state, value, detail);
            page.Readout(id + "_endpoint", agent.Endpoint);
            states.Add(new($"MIXER {i + 1}", state));
            if (agent.State == MixerConnectionState.Disconnected)
            {
                lostAgents.Add(agent.Name);
            }
        }

        // Audio routing columns, one per enabled mapping, in settings order.
        for (var i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            var id = $"ch_{i + 1}";
            var status = snap.Mappings.FirstOrDefault(s => s.Channel == mapping.Channel && s.Acp == mapping.Acp && s.StripIndex == mapping.StripIndex);
            var agentName = string.IsNullOrWhiteSpace(mapping.Agent) ? snap.Agents.FirstOrDefault()?.Name ?? "" : mapping.Agent;
            var agentLost = lostAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase);
            page.Readout(id + "_label", ChannelLabel(mapping.Channel));
            page.Readout(id + "_device", $"{(mapping.IsBus ? "BUS" : "STRIP")} {mapping.StripIndex} · {agentName}".ToUpperInvariant());
            if (agentLost || status is null)
            {
                page.Node(id, agentLost ? DiagState.Caution : DiagState.NoData,
                    agentLost ? EcamTokens.Off : EcamTokens.NoData,
                    agentLost ? EcamTokens.Muted : null);
                continue;
            }

            var muted = status.Muted == true;
            var gain = status.GainDb is { } g ? $"{g:+0;-0;0} dB" : null;
            page.Node(id, DiagState.Normal, muted ? EcamTokens.Off : EcamTokens.On, gain);
        }

        if (lostAgents.Count > 0)
        {
            var index = snap.Agents.ToList().FindIndex(a => a.Name.Equals(lostAgents[0], StringComparison.OrdinalIgnoreCase)) + 1;
            var affected = mappings.Where(m => lostAgents.Contains(string.IsNullOrWhiteSpace(m.Agent) ? snap.Agents[0].Name : m.Agent, StringComparer.OrdinalIgnoreCase))
                .Select(m => ChannelLabel(m.Channel)).Distinct().ToList();
            var title = $"MIXER AGENT {index} LOST";
            cautions.Raise(System, title, DiagState.Caution,
                [.. affected.Select(c => new DiagAction($"{c} CHANNEL", EcamTokens.Muted))]);
            foreach (var channel in affected)
            {
                cautions.Inop($"{channel} AUDIO");
            }

            page.CautionLine(affected.Count > 0 ? $"{title} · {affected[0]} MUTED" : title);
        }

        foreach (var (id, _, to) in _edges.Observe(states))
        {
            page.Events.Push($"{id} {(to == DiagState.Normal ? "CONN" : to == DiagState.NotApplicable ? "OFF" : "LOST")}", to);
        }
    }

    private int LostFor(string agent, DateTimeOffset now)
    {
        if (!_lostSince.TryGetValue(agent, out var since))
        {
            since = now;
            _lostSince[agent] = since;
        }

        return (int)(now - since).TotalSeconds;
    }

    /// <summary>The short column heading for a channel.</summary>
    public static string ChannelLabel(AudioChannel channel) => channel switch
    {
        AudioChannel.Vhf1 => "VHF1",
        AudioChannel.Vhf2 => "VHF2",
        AudioChannel.Vhf3 => "VHF3",
        AudioChannel.Hf1 => "HF1",
        AudioChannel.Hf2 => "HF2",
        AudioChannel.Intercom => "INT",
        AudioChannel.Cabin => "CAB",
        AudioChannel.Pa => "PA",
        _ => channel.ToString().ToUpperInvariant(),
    };
}
