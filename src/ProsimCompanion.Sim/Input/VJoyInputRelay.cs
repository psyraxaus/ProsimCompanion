using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Sim.Input;

/// <summary>
/// The vJoy input relay (2026-10-24, owner option A): presses a button on a vJoy virtual
/// joystick on THIS PC when a remote surface asks over the command API, so ChasePlane views
/// bound to those buttons can be changed from a Stream Deck key on a second PC.
/// <para>
/// Lazy and degrade-not-fail: nothing is loaded until the first press or the page's Check
/// button; a missing driver, a 32-bit DLL, a device another program owns — each becomes a
/// state on the Setup page and an <c>unavailable</c>/<c>preconditionFailed</c> reason, never an
/// exception out of the seam. Presses are serialised (one at a time, press → hold → release in
/// a <c>finally</c>) so a cancelled request can never leave a button stuck. The device stays
/// acquired between presses (vJoy's acquire is slow and exclusive) and is released on
/// dispose or when the settings change. No winmm/dinput anywhere — vJoy's DLL talks to its
/// own driver (the 2026-10-11 crash rule).
/// </para>
/// </summary>
public sealed class VJoyInputRelay : IInputRelay, IDisposable
{
    public const string PressEvent = "input.vjoy";
    private const string DllName = "vJoyInterface.dll";

    private readonly IOptionsMonitor<InputRelayOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<VJoyInputRelay> _logger;
    private readonly SemaphoreSlim _press = new(1, 1);
    private readonly object _gate = new();
    private readonly IDisposable? _subscription;

    private VJoyInterface? _vjoy;
    private uint _acquiredDevice;
    private string _loadedPath = "";
    private InputRelaySnapshot _snapshot = InputRelaySnapshot.Off;
    private int _settingsChanged;

    public VJoyInputRelay(
        IOptionsMonitor<InputRelayOptions> options,
        JsonlEventLog eventLog,
        ILogger<VJoyInputRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
        _subscription = options.OnChange(_ => Interlocked.Exchange(ref _settingsChanged, 1));
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public InputRelaySnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <inheritdoc />
    public async Task<InputRelaySnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await _press.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _settingsChanged, 1); // a Check always re-reads the saved settings
            EnsureReady(_options.CurrentValue);
            return Snapshot();
        }
        finally
        {
            _press.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> PressAsync(int button, int? holdMs, string? label, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var hold = Math.Clamp(holdMs ?? options.HoldMs, 20, 2000);
        label = string.IsNullOrWhiteSpace(label) ? "button " + button.ToString(CultureInfo.InvariantCulture) : label.Trim();

        await _press.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = EnsureReady(options);
            if (state.State != InputRelayState.Ready || _vjoy is null)
            {
                return state.State == InputRelayState.Off
                    ? CommandResult.Unavailable(state.Detail)
                    : CommandResult.PreconditionFailed(state.Detail);
            }

            if (state.ButtonCount > 0 && button > state.ButtonCount)
            {
                return CommandResult.PreconditionFailed(FormattableString.Invariant(
                    $"vJoy device {options.VJoyDevice} has {state.ButtonCount} buttons — button {button} does not exist (vJoyConf adds more)."));
            }

            var device = (uint)options.VJoyDevice;
            var pressed = false;
            try
            {
                if (!_vjoy.SetButton(device, (byte)button, true))
                {
                    return Fail(options, FormattableString.Invariant($"vJoy refused to press button {button} on device {options.VJoyDevice}."));
                }

                pressed = true;
                // Not cancellable on purpose: once down, the button comes up after the hold.
                await Task.Delay(hold, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (pressed)
                {
                    _vjoy.SetButton(device, (byte)button, false);
                }
            }

            var now = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "vJoy relay pressed {Label} (device {Device} button {Button}, {HoldMs} ms) for {Source}",
                label, options.VJoyDevice, button, hold, CommandRegistry.CurrentSource ?? "command");
            _eventLog.Record(PressEvent, new
            {
                device = options.VJoyDevice,
                button,
                holdMs = hold,
                label,
                source = CommandRegistry.CurrentSource ?? "command",
            });
            Publish(state with { LastPressUtc = now, LastPressLabel = label });
            return CommandResult.Ok(FormattableString.Invariant(
                $"Pressed {label} — vJoy device {options.VJoyDevice} button {button} for {hold} ms."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "vJoy relay press failed");
            return Fail(options, "vJoy press failed: " + ex.Message);
        }
        finally
        {
            _press.Release();
        }
    }

    /// <summary>Loads the DLL and acquires the device if needed; returns the state. Runs under
    /// the press semaphore. Settings changes (path, device, switch) release and reload.</summary>
    private InputRelaySnapshot EnsureReady(InputRelayOptions options)
    {
        if (Interlocked.Exchange(ref _settingsChanged, 0) == 1)
        {
            ReleaseDevice();
        }

        if (!options.Enabled)
        {
            Unload();
            return Publish(new InputRelaySnapshot(InputRelayState.Off, "The vJoy relay is off — Settings → Setup → vJoy relay.", 0, _snapshot.LastPressUtc, _snapshot.LastPressLabel));
        }

        var path = ResolvePath(options.VJoyInterfacePath);
        if (path is null)
        {
            Unload();
            return Publish(State(InputRelayState.NotInstalled,
                "vJoyInterface.dll not found — install the vJoy driver on this PC or enter the DLL path."));
        }

        if (_vjoy is null || !string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Unload();
            try
            {
                _vjoy = VJoyInterface.Load(path);
                _loadedPath = path;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                _logger.LogWarning(ex, "vJoyInterface.dll failed to load from {Path}", path);
                return Publish(State(InputRelayState.NotInstalled,
                    ex is BadImageFormatException
                        ? "vJoyInterface.dll is the 32-bit DLL — point at the x64 folder."
                        : "vJoyInterface.dll could not be loaded: " + ex.Message));
            }
        }

        if (!_vjoy.DriverEnabled)
        {
            return Publish(State(InputRelayState.NotInstalled, "The vJoy driver is not running (vJoyEnabled = false)."));
        }

        var device = (uint)Math.Clamp(options.VJoyDevice, 1, 16);
        if (_acquiredDevice == device)
        {
            return Publish(State(InputRelayState.Ready, Ready(options, _vjoy.ButtonCount(device)), _vjoy.ButtonCount(device)));
        }

        ReleaseDevice();
        switch (_vjoy.Status(device))
        {
            case VJoyInterface.DeviceStatus.Missing:
            case VJoyInterface.DeviceStatus.Unknown:
                return Publish(State(InputRelayState.DeviceMissing,
                    FormattableString.Invariant($"vJoy device {device} does not exist — create it in vJoyConf (one device with up to 128 buttons).")));
            case VJoyInterface.DeviceStatus.Busy:
                return Publish(State(InputRelayState.DeviceBusy,
                    FormattableString.Invariant($"vJoy device {device} is owned by another program — pick a free device number.")));
        }

        if (!_vjoy.Acquire(device))
        {
            return Publish(State(InputRelayState.DeviceBusy,
                FormattableString.Invariant($"vJoy device {device} could not be acquired.")));
        }

        _acquiredDevice = device;
        _vjoy.ResetButtons(device);
        var buttons = _vjoy.ButtonCount(device);
        _logger.LogInformation(
            "vJoy relay ready: device {Device} acquired, {Buttons} buttons, driver {Version:X4}, {Path}",
            device, buttons, _vjoy.Version, path);
        return Publish(State(InputRelayState.Ready, Ready(options, buttons), buttons));
    }

    private static string Ready(InputRelayOptions options, int buttons) => FormattableString.Invariant(
        $"vJoy device {options.VJoyDevice} acquired — {buttons} buttons.");

    private InputRelaySnapshot State(InputRelayState state, string detail, int buttons = 0) =>
        new(state, detail, buttons, _snapshot.LastPressUtc, _snapshot.LastPressLabel);

    private CommandResult Fail(InputRelayOptions options, string reason)
    {
        Publish(State(InputRelayState.Failed, reason, _snapshot.ButtonCount));
        // Next press re-acquires from scratch: the usual cause is the driver or device going away.
        ReleaseDevice();
        return CommandResult.Failed(reason);
    }

    /// <summary>The configured path when it exists, else the driver's default folders.</summary>
    internal static string? ResolvePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim();
            if (Directory.Exists(path))
            {
                path = Path.Combine(path, DllName);
            }

            return File.Exists(path) ? path : null;
        }

        return ExpectedFolders.VJoy()
            .Select(folder => Path.Combine(folder, DllName))
            .FirstOrDefault(File.Exists);
    }

    private void ReleaseDevice()
    {
        if (_vjoy is not null && _acquiredDevice != 0)
        {
            try
            {
                _vjoy.ResetButtons(_acquiredDevice);
                _vjoy.Relinquish(_acquiredDevice);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "vJoy relinquish failed");
            }
        }

        _acquiredDevice = 0;
    }

    private void Unload()
    {
        ReleaseDevice();
        _vjoy?.Dispose();
        _vjoy = null;
        _loadedPath = "";
    }

    private InputRelaySnapshot Publish(InputRelaySnapshot snapshot)
    {
        bool changed;
        lock (_gate)
        {
            changed = snapshot != _snapshot;
            _snapshot = snapshot;
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return snapshot;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _press.Wait(TimeSpan.FromSeconds(3));
        try
        {
            Unload();
        }
        finally
        {
            _press.Dispose();
        }
    }
}
