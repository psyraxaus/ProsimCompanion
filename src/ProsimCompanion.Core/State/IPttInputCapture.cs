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
    /// <summary>Currently connected joystick devices (winmm ids 0–15 that answer a position
    /// probe), with their product names. Empty when none are connected.</summary>
    IReadOnlyList<JoystickDeviceView> GetJoysticks();

    /// <summary>Waits for the next key press (and joystick button press when
    /// <paramref name="includeJoysticks"/> is set) and returns it, or null when nothing was
    /// pressed within <paramref name="timeout"/>. Inputs already held when the capture starts
    /// are ignored — only a fresh press binds.</summary>
    Task<PttInputCaptureResult?> CaptureAsync(
        bool includeJoysticks, TimeSpan timeout, CancellationToken cancellationToken);
}
