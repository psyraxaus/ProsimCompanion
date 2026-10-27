using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Skalarki;

/// <summary>
/// The pure transitions behind <see cref="SkalarkiStatusStore"/>: link up/down, board presence,
/// input events, the 5 s event rate and the reconnect backoff — no timers, no SDK, so a fake
/// client drives it in tests. Every mutator returns the new snapshot (and keeps it as
/// <see cref="Snapshot"/>); a call that changes nothing returns the same instance, which is
/// what lets the store skip the notification.
/// </summary>
public sealed class SkalarkiStateCore
{
    /// <summary>The window the event rate is measured over.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);

    /// <summary>Longest pause between connection attempts, whatever the configured base.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly Dictionary<SkalarkiDeviceId, SkalarkiDeviceView> _devices = [];
    private readonly Queue<DateTimeOffset> _eventTimes = new();
    private IReadOnlyList<SkalarkiDeviceId> _enabled = [];
    private int _attempt;

    public SkalarkiSnapshot Snapshot { get; private set; } = SkalarkiSnapshot.Empty;

    /// <summary>Consecutive failed connection attempts since the last successful link.</summary>
    public int Attempt => _attempt;

    /// <summary>Applies the user's board list. Empty = watch every board the Profiler reports
    /// connected (sticky: a board that was connected once stays on the page as LOST when it
    /// drops, instead of vanishing). A named board is on the page even before the Profiler has
    /// mentioned it, so a wrong name shows as amber XX rather than nothing.</summary>
    public SkalarkiSnapshot Configure(IReadOnlyList<SkalarkiDeviceId> enabledDevices)
    {
        ArgumentNullException.ThrowIfNull(enabledDevices);
        _enabled = enabledDevices;
        foreach (var id in enabledDevices)
        {
            _devices.TryAdd(id, SkalarkiDeviceView.Unknown);
        }

        foreach (var id in _devices.Keys.ToList())
        {
            var view = _devices[id];
            var enabled = enabledDevices.Count == 0 ? view.Enabled || view.Connected : enabledDevices.Contains(id);
            _devices[id] = view with { Enabled = enabled };
        }

        return Publish();
    }

    public SkalarkiSnapshot SetSdkVersion(string? version) => Publish(Snapshot with { SdkVersion = version });

    public SkalarkiSnapshot SetLicense(string? license) => Publish(Snapshot with { License = license });

    /// <summary>The feature is off or cannot start; the reason is what the page prints.</summary>
    public SkalarkiSnapshot Disabled(string? reason)
    {
        DropPresence(null);
        _attempt = 0;
        return Publish(Snapshot with { LinkState = ConnectionState.Disabled, LinkSinceUtc = null, Reason = reason, EventsPerSecond = 0 });
    }

    public SkalarkiSnapshot Connecting(string? reason = null)
        => Publish(Snapshot with { LinkState = ConnectionState.Connecting, Reason = reason });

    public SkalarkiSnapshot LinkUp(DateTimeOffset now)
    {
        _attempt = 0;
        return Publish(Snapshot with { LinkState = ConnectionState.Connected, LinkSinceUtc = now, Reason = null });
    }

    /// <summary>The link dropped (or an attempt failed). Every board the Profiler had reported
    /// connected is now unknown, which the page shows as LOST from this instant.</summary>
    public SkalarkiSnapshot LinkDown(DateTimeOffset now, string? reason)
    {
        DropPresence(now);
        _eventTimes.Clear();
        return Publish(Snapshot with { LinkState = ConnectionState.Disconnected, LinkSinceUtc = null, Reason = reason, EventsPerSecond = 0 });
    }

    /// <summary>The next pause before reconnecting: the base doubled per failed attempt, capped.</summary>
    public TimeSpan NextBackoff(TimeSpan baseBackoff)
    {
        _attempt++;
        var factor = Math.Pow(2, Math.Min(_attempt - 1, 10));
        var pause = TimeSpan.FromMilliseconds(baseBackoff.TotalMilliseconds * factor);
        return pause > MaxBackoff ? MaxBackoff : pause;
    }

    /// <summary>A board's presence as the Profiler reports it. The first report of a board
    /// sets no change time (a board that was never there is not "lost"); a later flip does.</summary>
    public SkalarkiSnapshot DeviceChanged(SkalarkiDeviceId device, bool connected, DateTimeOffset now)
    {
        var known = _devices.TryGetValue(device, out var view);
        var previous = view ?? SkalarkiDeviceView.Unknown;
        var enabled = WantsEnabled(device, previous, connected);
        if (known && previous.Connected == connected && previous.Enabled == enabled)
        {
            return Snapshot;
        }

        DateTimeOffset? change = !known
            ? (connected ? now : null)
            : previous.Connected == connected ? previous.LastChangeUtc : now;
        _devices[device] = previous with
        {
            Connected = connected,
            LastChangeUtc = change,
            Enabled = enabled,
        };
        return Publish();
    }

    /// <summary>An input event: counted for the rate and remembered on its board. Events from
    /// boards the user has not enabled are ignored (they are not registered anyway, but the
    /// Profiler may still send a stray one).</summary>
    public SkalarkiSnapshot HardwareEvent(SkalarkiHardwareEvent hardwareEvent)
    {
        ArgumentNullException.ThrowIfNull(hardwareEvent);
        if (!_devices.TryGetValue(hardwareEvent.Device, out var view) || !view.Enabled)
        {
            return Snapshot;
        }

        _eventTimes.Enqueue(hardwareEvent.AtUtc);
        _devices[hardwareEvent.Device] = view with
        {
            LastEventUtc = hardwareEvent.AtUtc,
            LastGroup = hardwareEvent.Group,
            LastControl = hardwareEvent.Control,
            EventCount = view.EventCount + 1,
        };
        return Publish(Snapshot with { EventsPerSecond = Rate(hardwareEvent.AtUtc) });
    }

    /// <summary>Once a second: lets the event rate decay to zero when the cockpit goes quiet.</summary>
    public SkalarkiSnapshot Tick(DateTimeOffset now)
    {
        var rate = Rate(now);
        return rate == Snapshot.EventsPerSecond ? Snapshot : Publish(Snapshot with { EventsPerSecond = rate });
    }

    /// <summary>The hardware-check session as the page should show it (package 2).</summary>
    public SkalarkiSnapshot SetCheck(SkalarkiCheckView? check)
        => Equals(check, Snapshot.Check) ? Snapshot : Publish(Snapshot with { Check = check });

    private bool WantsEnabled(SkalarkiDeviceId device, SkalarkiDeviceView previous, bool connected)
        => _enabled.Count == 0 ? previous.Enabled || connected : _enabled.Contains(device);

    private void DropPresence(DateTimeOffset? now)
    {
        foreach (var id in _devices.Keys.ToList())
        {
            var view = _devices[id];
            if (view.Connected)
            {
                _devices[id] = view with { Connected = false, LastChangeUtc = now ?? view.LastChangeUtc };
            }
        }
    }

    private double Rate(DateTimeOffset now)
    {
        while (_eventTimes.Count > 0 && now - _eventTimes.Peek() > RateWindow)
        {
            _eventTimes.Dequeue();
        }

        return Math.Round(_eventTimes.Count / RateWindow.TotalSeconds, 1);
    }

    private SkalarkiSnapshot Publish(SkalarkiSnapshot? next = null)
    {
        next ??= Snapshot;
        Snapshot = next with { Devices = new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView>(_devices) };
        return Snapshot;
    }
}
