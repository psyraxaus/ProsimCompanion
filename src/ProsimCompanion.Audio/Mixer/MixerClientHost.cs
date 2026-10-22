using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// Runs one <see cref="MixerClient"/> per enabled agent block (#178) and is the app's
/// <see cref="IMixerClients"/>. On every options change it reconciles: a block that vanished,
/// was switched off or changed its host/port/token has its client cancelled and disposed; a
/// new or changed block gets a fresh client. Each client reconnects on its own backoff, so
/// one pilot PC being off never touches the other. The host also owns the single footer
/// <c>Mixer</c> dot: an aggregate of the running clients. Off (or no agent with a host)
/// means no socket at all; the loop sleeps until the options change.
/// </summary>
public sealed class MixerClientHost : BackgroundService, IMixerClients
{
    private readonly IOptionsMonitor<MixerOptions> _options;
    private readonly ConnectionStatusStore _status;
    private readonly MixerStatusStore _store;
    private readonly IWireTrace _wire;
    private readonly ILogger<MixerClient> _clientLogger;
    private readonly ILogger<MixerClientHost> _logger;
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IMixerClient> _all = [];

    public MixerClientHost(
        IOptionsMonitor<MixerOptions> options,
        ConnectionStatusStore status,
        MixerStatusStore store,
        IWireTrace wire,
        ILogger<MixerClient> clientLogger,
        ILogger<MixerClientHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(clientLogger);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _status = status;
        _store = store;
        _wire = wire;
        _clientLogger = clientLogger;
        _logger = logger;
    }

    public IReadOnlyList<IMixerClient> All => _all;

    public event EventHandler? ClientsChanged;

    public IMixerClient? Find(string? agentName)
    {
        var agent = _options.CurrentValue.FindAgent(agentName);
        if (agent is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _running.TryGetValue(MixerAgentOptions.CleanName(agent.Name), out var running) ? running.Client : null;
        }
    }

    public Task<MixerProbeResult> ProbeAsync(string host, int port, string token, CancellationToken cancellationToken = default) =>
        MixerProbe.ProbeAsync(host, port, token, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscription = _options.OnChange(_ => changed.TrySetResult());
                await ReconcileAsync(_options.CurrentValue).ConfigureAwait(false);
                await changed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        finally
        {
            await ReconcileAsync(new MixerOptions { Enabled = false }).ConfigureAwait(false);
        }
    }

    /// <summary>Brings the running set in line with the options: retire what is gone or
    /// changed, start what is new, then publish the roster and the footer aggregate.</summary>
    private async Task ReconcileAsync(MixerOptions options)
    {
        await _reconcile.WaitAsync().ConfigureAwait(false);
        try
        {
            var agents = options.EffectiveAgents();
            var desired = new Dictionary<string, MixerAgentOptions>(StringComparer.OrdinalIgnoreCase);
            if (options.Enabled)
            {
                foreach (var agent in agents)
                {
                    var name = MixerAgentOptions.CleanName(agent.Name);
                    if (!agent.Enabled || string.IsNullOrWhiteSpace(agent.Host) || name.Length == 0)
                    {
                        continue;
                    }

                    if (!desired.TryAdd(name, agent))
                    {
                        _logger.LogWarning("Mixer agent {Agent} is listed twice; only the first block runs", name);
                    }
                }
            }

            List<Running> retire = [];
            List<MixerAgentOptions> start = [];
            lock (_gate)
            {
                foreach (var (name, running) in _running)
                {
                    if (!desired.TryGetValue(name, out var agent) || !string.Equals(MixerClient.SessionStampOf(agent), running.SessionStamp, StringComparison.Ordinal))
                    {
                        retire.Add(running);
                    }
                }

                foreach (var running in retire)
                {
                    _running.Remove(running.Client.Name);
                }

                foreach (var (name, agent) in desired)
                {
                    if (!_running.ContainsKey(name))
                    {
                        start.Add(agent);
                    }
                }
            }

            foreach (var running in retire)
            {
                await RetireAsync(running).ConfigureAwait(false);
            }

            foreach (var agent in start)
            {
                Start(agent);
            }

            // The roster in settings order: running clients report themselves, the rest
            // sit in the list as Off so the card can say which block is idle and why.
            var roster = new List<MixerAgentStatus>(agents.Count);
            foreach (var agent in agents)
            {
                var name = MixerAgentOptions.CleanName(agent.Name);
                MixerClient? client;
                lock (_gate)
                {
                    client = _running.TryGetValue(name, out var running) ? running.Client : null;
                }

                roster.Add(client?.Status ?? MixerAgentStatus.Off(
                    name,
                    $"{MixerAgentOptions.CleanName(agent.Host)}:{agent.Port}",
                    !options.Enabled ? null : !agent.Enabled ? "agent off" : string.IsNullOrWhiteSpace(agent.Host) ? "no host configured" : null));
            }

            IReadOnlyList<IMixerClient> all;
            lock (_gate)
            {
                all = agents
                    .Select(a => _running.TryGetValue(MixerAgentOptions.CleanName(a.Name), out var running) ? running.Client : null)
                    .Where(c => c is not null)
                    .Select(c => (IMixerClient)c!)
                    .ToList();
                _all = all;
            }

            _store.Update(s => s with { Enabled = options.Enabled, Agents = roster });
            UpdateAggregate(options);

            if (retire.Count > 0 || start.Count > 0)
            {
                _logger.LogInformation("Mixer agents running: {Count} ({Names})", all.Count, string.Join(", ", all.Select(c => c.Name)));
                ClientsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _reconcile.Release();
        }
    }

    private void Start(MixerAgentOptions agent)
    {
        var client = new MixerClient(agent, _options, _store, _wire, _clientLogger);
        var cts = new CancellationTokenSource();
        client.StateChanged += OnClientStateChanged;
        var task = RunGuardedAsync(client, cts.Token);
        lock (_gate)
        {
            _running[client.Name] = new Running(client, cts, task, MixerClient.SessionStampOf(agent));
        }
    }

    private async Task RunGuardedAsync(MixerClient client, CancellationToken token)
    {
        try
        {
            await client.RunAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The loop catches every expected failure; anything else must not kill the host.
            _logger.LogError(ex, "Mixer [{Agent}] client stopped unexpectedly", client.Name);
        }
    }

    private async Task RetireAsync(Running running)
    {
        running.Cts.Cancel();
        try
        {
            await running.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Mixer [{Agent}] client did not stop within 5 s; dropping it", running.Client.Name);
        }

        running.Client.StateChanged -= OnClientStateChanged;
        running.Client.Dispose();
        running.Cts.Dispose();
    }

    private void OnClientStateChanged(object? sender, EventArgs e) => UpdateAggregate(_options.CurrentValue);

    /// <summary>The one footer dot over every running client: Connected when all are,
    /// Connecting while none is down but one is still on its way, Disconnected when any is
    /// down ("1 of 2 agents connected"), Disabled when nothing runs.</summary>
    private void UpdateAggregate(MixerOptions options)
    {
        List<MixerClient> clients;
        lock (_gate)
        {
            clients = _running.Values.Select(r => r.Client).ToList();
        }

        if (clients.Count == 0)
        {
            var reason = !options.Enabled ? null
                : options.EffectiveAgents().Count == 0 ? "no agent configured"
                : "no agent enabled";
            _status.Set(Subsystems.Mixer, ConnectionState.Disabled, reason);
            return;
        }

        var connected = clients.Count(c => c.State == MixerConnectionState.Connected);
        var detail = clients.Count > 1 ? $"{connected} of {clients.Count} agents connected" : null;
        if (clients.Any(c => c.State == MixerConnectionState.Disconnected))
        {
            _status.Set(Subsystems.Mixer, ConnectionState.Disconnected, detail ?? clients[0].Status.LastError);
        }
        else if (clients.Any(c => c.State != MixerConnectionState.Connected))
        {
            _status.Set(Subsystems.Mixer, ConnectionState.Connecting, detail);
        }
        else
        {
            _status.Set(Subsystems.Mixer, ConnectionState.Connected, detail);
        }
    }

    public override void Dispose()
    {
        List<Running> running;
        lock (_gate)
        {
            running = [.. _running.Values];
            _running.Clear();
        }

        foreach (var entry in running)
        {
            entry.Cts.Cancel();
            entry.Client.Dispose();
            entry.Cts.Dispose();
        }

        _reconcile.Dispose();
        base.Dispose();
    }

    private sealed record Running(MixerClient Client, CancellationTokenSource Cts, Task Task, string SessionStamp);
}
