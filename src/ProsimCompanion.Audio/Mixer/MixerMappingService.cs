using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// Binds audio-panel channels to strips/buses on the mixer PCs: per enabled mapping the knob
/// analog (typed catalog subscription, same refs as the local backends) drives the target's
/// Gain, and — with Latch ticked — the REC push-button drives its Mute (the loudspeaker dial
/// mutes fully down instead). The pure maths lives in <see cref="MixerMappingMath"/>; the
/// 50 ms <see cref="LatestValueCoalescer"/> tick caps writes at ~20 per second per parameter.
/// Since #178 every row names an agent: the row's values go to that agent's client through
/// that agent's own coalescer (parameter names repeat across PCs — <c>Strip[0].Gain</c> on
/// PC1 and on PC2 are different knobs), and one agent being down never holds up another.
/// Dataref callbacks arrive on the SDK thread and only drop a number into a coalescer; the
/// tick loop does the sends. No ACP power gate (owner decision 2026-10-10: the knob value goes
/// out whether the panel is powered or not). While the feature is off, or has no enabled
/// mapping, nothing is subscribed and the loop sleeps until the options change.
/// </summary>
public sealed class MixerMappingService : BackgroundService
{
    private readonly IOptionsMonitor<MixerOptions> _options;
    private readonly IProsimDataRefs _prosim;
    private readonly IMixerClients _clients;
    private readonly MixerStatusStore _store;
    private readonly ILogger<MixerMappingService> _logger;
    private readonly object _gate = new();
    private readonly List<Binding> _bindings = [];
    private readonly Dictionary<string, LatestValueCoalescer> _coalescers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IMixerClient> _watched = [];

    public MixerMappingService(
        IOptionsMonitor<MixerOptions> options,
        IProsimDataRefs prosim,
        IMixerClients clients,
        MixerStatusStore store,
        ILogger<MixerMappingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _prosim = prosim;
        _clients = clients;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var untilChange = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var subscription = _options.OnChange(_ => untilChange.Cancel());
                EventHandler onClients = (_, _) => untilChange.Cancel();
                _clients.ClientsChanged += onClients;

                var options = _options.CurrentValue;
                var active = options.Enabled
                    ? options.Mappings.Where(m => m.Enabled && m.Channel.ExistsOn(m.Acp)).ToList()
                    : [];

                Bind(active, options);
                try
                {
                    if (active.Count == 0)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, untilChange.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunTicksAsync(untilChange.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Options or the client set changed — rebind.
                }
                finally
                {
                    _clients.ClientsChanged -= onClients;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        finally
        {
            Bind([], _options.CurrentValue);
        }
    }

    /// <summary>One flush per tick: every parameter with a new value gets one set on its
    /// agent; the result lands on its status row. Sets run concurrently and are never
    /// awaited by the tick.</summary>
    private async Task RunTicksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(LatestValueCoalescer.DefaultPeriod);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            Flush();
        }
    }

    private void Flush()
    {
        List<(string Agent, LatestValueCoalescer Coalescer)> agents;
        lock (_gate)
        {
            agents = _coalescers.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        foreach (var (agent, coalescer) in agents)
        {
            var client = _clients.Find(agent);
            if (client is null || client.State != MixerConnectionState.Connected)
            {
                // Not connected: leave the values pending so the first flush after the
                // welcome carries the current state (the reconnect handler re-offers
                // everything anyway). Another agent's flush is not held up by this one.
                continue;
            }

            foreach (var (parameter, value) in coalescer.Drain())
            {
                _ = SendAsync(client, agent, coalescer, parameter, value);
            }
        }
    }

    private async Task SendAsync(IMixerClient client, string agent, LatestValueCoalescer coalescer, string parameter, double value)
    {
        var result = await client.SetAsync(parameter, value).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var binding in _bindings)
            {
                if (binding.AgentName is not null
                    && MixerAgentOptions.SameName(binding.AgentName, agent)
                    && (binding.GainParameter == parameter || binding.MuteParameter == parameter))
                {
                    binding.LastResult = result.Code;
                }
            }
        }

        if (!result.Ok)
        {
            // The client logged the failure with its code; a failed write must not count as
            // "already at this value", so the next knob move resends it.
            coalescer.Forget(parameter);
        }

        PublishRows();
    }

    private void OnClientStateChanged(object? sender, EventArgs e)
    {
        if (sender is not IMixerClient client || client.State != MixerConnectionState.Connected)
        {
            return;
        }

        // Fresh session on this agent (it or its Voicemeeter may have restarted): replay
        // every current output bound to it. Other agents are untouched.
        lock (_gate)
        {
            if (!_coalescers.TryGetValue(client.Name, out var coalescer))
            {
                return;
            }

            coalescer.Reset();
            foreach (var binding in _bindings)
            {
                if (binding.AgentName is null || !MixerAgentOptions.SameName(binding.AgentName, client.Name))
                {
                    continue;
                }

                if (binding.GainDb is { } gain)
                {
                    coalescer.Offer(binding.GainParameter, gain);
                }

                if (binding.Muted is { } muted)
                {
                    coalescer.Offer(binding.MuteParameter, muted ? 1 : 0);
                }
            }
        }
    }

    private void Bind(IReadOnlyList<MixerMapping> mappings, MixerOptions options)
    {
        lock (_gate)
        {
            foreach (var binding in _bindings)
            {
                binding.Dispose();
            }

            _bindings.Clear();
            _coalescers.Clear();

            foreach (var client in _watched)
            {
                client.StateChanged -= OnClientStateChanged;
            }

            _watched.Clear();
            foreach (var client in _clients.All)
            {
                client.StateChanged += OnClientStateChanged;
                _watched.Add(client);
            }

            foreach (var mapping in mappings)
            {
                // Which PC: the row's name resolved against the agent list (empty = the first).
                var agent = options.FindAgent(mapping.Agent);
                string? agentName = null;
                string? reason = null;
                if (agent is null)
                {
                    reason = "no_agent";
                }
                else if (!options.Enabled || !agent.Enabled || string.IsNullOrWhiteSpace(agent.Host))
                {
                    reason = "agent_off";
                }
                else
                {
                    agentName = agent.Name.Trim();
                    if (!_coalescers.ContainsKey(agentName))
                    {
                        _coalescers[agentName] = new LatestValueCoalescer();
                    }
                }

                // Typed catalog refs — the same knob/latch names the local backends subscribe.
                var volume = _prosim.Subscribe(AcpDataRefCatalog.VolumeRef(mapping.Acp, mapping.Channel));
                var latch = mapping.UseLatch && mapping.Channel.HasRecLatch()
                    ? _prosim.Subscribe(AcpDataRefCatalog.LatchRef(mapping.Acp, mapping.Channel))
                    : null;
                var binding = new Binding(mapping, agentName, volume, latch, options.GainMinDb, options.GainMaxDb) { LastResult = reason };
                volume.ValueChanged += (_, _) => OnVolumeChanged(binding);
                if (latch is not null)
                {
                    latch.ValueChanged += (_, _) => OnLatchChanged(binding);
                }

                _bindings.Add(binding);

                // A shared registration may already hold a value — seed from it.
                OnVolumeChanged(binding);
                OnLatchChanged(binding);
            }

            if (_bindings.Count > 0)
            {
                _logger.LogInformation("Mixer mappings bound: {Count} over {Agents} agent(s)", _bindings.Count, _coalescers.Count);
            }
        }

        PublishRows();
    }

    private LatestValueCoalescer? CoalescerOf(Binding binding)
    {
        if (binding.AgentName is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _coalescers.TryGetValue(binding.AgentName, out var coalescer) ? coalescer : null;
        }
    }

    private void OnVolumeChanged(Binding binding)
    {
        // A knob value that never arrived stays unknown — send nothing rather than silence
        // the target at startup.
        if (binding.Volume.RawValue is null)
        {
            return;
        }

        var raw = binding.Volume.Value;
        binding.GainDb = MixerMappingMath.GainDb(raw, binding.MinDb, binding.MaxDb);
        var coalescer = CoalescerOf(binding);
        coalescer?.Offer(binding.GainParameter, binding.GainDb.Value);

        // No push-button (the loudspeaker): the dial is its own mute.
        if (binding.Mapping.UseLatch && binding.Latch is null)
        {
            var mute = MixerMappingMath.MuteFromDial(raw);
            binding.Muted = mute >= 0.5;
            coalescer?.Offer(binding.MuteParameter, mute);
        }
    }

    private void OnLatchChanged(Binding binding)
    {
        if (binding.Latch is null || binding.Latch.RawValue is null)
        {
            return;
        }

        var mute = MixerMappingMath.MuteFromLatch(binding.Latch.Value);
        binding.Muted = mute >= 0.5;
        CoalescerOf(binding)?.Offer(binding.MuteParameter, mute);
    }

    private void PublishRows()
    {
        MixerMappingStatus[] rows;
        lock (_gate)
        {
            rows = [.. _bindings.Select(b => new MixerMappingStatus(
                b.Mapping.Agent, b.Mapping.Acp, b.Mapping.Channel, b.Mapping.IsBus, b.Mapping.StripIndex, b.GainDb, b.Muted, b.LastResult))];
        }

        _store.Update(s => s with { Mappings = rows });
    }

    private sealed class Binding(
        MixerMapping mapping,
        string? agentName,
        IDataRefSubscription<double> volume,
        IDataRefSubscription<int>? latch,
        double minDb,
        double maxDb) : IDisposable
    {
        public MixerMapping Mapping { get; } = mapping;

        /// <summary>The resolved agent the row drives; null when the name matches no running
        /// block (the row then only shows its values, and <see cref="LastResult"/> says why).</summary>
        public string? AgentName { get; } = agentName;

        public IDataRefSubscription<double> Volume { get; } = volume;

        /// <summary>Null when Latch is off or the channel has no push-button (the loudspeaker).</summary>
        public IDataRefSubscription<int>? Latch { get; } = latch;

        public double MinDb { get; } = minDb;

        public double MaxDb { get; } = maxDb;

        public string GainParameter { get; } = MixerChannelNames.Parameter(mapping.IsBus, mapping.StripIndex, "Gain");

        public string MuteParameter { get; } = MixerChannelNames.Parameter(mapping.IsBus, mapping.StripIndex, "Mute");

        public double? GainDb { get; set; }

        public bool? Muted { get; set; }

        public string? LastResult { get; set; }

        public void Dispose()
        {
            Volume.Dispose();
            Latch?.Dispose();
        }
    }
}
