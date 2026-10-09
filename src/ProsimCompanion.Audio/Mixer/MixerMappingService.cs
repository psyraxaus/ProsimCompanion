using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// Binds ProSim values to remote mixer parameters: one dynamic dataref subscription per
/// enabled mapping, the pure maths in <see cref="MixerMappingMath"/>, and the 50 ms
/// <see cref="LatestValueCoalescer"/> tick that caps writes at ~20 per second per parameter.
/// Dataref callbacks arrive on the SDK thread and only drop a number into the coalescer; the
/// tick loop does the sends. No ACP power gate (owner decision 2026-10-10: the knob value goes
/// out whether the panel is powered or not). While the feature is off, or has no enabled
/// mapping, nothing is subscribed and the loop sleeps until the options change.
/// </summary>
public sealed class MixerMappingService : BackgroundService
{
    private readonly IOptionsMonitor<MixerOptions> _options;
    private readonly IProsimDataRefs _prosim;
    private readonly IMixerClient _client;
    private readonly MixerStatusStore _store;
    private readonly ILogger<MixerMappingService> _logger;
    private readonly LatestValueCoalescer _coalescer = new();
    private readonly object _gate = new();
    private readonly List<Binding> _bindings = [];

    public MixerMappingService(
        IOptionsMonitor<MixerOptions> options,
        IProsimDataRefs prosim,
        IMixerClient client,
        MixerStatusStore store,
        ILogger<MixerMappingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _prosim = prosim;
        _client = client;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.StateChanged += OnClientStateChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var untilChange = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var subscription = _options.OnChange(_ => untilChange.Cancel());

                var options = _options.CurrentValue;
                var active = options.Enabled
                    ? options.Mappings.Where(IsUsable).ToList()
                    : [];

                Bind(active);
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
                    // Options changed — rebind.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        finally
        {
            _client.StateChanged -= OnClientStateChanged;
            Bind([]);
        }
    }

    /// <summary>One flush per tick: every parameter with a new value gets one set; the result
    /// lands on its status row. Sets run concurrently and are never awaited by the tick.</summary>
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
        if (_client.State != MixerConnectionState.Connected)
        {
            // Not connected: leave the values pending so the first flush after the welcome
            // carries the current state (the reconnect handler re-offers everything anyway).
            return;
        }

        foreach (var (parameter, value) in _coalescer.Drain())
        {
            _ = SendAsync(parameter, value);
        }
    }

    private async Task SendAsync(string parameter, double value)
    {
        var result = await _client.SetAsync(parameter, value).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var binding in _bindings)
            {
                if (binding.Mapping.Parameter == parameter)
                {
                    binding.LastResult = result.Code;
                }
            }
        }

        if (!result.Ok)
        {
            // The client logged the failure with its code; a failed write must not count as
            // "already at this value", so the next knob move resends it.
            _coalescer.Forget(parameter);
        }

        PublishRows();
    }

    private void OnClientStateChanged(object? sender, EventArgs e)
    {
        if (_client.State != MixerConnectionState.Connected)
        {
            return;
        }

        // Fresh session (agent or Voicemeeter may have restarted): replay every current output.
        _coalescer.Reset();
        lock (_gate)
        {
            foreach (var binding in _bindings)
            {
                if (binding.LastOutput is { } output)
                {
                    _coalescer.Offer(binding.Mapping.Parameter, output);
                }
            }
        }
    }

    private static bool IsUsable(MixerMapping mapping) =>
        mapping.Enabled
        && !string.IsNullOrWhiteSpace(mapping.Source)
        && !string.IsNullOrWhiteSpace(mapping.Parameter);

    private void Bind(IReadOnlyList<MixerMapping> mappings)
    {
        lock (_gate)
        {
            foreach (var binding in _bindings)
            {
                binding.Dispose();
            }

            _bindings.Clear();
            _coalescer.Reset();

            foreach (var mapping in mappings)
            {
                // Escape hatch: the source is a user-typed dataref name (the page offers the ACP
                // knob/latch names; any readable dataref works). Frequent tier — knobs move fast.
                var subscription = _prosim.SubscribeDynamic(mapping.Source.Trim(), DataRefTier.Frequent);
                var binding = new Binding(mapping, subscription);
                subscription.ValueChanged += (_, _) => OnSourceChanged(binding);
                _bindings.Add(binding);

                // A shared registration may already hold a value — seed from it.
                if (subscription.RawValue is not null)
                {
                    OnSourceChanged(binding);
                }
            }

            if (_bindings.Count > 0)
            {
                _logger.LogInformation("Mixer mappings bound: {Count}", _bindings.Count);
            }
        }

        PublishRows();
    }

    private void OnSourceChanged(Binding binding)
    {
        var raw = binding.Subscription.RawValue;
        if (raw is null)
        {
            return;
        }

        var input = binding.Subscription.GetValue(double.NaN);
        if (double.IsNaN(input))
        {
            return;
        }

        var output = MixerMappingMath.Apply(binding.Mapping, input);
        binding.LastInput = input;
        binding.LastOutput = output;
        _coalescer.Offer(binding.Mapping.Parameter, output);
    }

    private void PublishRows()
    {
        MixerMappingStatus[] rows;
        lock (_gate)
        {
            rows = [.. _bindings.Select(b => new MixerMappingStatus(
                b.Mapping.Source, b.Mapping.Parameter, b.LastInput, b.LastOutput, b.LastResult))];
        }

        _store.Update(s => s with { Mappings = rows });
    }

    private sealed class Binding(MixerMapping mapping, IDataRefSubscription subscription) : IDisposable
    {
        public MixerMapping Mapping { get; } = mapping;

        public IDataRefSubscription Subscription { get; } = subscription;

        public double? LastInput { get; set; }

        public double? LastOutput { get; set; }

        public string? LastResult { get; set; }

        public void Dispose() => Subscription.Dispose();
    }
}
