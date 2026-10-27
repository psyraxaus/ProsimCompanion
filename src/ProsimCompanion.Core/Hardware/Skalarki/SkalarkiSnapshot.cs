using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Hardware.Skalarki;

/// <summary>One board as the Profiler and the user describe it.</summary>
/// <param name="Enabled">The user wants this board watched (an empty enabled list means every
/// board the Profiler reports connected).</param>
/// <param name="Connected">The Profiler's last word on whether the board's USB link is up.</param>
/// <param name="LastChangeUtc">When <see cref="Connected"/> last flipped; the page prints
/// "LOST nn S" from it.</param>
/// <param name="LastEventUtc">When the board last sent an input event.</param>
/// <param name="LastGroup">The SDK group of that event (FCU, ELEC, …).</param>
/// <param name="LastControl">The SDK event name of that event (FCU_SPD_PUSH, …).</param>
/// <param name="EventCount">Input events since the link came up.</param>
public sealed record SkalarkiDeviceView(
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastChangeUtc,
    DateTimeOffset? LastEventUtc,
    string? LastGroup,
    string? LastControl,
    long EventCount)
{
    /// <summary>A board nobody has heard of yet.</summary>
    public static SkalarkiDeviceView Unknown { get; } = new(false, false, null, null, null, null, 0);
}

/// <summary>One board's hardware-check line (package 2).</summary>
public enum SkalarkiCheckState
{
    Pending,
    Done,
    Lost,
    Skipped,
}

/// <summary>One board in a hardware check: the prompt to show, where it stands, and which
/// control proved it when it is done.</summary>
public sealed record SkalarkiCheckItem(
    SkalarkiDeviceId Device,
    string Prompt,
    SkalarkiCheckState State,
    DateTimeOffset? DoneAtUtc,
    string? Control);

/// <summary>The hardware-check session as the page shows it.</summary>
public sealed record SkalarkiCheckView(
    DateTimeOffset StartedUtc,
    IReadOnlyList<SkalarkiCheckItem> Items,
    int Done,
    int Total)
{
    /// <summary>True when every board that was not skipped has reported.</summary>
    public bool Complete => Total > 0 && Done == Total;
}

/// <summary>
/// Everything the PANELS page and the Skalarki settings page read: the Profiler link, the
/// licence and SDK version, the input-event rate, every board the Profiler has mentioned
/// (enabled or not, so the settings page can offer the ones it has seen), and the running
/// hardware check when there is one.
/// </summary>
public sealed record SkalarkiSnapshot(
    ConnectionState LinkState,
    DateTimeOffset? LinkSinceUtc,
    string? License,
    string? SdkVersion,
    double EventsPerSecond,
    IReadOnlyDictionary<SkalarkiDeviceId, SkalarkiDeviceView> Devices,
    SkalarkiCheckView? Check = null,
    string? Reason = null)
{
    /// <summary>Before the service has said anything: disabled, nothing known.</summary>
    public static SkalarkiSnapshot Empty { get; } = new(
        ConnectionState.Disabled,
        null,
        null,
        null,
        0,
        new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView>(),
        null,
        null);

    /// <summary>The boards the page draws: enabled ones, in SDK order.</summary>
    public IEnumerable<KeyValuePair<SkalarkiDeviceId, SkalarkiDeviceView>> EnabledDevices
        => Devices.Where(pair => pair.Value.Enabled).OrderBy(pair => (int)pair.Key);

    /// <summary>How many enabled boards the Profiler currently reports connected.</summary>
    public int ConnectedCount => Devices.Count(pair => pair.Value is { Enabled: true, Connected: true });

    /// <summary>How many boards are enabled.</summary>
    public int EnabledCount => Devices.Count(pair => pair.Value.Enabled);
}

/// <summary>
/// The Skalarki state store (campaign #86 shape): the connection service is its only writer,
/// the diagnostics collector and the two web pages read it. Record equality means a tick that
/// changes nothing wakes nobody.
/// </summary>
public sealed class SkalarkiStatusStore : SnapshotStore<SkalarkiSnapshot>
{
    public SkalarkiStatusStore()
        : base(SkalarkiSnapshot.Empty)
    {
    }

    /// <summary>Replaces the whole snapshot (the state core owns the transitions).</summary>
    public void Publish(SkalarkiSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Update(_ => snapshot);
    }
}
