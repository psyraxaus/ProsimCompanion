namespace ProsimCompanion.Core.State;

/// <summary>
/// Enumerates this machine's audio endpoints for the settings pickers, so users choose
/// devices from a list instead of typing Windows device names from memory. Kept in Core so
/// the Web project (which references only Core) can inject it; implemented by
/// ProsimCompanion.Speech (registered with the speech pillar). Enumeration failures return
/// empty lists — the settings pages then fall back to manual entry.
/// </summary>
public interface IAudioDeviceCatalog
{
    /// <summary>Capture (microphone) device names, exactly as the recognizer matches them —
    /// WaveIn product names, which Windows truncates to 31 characters.</summary>
    IReadOnlyList<string> GetCaptureDeviceNames();

    /// <summary>Active render (output) device friendly names, exactly as playback matches
    /// them.</summary>
    IReadOnlyList<string> GetRenderDeviceNames();
}
