using ProsimCompanion.Core.Hardware.Skalarki;

namespace ProsimCompanion.Core.Diagnostics.Ecam.HardwareCheck;

/// <summary>
/// The PANELS page's hardware check: one line per watched board with a prompt ("PRESS CLR"),
/// ticked off by the first input event from that board. Pure — the connection service feeds
/// it presence and events and publishes <see cref="View"/> into the Skalarki snapshot; no
/// timers, no SDK. A board that is not connected when the check starts is Skipped (it
/// cannot be touched); a board that drops during the check is Lost and goes back to Pending
/// when it returns, because its earlier proof says nothing about the board as re-plugged.
/// </summary>
public sealed class HardwareCheckSession
{
    /// <summary>The prompt for a board the layout file names none for.</summary>
    public const string DefaultPrompt = "ANY KEY";

    private readonly List<SkalarkiCheckItem> _items = [];
    private IReadOnlyDictionary<SkalarkiDeviceId, string> _prompts = new Dictionary<SkalarkiDeviceId, string>();
    private DateTimeOffset _startedUtc;

    /// <summary>The session as the page shows it; null while no check is running.</summary>
    public SkalarkiCheckView? View { get; private set; }

    /// <summary>Begins a check over <paramref name="devices"/> (page order).</summary>
    /// <param name="prompts">Per-board prompt text from the layout file.</param>
    /// <param name="presence">The boards' current presence, to skip the ones that are not there.</param>
    public void Start(
        IReadOnlyList<SkalarkiDeviceId> devices,
        IReadOnlyDictionary<SkalarkiDeviceId, string> prompts,
        IReadOnlyDictionary<SkalarkiDeviceId, SkalarkiDeviceView> presence,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(presence);

        _prompts = prompts;
        _startedUtc = now;
        _items.Clear();
        foreach (var device in devices)
        {
            var connected = presence.TryGetValue(device, out var view) && view.Connected;
            _items.Add(new SkalarkiCheckItem(device, PromptFor(device), connected ? SkalarkiCheckState.Pending : SkalarkiCheckState.Skipped, null, null));
        }

        Rebuild();
    }

    /// <summary>Starts the same board list over again (the RESTART button).</summary>
    public void Restart(IReadOnlyDictionary<SkalarkiDeviceId, SkalarkiDeviceView> presence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(presence);
        Start([.. _items.Select(i => i.Device)], _prompts, presence, now);
    }

    /// <summary>Ends the check; the page shows no list.</summary>
    public void Stop()
    {
        _items.Clear();
        View = null;
    }

    /// <summary>An input event from a board: the first one proves it. True when the view changed.</summary>
    public bool OnDeviceEvent(SkalarkiDeviceId device, string control, DateTimeOffset now)
    {
        var index = _items.FindIndex(i => i.Device == device);
        if (index < 0 || _items[index].State != SkalarkiCheckState.Pending)
        {
            return false;
        }

        _items[index] = _items[index] with { State = SkalarkiCheckState.Done, DoneAtUtc = now, Control = control };
        Rebuild();
        return true;
    }

    /// <summary>The Profiler lost a board mid-check: its line turns LOST whatever it was.</summary>
    public bool OnDeviceLost(SkalarkiDeviceId device, DateTimeOffset now)
    {
        var index = _items.FindIndex(i => i.Device == device);
        if (index < 0 || _items[index].State is SkalarkiCheckState.Lost or SkalarkiCheckState.Skipped)
        {
            return false;
        }

        _items[index] = _items[index] with { State = SkalarkiCheckState.Lost, DoneAtUtc = null, Control = null };
        Rebuild();
        return true;
    }

    /// <summary>A lost (or skipped) board is back: it has to be touched again.</summary>
    public bool OnDeviceBack(SkalarkiDeviceId device)
    {
        var index = _items.FindIndex(i => i.Device == device);
        if (index < 0 || _items[index].State is not (SkalarkiCheckState.Lost or SkalarkiCheckState.Skipped))
        {
            return false;
        }

        _items[index] = _items[index] with { State = SkalarkiCheckState.Pending };
        Rebuild();
        return true;
    }

    private string PromptFor(SkalarkiDeviceId device)
        => _prompts.TryGetValue(device, out var prompt) && !string.IsNullOrWhiteSpace(prompt) ? prompt.Trim().ToUpperInvariant() : DefaultPrompt;

    private void Rebuild()
    {
        var total = _items.Count(i => i.State != SkalarkiCheckState.Skipped);
        var done = _items.Count(i => i.State == SkalarkiCheckState.Done);
        View = new SkalarkiCheckView(_startedUtc, [.. _items], done, total);
    }
}
