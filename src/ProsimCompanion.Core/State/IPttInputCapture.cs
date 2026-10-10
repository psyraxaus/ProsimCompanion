namespace ProsimCompanion.Core.State;

/// <summary>A connected winmm joystick-class device (stick, yoke, HOTAS, button box).</summary>
public sealed record JoystickDeviceView(int Id, string Name);

/// <summary>What the user pressed during a capture: exactly one of the keyboard key or the
/// joystick pair is set. <paramref name="KeyName"/> is in the format the PTT key parser
/// accepts, so it can be written straight into the settings.</summary>
public sealed record PttInputCaptureResult(string? KeyName, int? JoystickId, int? JoystickButton)
{
    public bool IsJoystick => JoystickId is not null;
}

/// <summary>
/// Lets the settings UI bind push-to-talk by pressing the actual key or button instead of
/// typing key names and raw winmm ids. Kept in Core so the Web project (which references only
/// Core) can inject it; implemented by the speech pillar's PTT service, which already owns the
/// global keyboard hook and the joystick poller. The Blazor circuit runs on the sim PC, so a
/// server-side capture sees the same devices PTT will use.
/// </summary>
public interface IPttInputCapture
{
    /// <summary>The joystick devices the last <see cref="ScanJoysticksAsync"/> found (winmm ids
    /// 0–15 with their product names). Never touches the joystick layer itself: empty until a
    /// scan has run. Windows' winmm/dinput joystick API has crashed the process three times
    /// on the owner's sim PC (2026-09-20, 2026-10-10, 2026-10-11 — the last one from this very
    /// list being built while the Voice FO settings page opened), so nothing calls it unless
    /// the pilot asks.</summary>
    IReadOnlyList<JoystickDeviceView> GetJoysticks();

    /// <summary>Probes the sixteen winmm ids off the caller's thread and returns the connected
    /// devices — the pilot's explicit "scan" or a joystick capture, never a page load.</summary>
    Task<IReadOnlyList<JoystickDeviceView>> ScanJoysticksAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the next key press (and joystick button press when
    /// <paramref name="includeJoysticks"/> is set) and returns it, or null when nothing was
    /// pressed within <paramref name="timeout"/>. Inputs already held when the capture starts
    /// are ignored — only a fresh press binds.</summary>
    Task<PttInputCaptureResult?> CaptureAsync(
        bool includeJoysticks, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Live pressed state of the two bindings — the settings card's indicator lamps
    /// (Prosim2FO parity: press the bound input, watch the light).</summary>
    bool OwnPttPressed { get; }
    bool AtcMutePressed { get; }

    /// <summary>Raised on any pressed-state edge, on the service's worker thread — Blazor
    /// consumers marshal with InvokeAsync.</summary>
    event EventHandler? PressedChanged;
}
