using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Acp;

/// <summary>Receives power-gated knob/latch changes for the bound (ACP, channel) keys.
/// Callbacks arrive on the ProSim SDK's push thread — implementations must not block.</summary>
public interface IAcpVolumeSink
{
    void OnVolume(AcpSide acp, AudioChannel channel, float normalized);

    /// <summary>true = the REC latch is pushed in (muted) — or, for a dial with no latch
    /// (the loudspeaker), the dial is fully down.</summary>
    void OnMute(AcpSide acp, AudioChannel channel, bool muted);
}

/// <summary>
/// The single ACP source model both backends consume: subscribes the knob analog and REC latch
/// for each bound (ACP, channel) key at the 250 ms tier (the predecessors' proven cadence) and
/// fans power-gated changes out to the active backend. While an ACP is unpowered its events are
/// suppressed (targets hold their last state); on power restoration the current values are
/// re-emitted so targets catch up without waiting for a knob touch. The loudspeaker dial rides
/// the same feed: no latch (its mute is the dial at zero) and its own power rule
/// (<see cref="AcpPowerGate.IsPowered(AcpSide, AudioChannel, in AcpPowerInputs)"/>).
/// </summary>
public sealed class AcpChannelFeed : IDisposable
{
    private readonly IProsimDataRefs _prosim;
    private readonly ILogger<AcpChannelFeed> _logger;
    private readonly object _gate = new();

    private readonly IDataRefSubscription<bool> _acEss;
    private readonly IDataRefSubscription<bool> _dcEss;
    private readonly IDataRefSubscription<bool> _dc1;
    private readonly IDataRefSubscription<int> _audioSwitching;

    private readonly List<ChannelBinding> _bindings = [];
    private IAcpVolumeSink? _sink;
    private AcpPowerInputs _lastPower = AcpPowerInputs.Unknown;

    /// <summary>Raised (on the SDK thread) whenever any ACP's powered state may have changed.</summary>
    public event EventHandler? PowerChanged;

    public AcpChannelFeed(IProsimDataRefs prosim, ILogger<AcpChannelFeed> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _logger = logger;

        _acEss = prosim.Subscribe(ProsimDataRefNames.ElecBusPowerAcEss);
        _dcEss = prosim.Subscribe(ProsimDataRefNames.ElecBusPowerDcEss);
        _dc1 = prosim.Subscribe(ProsimDataRefNames.ElecBusPowerDc1);
        _audioSwitching = prosim.Subscribe(ProsimDataRefNames.AudioSwitching);

        _acEss.ValueChanged += OnPowerRefChanged;
        _dcEss.ValueChanged += OnPowerRefChanged;
        _dc1.ValueChanged += OnPowerRefChanged;
        _audioSwitching.ValueChanged += OnPowerRefChanged;
    }

    public AcpPowerInputs PowerInputs => new(
        AcEss: _acEss.Value,
        DcEss: _dcEss.Value,
        Dc1: _dc1.Value,
        // NORM fallback (in the catalog descriptor): an unreadable switch must never gate out ACP1/ACP2.
        AudioSwitching: _audioSwitching.Value);

    public bool IsPowered(AcpSide acp) => AcpPowerGate.IsPowered(acp, PowerInputs);

    /// <summary>Replaces the bound key set and sink, then seeds the sink with current values
    /// for every powered ACP (an unpowered ACP contributes nothing until power returns).</summary>
    public void Bind(IReadOnlyCollection<(AcpSide Acp, AudioChannel Channel)> keys, IAcpVolumeSink sink)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            UnbindLocked();
            _sink = sink;
            foreach (var (acp, channel) in keys.Distinct())
            {
                if (!channel.ExistsOn(acp))
                {
                    // Only reachable from a hand-edited config — the settings page never
                    // offers the key.
                    _logger.LogWarning("ACP feed: {Acp} has no {Channel} dial — mapping skipped", acp, channel);
                    continue;
                }

                var binding = new ChannelBinding(
                    acp,
                    channel,
                    _prosim.Subscribe(AcpDataRefCatalog.VolumeRef(acp, channel)),
                    channel.HasRecLatch()
                        ? _prosim.Subscribe(AcpDataRefCatalog.LatchRef(acp, channel))
                        : null);
                binding.Volume.ValueChanged += (_, _) => EmitVolume(binding);
                if (binding.Latch is { } latch)
                {
                    latch.ValueChanged += (_, _) => EmitMute(binding);
                }

                _bindings.Add(binding);
            }

            _logger.LogInformation("ACP feed bound: {Count} channel keys", _bindings.Count);
            EmitCurrentLocked();
        }
    }

    public void Unbind()
    {
        lock (_gate)
        {
            UnbindLocked();
        }
    }

    public void Dispose()
    {
        Unbind();
        _acEss.ValueChanged -= OnPowerRefChanged;
        _dcEss.ValueChanged -= OnPowerRefChanged;
        _dc1.ValueChanged -= OnPowerRefChanged;
        _audioSwitching.ValueChanged -= OnPowerRefChanged;
        _acEss.Dispose();
        _dcEss.Dispose();
        _dc1.Dispose();
        _audioSwitching.Dispose();
    }

    private void UnbindLocked()
    {
        foreach (var binding in _bindings)
        {
            binding.Volume.Dispose();
            binding.Latch?.Dispose();
        }

        _bindings.Clear();
        _sink = null;
    }

    private void OnPowerRefChanged(object? sender, EventArgs e)
    {
        var inputs = PowerInputs;
        lock (_gate)
        {
            var previous = _lastPower;
            _lastPower = inputs;

            // Re-emit on any closed→open transition so targets catch up. Per binding, not
            // per ACP: the loudspeaker dial has its own power rule.
            foreach (var binding in _bindings)
            {
                if (!AcpPowerGate.IsPowered(binding.Acp, binding.Channel, previous)
                    && AcpPowerGate.IsPowered(binding.Acp, binding.Channel, inputs))
                {
                    EmitVolumeLocked(binding);
                    EmitMuteLocked(binding);
                }
            }
        }

        PowerChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EmitCurrentLocked()
    {
        foreach (var binding in _bindings)
        {
            EmitVolumeLocked(binding);
            EmitMuteLocked(binding);
        }
    }

    private void EmitVolume(ChannelBinding binding)
    {
        lock (_gate)
        {
            EmitVolumeLocked(binding);
            if (binding.Latch is null)
            {
                // The dial is its own mute; only the crossing is worth a write.
                EmitMuteLocked(binding, onlyOnChange: true);
            }
        }
    }

    private void EmitMute(ChannelBinding binding)
    {
        lock (_gate)
        {
            EmitMuteLocked(binding);
        }
    }

    private void EmitVolumeLocked(ChannelBinding binding)
    {
        // A knob value that never arrived stays unknown — emit nothing rather than 0
        // (a spurious zero would silence the target app at startup).
        if (_sink is null || binding.Volume.RawValue is null || !IsPowered(binding))
        {
            return;
        }

        _sink.OnVolume(binding.Acp, binding.Channel, VolumeMath.Normalize(binding.Volume.Value));
    }

    private void EmitMuteLocked(ChannelBinding binding, bool onlyOnChange = false)
    {
        if (_sink is null || !IsPowered(binding))
        {
            return;
        }

        if (binding.Latch is null)
        {
            // No latch: fully down is the mute (−60 dB on a VoiceMeeter target is still
            // audible). Same unknown-stays-unknown rule as the knob value.
            if (binding.Volume.RawValue is null)
            {
                return;
            }

            var dialMuted = VolumeMath.IsDialAtZero(binding.Volume.Value);
            if (onlyOnChange && binding.DialMuted == dialMuted)
            {
                return;
            }

            if (binding.DialMuted != dialMuted)
            {
                // The one line that shows in a flight log that the dial reached the target.
                _logger.LogInformation("{Acp} {Channel} dial {State}", binding.Acp, binding.Channel,
                    dialMuted ? "fully down — target muted" : "raised — target unmuted");
            }

            binding.DialMuted = dialMuted;
            _sink.OnMute(binding.Acp, binding.Channel, dialMuted);
            return;
        }

        if (binding.Latch.RawValue is null)
        {
            return;
        }

        // Latch: 0 = muted, 1 = unmuted (fallback 1 = fail-audible, guarded by RawValue above).
        _sink.OnMute(binding.Acp, binding.Channel, binding.Latch.Value == 0);
    }

    private bool IsPowered(ChannelBinding binding) =>
        AcpPowerGate.IsPowered(binding.Acp, binding.Channel, PowerInputs);

    private sealed class ChannelBinding(
        AcpSide acp,
        AudioChannel channel,
        IDataRefSubscription<double> volume,
        IDataRefSubscription<int>? latch)
    {
        public AcpSide Acp { get; } = acp;

        public AudioChannel Channel { get; } = channel;

        public IDataRefSubscription<double> Volume { get; } = volume;

        /// <summary>Null for a dial with no REC latch (the loudspeaker).</summary>
        public IDataRefSubscription<int>? Latch { get; } = latch;

        /// <summary>Last dial-at-zero mute emitted for a latchless dial; null before the first.</summary>
        public bool? DialMuted { get; set; }
    }
}
