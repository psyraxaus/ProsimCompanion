using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Backends.CoreAudio;

/// <summary>One discovered Windows audio session for a mapping.</summary>
public sealed record SessionHandle(AudioSessionControl Control, string InstanceId);

/// <summary>
/// Owns the WASAPI device list: throttled rescans, blacklist filtering, and per-mapping session
/// lookup. All calls happen on the backend's housekeeping thread (a thread-pool/MTA thread) —
/// the enumerator is also created there because a COM object binds to the apartment of its
/// constructing thread, and an STA-bound enumerator would marshal every session call through
/// the WPF dispatcher (predecessor archaeology 2026-05-03: this starved the volume worker
/// whenever the UI repainted).
/// </summary>
public sealed class CoreAudioDeviceRegistry : IDisposable
{
    private readonly ILogger<CoreAudioDeviceRegistry> _logger;
    private MMDeviceEnumerator? _enumerator;
    private readonly List<(string Name, MMDevice Device)> _devices = [];

    public CoreAudioDeviceRegistry(ILogger<CoreAudioDeviceRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Blacklist semantics: an entry suppresses every device whose friendly name
    /// STARTS WITH it (case-insensitive) — one entry covers "Speakers (Realtek…)" across
    /// driver-suffix variations.</summary>
    public static bool IsBlacklisted(string deviceName, IReadOnlyList<string> blacklist)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        ArgumentNullException.ThrowIfNull(blacklist);

        return blacklist.Any(entry => !string.IsNullOrWhiteSpace(entry)
            && deviceName.StartsWith(entry.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> DeviceNames
    {
        get
        {
            lock (_devices)
            {
                return [.. _devices.Select(d => d.Name)];
            }
        }
    }

    /// <summary>Re-enumerates active render devices. Returns true when the device set changed
    /// (new/removed devices ⇒ sessions must be re-searched).</summary>
    public bool Rescan(IReadOnlyList<string> blacklist)
    {
        ArgumentNullException.ThrowIfNull(blacklist);

        lock (_devices)
        {
            _enumerator ??= new MMDeviceEnumerator();

            var previous = _devices.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, device) in _devices)
            {
                device.Dispose();
            }

            _devices.Clear();

            try
            {
                foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    string name;
                    try
                    {
                        name = device.FriendlyName;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Audio device with unreadable name skipped");
                        device.Dispose();
                        continue;
                    }

                    if (IsBlacklisted(name, blacklist))
                    {
                        device.Dispose();
                        continue;
                    }

                    _devices.Add((name, device));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio device enumeration failed");
            }

            var current = _devices.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return !current.SetEquals(previous);
        }
    }

    /// <summary>Finds the Windows audio sessions a mapping controls right now. Matches on
    /// process id, with a session-instance-identifier "{binary}.exe" fallback (some hosts
    /// register sessions under a broker process id). Every per-session property read is
    /// individually guarded so one broken sibling session never hides the rest.</summary>
    public List<SessionHandle> FindSessions(AudioAppMapping mapping, IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(processIds);

        var result = new List<SessionHandle>();
        lock (_devices)
        {
            foreach (var (name, device) in _devices)
            {
                if (mapping.Device.Length > 0
                    && !string.Equals(name, mapping.Device, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            var instanceId = session.GetSessionInstanceIdentifier;
                            var matches = processIds.Contains((int)session.GetProcessID)
                                || instanceId.Contains($"{mapping.Binary}.exe", StringComparison.OrdinalIgnoreCase);
                            if (!matches)
                            {
                                continue;
                            }

                            if (mapping.OnlyActive && session.State != AudioSessionState.AudioSessionStateActive)
                            {
                                continue;
                            }

                            result.Add(new SessionHandle(session, instanceId));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Session probe failed on {Device}", name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Session enumeration failed on {Device}", name);
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_devices)
        {
            foreach (var (_, device) in _devices)
            {
                device.Dispose();
            }

            _devices.Clear();
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }
}
