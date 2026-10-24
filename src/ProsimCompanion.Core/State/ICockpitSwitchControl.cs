using ProsimCompanion.Core.Commands;

namespace ProsimCompanion.Core.State;

/// <summary>Live state of one configured cockpit switch, for the key faces.</summary>
/// <param name="Id">The row id.</param>
/// <param name="Value">The switch dataref's value, or null before ProSim sent one.</param>
/// <param name="Position">The catalogue label of that value ("Pushed", "NAV"), or null.</param>
/// <param name="UpperLit">The upper lamp (indicator dataref non-zero).</param>
/// <param name="LowerLit">The lower lamp (indicator non-zero, else value non-zero).</param>
/// <param name="Stale">ProSim disconnected after the last value.</param>
public sealed record CockpitSwitchState(string Id, int? Value, string? Position, bool UpperLit, bool LowerLit, bool Stale);

/// <summary>Every configured switch's state, published by the ProSim pillar.</summary>
public sealed record CockpitSwitchesSnapshot(IReadOnlyList<CockpitSwitchState> Switches)
{
    /// <summary>No rows published yet (the store's initial value, and the ProSim pillar absent).</summary>
    public static CockpitSwitchesSnapshot Empty { get; } = new([]);
}

/// <summary>The store the status feed and the settings page read.</summary>
public sealed class CockpitSwitchStateStore : SnapshotStore<CockpitSwitchesSnapshot>
{
    public CockpitSwitchStateStore() : base(CockpitSwitchesSnapshot.Empty)
    {
    }
}

/// <summary>
/// The cockpit-switch relay as the command handlers and the web UI see it (Core only; the
/// ProSim pillar implements it). Degrade-not-fail: with ProSim away every call answers in
/// words — <c>unavailable</c>, never an exception.
/// </summary>
public interface ICockpitSwitchControl
{
    /// <summary>One key press on the row: toggle / momentary / cycle per the row's press mode.
    /// <paramref name="longPress"/> steps a cycling selector backwards.</summary>
    Task<CommandResult> PressAsync(string id, bool longPress, CancellationToken cancellationToken = default);

    /// <summary>Write an explicit catalogue value (a knob position).</summary>
    Task<CommandResult> SetAsync(string id, int value, CancellationToken cancellationToken = default);
}
