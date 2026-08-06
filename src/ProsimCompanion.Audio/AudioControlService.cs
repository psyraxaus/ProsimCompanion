using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Audio.Backends.CoreAudio;
using ProsimCompanion.Audio.Backends.VoiceMeeter;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio;

/// <summary>
/// The audio pillar's orchestrator: binds the ACP channel feed to the configured backend,
/// switches backends live (settings hot-reload — CoreAudio session volumes are restored,
/// VoiceMeeter targets reset to 0 dB before control is handed over), and drives the CoreAudio
/// housekeeping tick. The two backends are mutually exclusive: in VoiceMeeter mode no CoreAudio
/// session is touched at all, and vice versa.
/// </summary>
public sealed class AudioControlService : IAudioControl, IDisposable
{
    private readonly AcpChannelFeed _feed;
    private readonly CoreAudioBackend _coreAudio;
    private readonly VoiceMeeterBinder _voiceMeeterBinder;
    private readonly VoiceMeeterRemote _voiceMeeter;
    private readonly AudioStatusStore _status;
    private readonly IOptionsMonitor<AudioOptions> _options;
    private readonly ILogger<AudioControlService> _logger;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private AudioBackend? _boundBackend;
    private string _boundFingerprint = "";
    private int _ticking;
    private bool _disposed;

    public AudioControlService(
        AcpChannelFeed feed,
        CoreAudioBackend coreAudio,
        VoiceMeeterBinder voiceMeeterBinder,
        VoiceMeeterRemote voiceMeeter,
        AudioStatusStore status,
        IOptionsMonitor<AudioOptions> options,
        ILogger<AudioControlService> logger)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(coreAudio);
        ArgumentNullException.ThrowIfNull(voiceMeeterBinder);
        ArgumentNullException.ThrowIfNull(voiceMeeter);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _feed = feed;
        _coreAudio = coreAudio;
        _voiceMeeterBinder = voiceMeeterBinder;
        _voiceMeeter = voiceMeeter;
        _status = status;
        _options = options;
        _logger = logger;

        _feed.PowerChanged += OnPowerChanged;

        var interval = Math.Clamp(options.CurrentValue.RunIntervalMs, 250, 60_000);
        _timer = new Timer(_ => Tick(), null, dueTime: 2000, period: interval);
    }

    /// <summary>One evaluation step — exposed for tests; the timer calls this every interval.</summary>
    public void ProcessTick(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!options.Enabled)
            {
                if (_boundBackend is not null)
                {
                    TeardownLocked();
                }

                PublishOverallLocked(options, voiceMeeterAvailable: _voiceMeeter.IsAvailable);
                return;
            }

            var fingerprint = Fingerprint(options);
            if (_boundBackend != options.Backend || fingerprint != _boundFingerprint)
            {
                TeardownLocked();
                BindLocked(options);
                _boundFingerprint = fingerprint;
            }
            else if (_boundBackend is null && options.Backend == AudioBackend.VoiceMeeter)
            {
                // VoiceMeeter selected but not yet available — retry the login each tick.
                BindLocked(options);
            }

            if (_boundBackend == AudioBackend.CoreAudio)
            {
                _coreAudio.Tick(nowUtc, options);
            }

            PublishOverallLocked(options, _voiceMeeter.IsAvailable);
        }
    }

    /// <summary>Hands every controlled target back cleanly (host shutdown).</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            TeardownLocked();
            _voiceMeeter.Logout();
        }
    }

    public IReadOnlyList<VoiceMeeterTargetView> GetVoiceMeeterTargets()
    {
        var options = _options.CurrentValue;
        if (!_voiceMeeter.IsLoaded && !_voiceMeeter.Login(options.VoiceMeeterDllPath))
        {
            return [];
        }

        var (strips, buses) = _voiceMeeter.GetCounts();
        var result = new List<VoiceMeeterTargetView>(strips + buses);
        for (var i = 0; i < strips; i++)
        {
            result.Add(new VoiceMeeterTargetView(i, IsBus: false, _voiceMeeter.GetLabel(i, isBus: false) ?? ""));
        }

        for (var i = 0; i < buses; i++)
        {
            result.Add(new VoiceMeeterTargetView(i, IsBus: true, _voiceMeeter.GetLabel(i, isBus: true) ?? ""));
        }

        return result;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _feed.PowerChanged -= OnPowerChanged;
        lock (_gate)
        {
            if (!_disposed)
            {
                TeardownLocked();
                _voiceMeeter.Logout();
                _disposed = true;
            }
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void BindLocked(AudioOptions options)
    {
        if (options.Backend == AudioBackend.CoreAudio)
        {
            var keys = _coreAudio.Bind(options.AppMappings, options.CoreAudioAcp);
            _feed.Bind(keys, _coreAudio);
            _boundBackend = AudioBackend.CoreAudio;
            _status.Update(s => s with { VoiceMeeterFallbackReason = "" });
            return;
        }

        if (!_voiceMeeter.Login(options.VoiceMeeterDllPath))
        {
            _boundBackend = null; // retried next tick
            return;
        }

        var activeSets = ResolveVoiceMeeterSets(options, out var fallbackReason);
        _status.Update(s => s with { VoiceMeeterFallbackReason = fallbackReason });
        var vmKeys = _voiceMeeterBinder.Bind(activeSets);
        _feed.Bind(vmKeys, _voiceMeeterBinder);
        _boundBackend = AudioBackend.VoiceMeeter;
    }

    private void TeardownLocked()
    {
        _feed.Unbind();
        switch (_boundBackend)
        {
            case AudioBackend.CoreAudio:
                _coreAudio.Release();
                break;
            case AudioBackend.VoiceMeeter:
                // Neutral reset MUST precede Clear() — targets at a knob's last −20 dB would
                // otherwise stay attenuated in the OS chain forever. Suspend, never Logout,
                // on a switch: repeated VBVMR_Login is flaky on some VoiceMeeter versions.
                _voiceMeeterBinder.ResetTargetsToNeutral();
                _voiceMeeterBinder.Clear();
                _voiceMeeter.SuspendWrites();
                break;
        }

        _boundBackend = null;
        _boundFingerprint = "";
    }

    private List<(AcpSide Acp, IReadOnlyList<VoiceMeeterTargetMapping> Mappings)> ResolveVoiceMeeterSets(
        AudioOptions options, out string fallbackReason)
    {
        var sets = new List<(AcpSide, IReadOnlyList<VoiceMeeterTargetMapping>)>();
        foreach (var acp in options.ActiveAcps.Distinct())
        {
            sets.Add((acp, MappingsFor(options, acp)));
        }

        var reason = VoiceMeeterMappingValidator.Validate(sets);
        if (reason is null)
        {
            fallbackReason = "";
            return sets;
        }

        // Conflicts fall back to Captain-only for the session; the config is never modified.
        _logger.LogWarning("VoiceMeeter mapping conflict ({Reason}) — falling back to Captain only", reason);
        var captainOnly = new List<(AcpSide, IReadOnlyList<VoiceMeeterTargetMapping>)>
        {
            (AcpSide.Captain, MappingsFor(options, AcpSide.Captain)),
        };
        if (VoiceMeeterMappingValidator.Validate(captainOnly) is not null)
        {
            captainOnly.Clear(); // Even Captain alone conflicts — bind nothing.
        }

        fallbackReason = $"{reason} — using Captain mappings only until fixed";
        return captainOnly;
    }

    private static List<VoiceMeeterTargetMapping> MappingsFor(AudioOptions options, AcpSide acp)
    {
        foreach (var (key, mappings) in options.VoiceMeeterMappings)
        {
            if (Enum.TryParse<AcpSide>(key, ignoreCase: true, out var parsed) && parsed == acp)
            {
                return mappings;
            }
        }

        return [];
    }

    private void OnPowerChanged(object? sender, EventArgs e) => PublishPower();

    private void PublishPower()
    {
        var powered = new Dictionary<AcpSide, bool>
        {
            [AcpSide.Captain] = _feed.IsPowered(AcpSide.Captain),
            [AcpSide.FirstOfficer] = _feed.IsPowered(AcpSide.FirstOfficer),
            [AcpSide.Observer] = _feed.IsPowered(AcpSide.Observer),
        };
        _status.Update(s => s with { AcpPowered = powered });
    }

    private void PublishOverallLocked(AudioOptions options, bool voiceMeeterAvailable)
    {
        var powered = new Dictionary<AcpSide, bool>
        {
            [AcpSide.Captain] = _feed.IsPowered(AcpSide.Captain),
            [AcpSide.FirstOfficer] = _feed.IsPowered(AcpSide.FirstOfficer),
            [AcpSide.Observer] = _feed.IsPowered(AcpSide.Observer),
        };
        _status.Update(s => s with
        {
            Enabled = options.Enabled,
            ActiveBackend = _boundBackend ?? options.Backend,
            VoiceMeeterAvailable = voiceMeeterAvailable,
            AcpPowered = powered,
        });
    }

    /// <summary>Change detection for live rebinds: the full options object, serialized. Cheap
    /// at 1 Hz and immune to forgetting a field in a hand-rolled comparison.</summary>
    private static string Fingerprint(AudioOptions options) => JsonSerializer.Serialize(options);
}
