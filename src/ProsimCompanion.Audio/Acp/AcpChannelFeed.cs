using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Acp;

/// <summary>Receives power-gated knob/latch changes for the bound (ACP, channel) keys.
/// Callbacks arrive on the ProSim SDK's push thread — implementations must not block.</summary>
public interface IAcpVolumeSink
{
    void OnVolume(AcpSide acp, AudioChannel channel, float normalized);

    /// <summary>true = the REC latch is pushed in (muted).</summary>
    void OnMute(AcpSide acp, AudioChannel channel, bool muted);
}

/// <summary>
/// The single ACP source model both backends consume: subscribes the knob analog and REC latch
/// for each bound (ACP, channel) key at the 250 ms tier (the predecessors' proven cadence) and
/// fans power-gated changes out to the active backend. While an ACP is unpowered its events are
/// suppressed (targets hold their last state); on power restoration the current values are
/// re-emitted so targets catch up without waiting for a knob touch.
/// </summary>
public sealed class AcpChannelFeed : IDisposable
{
    private readonly IProsimDataRefs _prosim;
    private readonly ILogger<AcpChannelFeed> _logger;
    private readonly object _gate = new();

    private readonly IDataRefSubscription _acEss;
    private readonly IDataRefSubscription _dcEss;
    private readonly IDataRefSubscription _dc1;
    private readonly IDataRefSubscription _audioSwitching;

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

        _acEss = prosim.Subscribe(ProsimDataRefNames.ElecBusPowerAcEss, DataRefTier.Frequent);
        _dcEss = prosim.Subscribe(ProsimDataRefNames.ElecBusPowerDcEss, DataRefTier.Frequent);
        _dc1 = prosim.Subscribe(ProsimDataRefNames.ElecBusPowerDc1, DataRefTier.Frequent);
        _audioSwitching = prosim.Subscribe(ProsimDataRefNames.AudioSwitching, DataRefTier.Frequent);

        _acEss.ValueChanged += OnPowerRefChanged;
        _dcEss.ValueChanged += OnPowerRefChanged;
        _dc1.ValueChanged += OnPowerRefChanged;
        _audioSwitching.ValueChanged += OnPowerRefChanged;
    }

    public AcpPowerInputs PowerInputs => new(
        AcEss: _acEss.GetValue(0) != 0,
        DcEss: _dcEss.GetValue(0) != 0,
        Dc1: _dc1.GetValue(0) != 0,
        // NORM fallback: an unreadable switch must never gate out ACP1/ACP2.
        AudioSwitching: _audioSwitching.GetValue(1));

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
                var binding = new ChannelBinding(
                    acp,
                    channel,
                    _prosim.Subscribe(AcpDataRefCatalog.VolumeRef(acp, channel), DataRefTier.Frequent),
                    _prosim.Subscribe(AcpDataRefCatalog.LatchRef(acp, channel), DataRefTier.Frequent));
                binding.Volume.ValueChanged += (_, _) => EmitVolume(binding);
                binding.Latch.ValueChanged += (_, _) => EmitMute(binding);
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
            binding.Latch.Dispose();
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

            // Re-emit on any closed→open ACP transition so targets catch up.
            foreach (var acp in new[] { AcpSide.Captain, AcpSide.FirstOfficer, AcpSide.Observer })
            {
                if (!AcpPowerGate.IsPowered(acp, previous) && AcpPowerGate.IsPowered(acp, inputs))
                {
                    foreach (var binding in _bindings)
                    {
                        if (binding.Acp == acp)
                        {
                            EmitVolumeLocked(binding);
                            EmitMuteLocked(binding);
                        }
                    }
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
        if (_sink is null || binding.Volume.RawValue is null || !IsPowered(binding.Acp))
        {
            return;
        }

        _sink.OnVolume(binding.Acp, binding.Channel, VolumeMath.Normalize(binding.Volume.GetValue(0.0)));
    }

    private void EmitMuteLocked(ChannelBinding binding)
    {
        if (_sink is null || binding.Latch.RawValue is null || !IsPowered(binding.Acp))
        {
            return;
        }

        // Latch: 0 = muted, 1 = unmuted.
        _sink.OnMute(binding.Acp, binding.Channel, binding.Latch.GetValue(1) == 0);
    }

    private sealed record ChannelBinding(
        AcpSide Acp,
        AudioChannel Channel,
        IDataRefSubscription Volume,
        IDataRefSubscription Latch);
}
