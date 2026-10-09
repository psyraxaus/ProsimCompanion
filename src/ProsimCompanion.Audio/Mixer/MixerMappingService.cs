using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// Binds audio-panel channels to strips/buses on the mixer PC: per enabled mapping the knob
/// analog (typed catalog subscription, same refs as the local backends) drives the target's
/// Gain, and — with Latch ticked — the REC push-button drives its Mute (the loudspeaker dial
/// mutes fully down instead). The pure maths lives in <see cref="MixerMappingMath"/>; the
/// 50 ms <see cref="LatestValueCoalescer"/> tick caps writes at ~20 per second per parameter.
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
            Bind([], _options.CurrentValue);
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
                if (binding.GainParameter == parameter || binding.MuteParameter == parameter)
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
                if (binding.GainDb is { } gain)
                {
                    _coalescer.Offer(binding.GainParameter, gain);
                }

                if (binding.Muted is { } muted)
                {
                    _coalescer.Offer(binding.MuteParameter, muted ? 1 : 0);
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
            _coalescer.Reset();

            foreach (var mapping in mappings)
            {
                // Typed catalog refs — the same knob/latch names the local backends subscribe.
                var volume = _prosim.Subscribe(AcpDataRefCatalog.VolumeRef(mapping.Acp, mapping.Channel));
                var latch = mapping.UseLatch && mapping.Channel.HasRecLatch()
                    ? _prosim.Subscribe(AcpDataRefCatalog.LatchRef(mapping.Acp, mapping.Channel))
                    : null;
                var binding = new Binding(mapping, volume, latch, options.GainMinDb, options.GainMaxDb);
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
                _logger.LogInformation("Mixer mappings bound: {Count}", _bindings.Count);
            }
        }

        PublishRows();
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
        _coalescer.Offer(binding.GainParameter, binding.GainDb.Value);

        // No push-button (the loudspeaker): the dial is its own mute.
        if (binding.Mapping.UseLatch && binding.Latch is null)
        {
            var mute = MixerMappingMath.MuteFromDial(raw);
            binding.Muted = mute >= 0.5;
            _coalescer.Offer(binding.MuteParameter, mute);
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
        _coalescer.Offer(binding.MuteParameter, mute);
    }

    private void PublishRows()
    {
        MixerMappingStatus[] rows;
        lock (_gate)
        {
            rows = [.. _bindings.Select(b => new MixerMappingStatus(
                b.Mapping.Acp, b.Mapping.Channel, b.Mapping.IsBus, b.Mapping.StripIndex, b.GainDb, b.Muted, b.LastResult))];
        }

        _store.Update(s => s with { Mappings = rows });
    }

    private sealed class Binding(
        MixerMapping mapping,
        IDataRefSubscription<double> volume,
        IDataRefSubscription<int>? latch,
        double minDb,
        double maxDb) : IDisposable
    {
        public MixerMapping Mapping { get; } = mapping;

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
