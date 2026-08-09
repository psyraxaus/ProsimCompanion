using Microsoft.Extensions.Logging;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Backends.VoiceMeeter;

/// <summary>
/// Routes power-gated ACP knob/latch changes to VoiceMeeter strip/bus parameters.
/// VBVMR_SetParameterFloat is sub-ms, so writes happen synchronously on the dataref push
/// dispatcher thread — no per-mapping worker/coalescer needed (unlike the CoreAudio path).
/// Since issue #35 that dispatcher is decoupled from the SDK's receive thread, so a stalled
/// VBVMR call delays notifications instead of freezing every dataref cache in the app.
/// </summary>
public sealed class VoiceMeeterBinder : IAcpVolumeSink
{
    private readonly VoiceMeeterRemote _remote;
    private readonly AudioStatusStore _status;
    private readonly ILogger<VoiceMeeterBinder> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(AcpSide Acp, AudioChannel Channel), BoundTarget> _targets = [];

    public VoiceMeeterBinder(
        VoiceMeeterRemote remote,
        AudioStatusStore status,
        ILogger<VoiceMeeterBinder> logger)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);

        _remote = remote;
        _status = status;
        _logger = logger;
    }

    /// <summary>Replaces the bound mapping set (assumed already validated) and returns the
    /// (ACP, channel) keys the feed should subscribe.</summary>
    public IReadOnlyCollection<(AcpSide Acp, AudioChannel Channel)> Bind(
        IReadOnlyList<(AcpSide Acp, IReadOnlyList<VoiceMeeterTargetMapping> Mappings)> activeSets)
    {
        ArgumentNullException.ThrowIfNull(activeSets);

        lock (_gate)
        {
            _targets.Clear();
            foreach (var (acp, mappings) in activeSets)
            {
                foreach (var mapping in mappings)
                {
                    _targets[(acp, mapping.Channel)] = new BoundTarget(mapping);
                }
            }

            _logger.LogInformation("VoiceMeeter bound: {Count} targets across {Acps} ACP(s)",
                _targets.Count, activeSets.Count);
            PublishLocked();
            return [.. _targets.Keys];
        }
    }

    /// <summary>Returns every configured target to 0 dB unmuted so handing control back to the
    /// OS/VoiceMeeter chain never leaves a knob's last attenuation stuck in the signal path.
    /// Must run while the targets are still bound and writes still allowed — the predecessor
    /// called this after unbinding, which made its neutral reset dead code.</summary>
    public void ResetTargetsToNeutral()
    {
        lock (_gate)
        {
            foreach (var target in _targets.Values)
            {
                _remote.SetGainDb(target.Mapping.StripIndex, target.Mapping.IsBus, 0f);
                _remote.SetMute(target.Mapping.StripIndex, target.Mapping.IsBus, false);
            }

            if (_targets.Count > 0)
            {
                _logger.LogInformation("VoiceMeeter targets reset to 0 dB unmuted");
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _targets.Clear();
            PublishLocked();
        }
    }

    public void OnVolume(AcpSide acp, AudioChannel channel, float normalized)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue((acp, channel), out var target))
            {
                return;
            }

            var gainDb = VolumeMath.ToVoiceMeeterGainDb(normalized);
            _remote.SetGainDb(target.Mapping.StripIndex, target.Mapping.IsBus, gainDb);
            target.GainDb = gainDb;
            PublishLocked();
        }
    }

    public void OnMute(AcpSide acp, AudioChannel channel, bool muted)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue((acp, channel), out var target) || !target.Mapping.UseLatch)
            {
                return;
            }

            _remote.SetMute(target.Mapping.StripIndex, target.Mapping.IsBus, muted);
            target.Muted = muted;
            PublishLocked();
        }
    }

    private void PublishLocked()
    {
        var views = _targets
            .OrderBy(pair => pair.Key.Acp)
            .ThenBy(pair => pair.Key.Channel)
            .Select(pair => new VoiceMeeterBindingView(
                pair.Key.Acp,
                pair.Key.Channel,
                pair.Value.Mapping.StripIndex,
                pair.Value.Mapping.IsBus,
                pair.Value.GainDb,
                pair.Value.Muted))
            .ToList();
        _status.Update(s => s with { VoiceMeeterBindings = views });
    }

    private sealed class BoundTarget(VoiceMeeterTargetMapping mapping)
    {
        public VoiceMeeterTargetMapping Mapping { get; } = mapping;

        public float? GainDb { get; set; }

        public bool? Muted { get; set; }
    }
}
