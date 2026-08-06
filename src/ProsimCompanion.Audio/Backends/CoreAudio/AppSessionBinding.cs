using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Backends.CoreAudio;

/// <summary>
/// Runtime state of one CoreAudio app mapping: the mapped process(es), the Windows audio
/// sessions under control, and a write coalescer. ProSim SDK callbacks arrive on the shared
/// push thread while a CoreAudio volume write can take tens of milliseconds (seconds when an
/// elevated process holds a session on the same device) — so knob callbacks only store the
/// latest pending value and a single drain worker per mapping performs the writes. Rapid knob
/// movement deliberately collapses to the final position.
/// </summary>
public sealed class AppSessionBinding
{
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();

    /// <summary>Serializes the drain worker's COM writes against restore-on-release, so a
    /// knob event racing a backend switch/shutdown cannot overwrite the restored volumes.</summary>
    private readonly object _comGate = new();

    private List<Process> _processes = [];
    private List<SessionHandle> _sessions = [];
    private readonly Dictionary<string, (float Volume, bool Mute)> _saved = [];

    private float? _pendingVolume;
    private bool? _pendingMute;
    private bool _writerActive;
    private float? _currentVolume;
    private bool? _currentMute;
    private bool _elevated;
    private bool _elevatedWarned;

    public AppSessionBinding(AudioAppMapping mapping, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(logger);

        Mapping = mapping;
        _logger = logger;
    }

    public AudioAppMapping Mapping { get; }

    /// <summary>Consecutive ticks where the process ran but no session matched; past the
    /// configured threshold the backend forces a device rescan.</summary>
    public int SearchCounter { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _processes.Count > 0;
            }
        }
    }

    public bool IsElevated
    {
        get
        {
            lock (_stateLock)
            {
                return _elevated;
            }
        }
    }

    public bool HasSessions
    {
        get
        {
            lock (_stateLock)
            {
                return _sessions.Count > 0;
            }
        }
    }

    public IReadOnlyCollection<int> ProcessIds
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _processes.Select(p => p.Id)];
            }
        }
    }

    /// <summary>Replaces the tracked processes with a fresh scan result. The previous tick's
    /// Process handles are explicitly disposed — the predecessor leaked hundreds of handles
    /// per tick before this, which degraded the system audio stack within minutes. Returns
    /// true when the process set changed (sessions must be re-searched).</summary>
    public bool UpdateProcesses(Process[] found)
    {
        ArgumentNullException.ThrowIfNull(found);

        lock (_stateLock)
        {
            var previousIds = _processes.Select(p => p.Id).ToHashSet();
            foreach (var process in _processes)
            {
                process.Dispose();
            }

            _processes = [.. found];

            var elevated = _processes.Count > 0 && _processes.All(p => !IsAccessible(p));
            if (elevated && !_elevated)
            {
                if (!_elevatedWarned)
                {
                    _elevatedWarned = true;
                    _logger.LogWarning(
                        "{Binary} runs at higher integrity than ProsimCompanion — its audio sessions "
                        + "are invisible; run ProsimCompanion as administrator to control it", Mapping.Binary);
                }
            }

            _elevated = elevated;

            var currentIds = _processes.Select(p => p.Id).ToHashSet();
            if (currentIds.SetEquals(previousIds))
            {
                return false;
            }

            if (_processes.Count == 0)
            {
                // Process gone: sessions die with it — drop them without a restore attempt
                // (the COM objects are already dead) and reset the elevation latch.
                _sessions = [];
                _saved.Clear();
                _elevated = false;
                _elevatedWarned = false;
                _currentVolume = null;
                _currentMute = null;
                SearchCounter = 0;
            }

            return true;
        }
    }

    /// <summary>Adopts a fresh session lookup: snapshots each new session's volume/mute for
    /// restore-on-release, then pushes the current knob state so the app matches the cockpit
    /// immediately.</summary>
    public void SetSessions(List<SessionHandle> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        lock (_stateLock)
        {
            foreach (var session in sessions)
            {
                if (!_saved.ContainsKey(session.InstanceId))
                {
                    try
                    {
                        _saved[session.InstanceId] =
                            (session.Control.SimpleAudioVolume.Volume, session.Control.SimpleAudioVolume.Mute);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Snapshotting session volume failed for {Binary}", Mapping.Binary);
                    }
                }
            }

            _sessions = sessions;
            SearchCounter = 0;
        }

        if (sessions.Count > 0)
        {
            lock (_writeLock)
            {
                _pendingVolume ??= _currentVolume;
                if (Mapping.UseLatch)
                {
                    _pendingMute ??= _currentMute;
                }
            }

            KickWriter();
        }
    }

    public void QueueVolume(float normalized)
    {
        lock (_writeLock)
        {
            _currentVolume = normalized;
            _pendingVolume = normalized;
        }

        KickWriter();
    }

    public void QueueMute(bool muted)
    {
        if (!Mapping.UseLatch)
        {
            // UseLatch off means mute is NEVER written — a session muted elsewhere stays muted.
            return;
        }

        lock (_writeLock)
        {
            _currentMute = muted;
            _pendingMute = muted;
        }

        KickWriter();
    }

    /// <summary>Writes the discovery-time volumes back and releases the sessions (backend
    /// switch or shutdown). GSX caveat carried from the predecessor: Couatl resets its own
    /// sessions concurrently, so its restore can lose the race.</summary>
    public void RestoreAndClearSessions()
    {
        lock (_comGate)
        {
            // Discard queued knob values first — nothing may write after the restore.
            lock (_writeLock)
            {
                _pendingVolume = null;
                _pendingMute = null;
            }

            RestoreUnderComGate();
        }
    }

    private void RestoreUnderComGate()
    {
        lock (_stateLock)
        {
            foreach (var session in _sessions)
            {
                if (!_saved.TryGetValue(session.InstanceId, out var saved))
                {
                    continue;
                }

                try
                {
                    session.Control.SimpleAudioVolume.Volume = saved.Volume;
                    session.Control.SimpleAudioVolume.Mute = saved.Mute;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Restoring session volume failed for {Binary}", Mapping.Binary);
                }
            }

            _sessions = [];
            _saved.Clear();
        }
    }

    public void DisposeProcesses()
    {
        lock (_stateLock)
        {
            foreach (var process in _processes)
            {
                process.Dispose();
            }

            _processes = [];
        }
    }

    /// <summary>True when any controlled session left the Active state (an OnlyActive mapping
    /// then needs a re-search). Costs one COM call per session — the backend throttles it.</summary>
    public bool HasInactiveSessions()
    {
        lock (_stateLock)
        {
            if (!Mapping.OnlyActive)
            {
                return false;
            }

            foreach (var session in _sessions)
            {
                try
                {
                    if (session.Control.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    return true; // A dead session counts as inactive — re-search.
                }
            }

            return false;
        }
    }

    public AudioMappingView ToView()
    {
        lock (_stateLock)
        {
            var state = _elevated ? AudioMappingState.Elevated
                : _processes.Count == 0 ? AudioMappingState.NotRunning
                : _sessions.Count == 0 ? AudioMappingState.Searching
                : AudioMappingState.Bound;
            return new AudioMappingView(
                Mapping.Binary,
                Mapping.Channel,
                Mapping.Device,
                state,
                _sessions.Count,
                _currentVolume,
                Mapping.UseLatch ? _currentMute : null);
        }
    }

    /// <summary>The process is elevated when its MainModule is unreadable: the same integrity
    /// barrier that blocks MainModule also hides the process's audio sessions from unelevated
    /// CoreAudio enumeration, so the probe is a reliable proxy.</summary>
    private static bool IsAccessible(Process process)
    {
        try
        {
            _ = process.MainModule?.FileName;
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void KickWriter()
    {
        lock (_writeLock)
        {
            if (_writerActive || (_pendingVolume is null && _pendingMute is null))
            {
                return;
            }

            _writerActive = true;
        }

        _ = Task.Run(DrainAsync);
    }

    private Task DrainAsync()
    {
        while (true)
        {
            float? volume;
            bool? mute;
            lock (_writeLock)
            {
                volume = _pendingVolume;
                mute = _pendingMute;
                _pendingVolume = null;
                _pendingMute = null;
                if (volume is null && mute is null)
                {
                    _writerActive = false;
                    return Task.CompletedTask;
                }
            }

            // The session list is re-read INSIDE the COM gate: if a restore-on-release ran
            // while this drain was pending, the list is already empty here and the stale
            // knob value is never written over the restored volumes.
            lock (_comGate)
            {
                List<SessionHandle> sessions;
                lock (_stateLock)
                {
                    sessions = _sessions;
                }

                foreach (var session in sessions)
                {
                    try
                    {
                        if (volume is { } v)
                        {
                            session.Control.SimpleAudioVolume.Volume = v;
                        }

                        if (mute is { } m && Mapping.UseLatch)
                        {
                            session.Control.SimpleAudioVolume.Mute = m;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Session write failed for {Binary} — will re-search", Mapping.Binary);
                        lock (_stateLock)
                        {
                            _sessions = [];
                        }

                        break;
                    }
                }
            }
        }
    }
}
