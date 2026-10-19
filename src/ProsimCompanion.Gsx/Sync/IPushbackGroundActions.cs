using System.Text.RegularExpressions;
using ProsimCompanion.Gsx.Menu;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Everything the pushback tick does to the ground crew, in one seam: the door, jetway/stairs
/// and ground-equipment modules plus the one GSX menu pick it makes (the good-engine-start
/// confirmation). The gatherer reads <see cref="AnyDoorOpen"/> through it too. One interface
/// because the pushback shell is the only caller of this exact set, and faking it is how the
/// tick's effects are exercised without the sim.
/// </summary>
public interface IPushbackGroundActions
{
    /// <summary>Any entry/cargo door open in ProSim.</summary>
    bool AnyDoorOpen { get; }

    Task CloseAllDoorsAsync();

    Task RequestJetwayRemovalAsync();

    /// <summary>The departure-phase jetway/stairs connect step (#9 phase-point hook).</summary>
    GsxPrepStatus RunDepartureJetwayStep();

    Task RemoveStairsAfterDepartureAsync(string mode);

    Task RemoveGroundEquipmentForDepartureAsync();

    /// <summary>One gradual-removal tick (the non-sequence flow's own removal timing).</summary>
    Task TickGradualRemovalAsync();

    /// <summary>Picks "Confirm good engine start" on GSX's "Interrupt pushback?" menu
    /// (issue #106).</summary>
    Task<GsxIntentResult> ConfirmGoodEngineStartAsync(CancellationToken cancellationToken);
}

/// <summary>The adapter that fills <see cref="IPushbackGroundActions"/> with the real door,
/// jetway/stairs and ground-equipment modules and the menu intent executor.</summary>
public sealed class GsxPushbackGroundActions : IPushbackGroundActions
{
    /// <summary>Prosim2GSX's proven entry pattern — only the engine-start variant of the
    /// interrupt menu matches; other variants fail safe.</summary>
    private static readonly GsxMenuIntent ConfirmGoodEngineStart = new()
    {
        Name = "confirm good engine start",
        TitlePrefixes = ["Interrupt pushback"],
        EntryPattern = new Regex("^confirm good engine", RegexOptions.IgnoreCase),
    };

    private readonly GsxDoorService _doors;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly GsxMenuIntentExecutor _menuExecutor;

    public GsxPushbackGroundActions(
        GsxDoorService doors,
        GsxJetwayStairsService jetwayStairs,
        GsxGroundEquipmentService groundEquipment,
        GsxMenuIntentExecutor menuExecutor)
    {
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(jetwayStairs);
        ArgumentNullException.ThrowIfNull(groundEquipment);
        ArgumentNullException.ThrowIfNull(menuExecutor);
        _doors = doors;
        _jetwayStairs = jetwayStairs;
        _groundEquipment = groundEquipment;
        _menuExecutor = menuExecutor;
    }

    /// <inheritdoc />
    public bool AnyDoorOpen => _doors.AnyDoorOpen;

    /// <inheritdoc />
    public Task CloseAllDoorsAsync() => _doors.CloseAllDoorsAsync();

    /// <inheritdoc />
    public Task RequestJetwayRemovalAsync() => _jetwayStairs.RequestRemovalAsync();

    /// <inheritdoc />
    public GsxPrepStatus RunDepartureJetwayStep() => _jetwayStairs.RunDepartureStep();

    /// <inheritdoc />
    public Task RemoveStairsAfterDepartureAsync(string mode) => _jetwayStairs.RemoveStairsAfterDepartureAsync(mode);

    /// <inheritdoc />
    public Task RemoveGroundEquipmentForDepartureAsync() => _groundEquipment.RemoveForDepartureAsync();

    /// <inheritdoc />
    public Task TickGradualRemovalAsync() => _groundEquipment.TickGradualRemovalAsync();

    /// <inheritdoc />
    public Task<GsxIntentResult> ConfirmGoodEngineStartAsync(CancellationToken cancellationToken)
        => _menuExecutor.ExecuteAsync(ConfirmGoodEngineStart, cancellationToken);
}
