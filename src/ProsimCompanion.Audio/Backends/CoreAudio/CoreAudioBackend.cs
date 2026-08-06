using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Backends.CoreAudio;

/// <summary>
/// Routes power-gated ACP knob/latch changes to Windows per-app session volumes, and runs the
/// housekeeping that keeps mappings bound: throttled process scans, throttled device rescans,
/// and session re-search when a mapped app starts, stops, moves devices, or goes inactive.
/// All housekeeping runs on the orchestrator's tick thread.
/// </summary>
public sealed class CoreAudioBackend : IAcpVolumeSink, IDisposable
{
    private const int InactiveCheckTtlMs = 5000;

    private readonly CoreAudioDeviceRegistry _devices;
    private readonly AudioStatusStore _status;
    private readonly ILogger<CoreAudioBackend> _logger;
    private readonly object _gate = new();

    private List<AppSessionBinding> _bindings = [];
    private AcpSide _acp = AcpSide.Captain;
    private DateTimeOffset _nextProcessCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDeviceScan = DateTimeOffset.MinValue;
    private DateTimeOffset _nextInactiveCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _sessionSearchHoldUntil = DateTimeOffset.MinValue;
    private bool _forceRescan;
    private bool _deviceScanDone;

    public CoreAudioBackend(
        CoreAudioDeviceRegistry devices,
        AudioStatusStore status,
        ILogger<CoreAudioBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);

        _devices = devices;
        _status = status;
        _logger = logger;
    }

    /// <summary>Replaces the mapping set and returns the (ACP, channel) keys the feed should
    /// subscribe — every mapping listens to the single configured ACP on this backend.</summary>
    public IReadOnlyCollection<(AcpSide Acp, AudioChannel Channel)> Bind(
        IReadOnlyList<AudioAppMapping> mappings, AcpSide acp)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        lock (_gate)
        {
            ReleaseLocked();
            _acp = acp;
            _bindings = [.. mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.Binary))
                .Select(m => new AppSessionBinding(m, _logger))];
            _nextProcessCheck = DateTimeOffset.MinValue;
            _forceRescan = true;
            _logger.LogInformation("CoreAudio bound: {Count} app mappings on {Acp}", _bindings.Count, acp);
            PublishLocked();
            return [.. _bindings.Select(b => (acp, b.Mapping.Channel)).Distinct()];
        }
    }

    /// <summary>Restores discovery-time session volumes and drops every binding (backend
    /// switch or shutdown).</summary>
    public void Release()
    {
        lock (_gate)
        {
            ReleaseLocked();
            PublishLocked();
        }
    }

    public void OnVolume(AcpSide acp, AudioChannel channel, float normalized)
    {
        // The feed only carries the bound ACP's keys, but re-check against races around rebind.
        List<AppSessionBinding> bindings;
        lock (_gate)
        {
            if (acp != _acp)
            {
                return;
            }

            bindings = _bindings;
        }

        foreach (var binding in bindings)
        {
            if (binding.Mapping.Channel == channel)
            {
                binding.QueueVolume(normalized);
            }
        }

        Publish();
    }

    public void OnMute(AcpSide acp, AudioChannel channel, bool muted)
    {
        List<AppSessionBinding> bindings;
        lock (_gate)
        {
            if (acp != _acp)
            {
                return;
            }

            bindings = _bindings;
        }

        foreach (var binding in bindings)
        {
            if (binding.Mapping.Channel == channel)
            {
                binding.QueueMute(muted);
            }
        }

        Publish();
    }

    /// <summary>One housekeeping step; the orchestrator calls this every run interval.</summary>
    public void Tick(DateTimeOffset now, AudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            if (_bindings.Count == 0)
            {
                return;
            }

            var processesChanged = false;
            if (now >= _nextProcessCheck)
            {
                _nextProcessCheck = now.AddMilliseconds(Math.Max(500, options.ProcessCheckIntervalMs));
                processesChanged = ScanProcessesLocked();
                if (processesChanged)
                {
                    // A just-started app needs a moment to create its audio session.
                    _sessionSearchHoldUntil = now.AddMilliseconds(Math.Max(0, options.ProcessStartupDelayMs));
                }
            }

            if (now >= _nextInactiveCheck)
            {
                _nextInactiveCheck = now.AddMilliseconds(InactiveCheckTtlMs);
                foreach (var binding in _bindings)
                {
                    if (binding.HasSessions && binding.HasInactiveSessions())
                    {
                        _forceRescan = true;
                    }
                }
            }

            var searchExhausted = _bindings.Any(b =>
                b.IsRunning && !b.IsElevated && !b.HasSessions
                && b.SearchCounter > options.ProcessMaxSearchCount);

            var scanDue = now >= _nextDeviceScan;
            if (!_deviceScanDone || scanDue || _forceRescan || searchExhausted)
            {
                _nextDeviceScan = now.AddMilliseconds(Math.Max(5000, options.DeviceCheckIntervalMs));
                var devicesChanged = _devices.Rescan(options.DeviceBlacklist);
                _deviceScanDone = true;
                if (searchExhausted)
                {
                    foreach (var binding in _bindings)
                    {
                        binding.SearchCounter = 0;
                    }
                }

                if (devicesChanged)
                {
                    _forceRescan = true;
                }
            }

            if (now >= _sessionSearchHoldUntil && (processesChanged || _forceRescan
                || _bindings.Any(b => b.IsRunning && !b.IsElevated && !b.HasSessions)))
            {
                _forceRescan = false;
                SearchSessionsLocked();
            }

            PublishLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseLocked();
        }
    }

    private bool ScanProcessesLocked()
    {
        var changed = false;
        foreach (var group in _bindings.GroupBy(b => b.Mapping.Binary, StringComparer.OrdinalIgnoreCase))
        {
            Process[] found;
            try
            {
                found = Process.GetProcessesByName(group.Key);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Process scan failed for {Binary}", group.Key);
                continue;
            }

            var first = true;
            foreach (var binding in group)
            {
                // Each binding disposes the handles it owns, so siblings sharing a binary
                // (same app on two devices) each get their own scan result.
                var own = first ? found : Process.GetProcessesByName(group.Key);
                first = false;
                changed |= binding.UpdateProcesses(own);
            }
        }

        return changed;
    }

    private void SearchSessionsLocked()
    {
        foreach (var binding in _bindings)
        {
            if (!binding.IsRunning || binding.IsElevated)
            {
                continue;
            }

            var sessions = _devices.FindSessions(binding.Mapping, binding.ProcessIds);
            if (sessions.Count > 0)
            {
                binding.SetSessions(sessions);
            }
            else if (!binding.HasSessions)
            {
                binding.SearchCounter++;
            }
        }
    }

    private void ReleaseLocked()
    {
        foreach (var binding in _bindings)
        {
            binding.RestoreAndClearSessions();
            binding.DisposeProcesses();
        }

        _bindings = [];
    }

    private void Publish()
    {
        lock (_gate)
        {
            PublishLocked();
        }
    }

    private void PublishLocked()
    {
        var views = _bindings.Select(b => b.ToView()).ToList();
        _status.Update(s => s with { Mappings = views });
    }
}
