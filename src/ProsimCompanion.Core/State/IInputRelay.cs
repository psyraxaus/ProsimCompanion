using ProsimCompanion.Core.Commands;

namespace ProsimCompanion.Core.State;

/// <summary>Where the vJoy relay stands, for the Setup page's pill and the command reasons.</summary>
public enum InputRelayState
{
    /// <summary>Switched off in settings; the DLL is not loaded.</summary>
    Off,

    /// <summary><c>vJoyInterface.dll</c> was not found, failed to load, or the driver is not running.</summary>
    NotInstalled,

    /// <summary>The configured device number does not exist in the vJoy configuration.</summary>
    DeviceMissing,

    /// <summary>Another program owns the device (vJoy allows one owner per device).</summary>
    DeviceBusy,

    /// <summary>Device acquired; presses go through.</summary>
    Ready,

    /// <summary>The last press failed after the device had been acquired.</summary>
    Failed,
}

/// <summary>A snapshot of the relay: state, a one-line detail (never a secret), the device's
/// button count once known, and the last press.</summary>
public sealed record InputRelaySnapshot(
    InputRelayState State,
    string Detail,
    int ButtonCount,
    DateTimeOffset? LastPressUtc,
    string? LastPressLabel)
{
    public static InputRelaySnapshot Off { get; } = new(InputRelayState.Off, "", 0, null, null);
}

/// <summary>
/// The vJoy input relay as Core sees it (Web and the command handlers → Core only; the Sim
/// project implements it). Degrade-not-fail: with the driver absent every member answers in
/// words and nothing throws.
/// </summary>
public interface IInputRelay
{
    /// <summary>Current state for the Setup page.</summary>
    InputRelaySnapshot Snapshot();

    /// <summary>Raised whenever the snapshot changes.</summary>
    event Action? Changed;

    /// <summary>Press-and-release one button of the configured device. <paramref name="holdMs"/>
    /// null = the configured hold. <paramref name="label"/> is what the log and the session
    /// event call the press (a view name, or the raw button).</summary>
    Task<CommandResult> PressAsync(int button, int? holdMs, string? label, CancellationToken cancellationToken = default);

    /// <summary>Load the DLL and acquire the device now (the page's "Check" button), returning
    /// the fresh snapshot. Works on the SAVED settings.</summary>
    Task<InputRelaySnapshot> ProbeAsync(CancellationToken cancellationToken = default);
}
