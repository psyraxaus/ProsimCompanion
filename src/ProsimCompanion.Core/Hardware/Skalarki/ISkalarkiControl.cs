namespace ProsimCompanion.Core.Hardware.Skalarki;

/// <summary>
/// The PANELS page's three buttons, implemented by the Skalarki connection service and
/// resolved by the page as optional (absent = the pillar is not registered, buttons hidden).
/// These are the ONLY paths that drive outputs: LAMP TEST (bounded by the lamp-test duration)
/// and HW RESET (behind the page's explicit confirm). Everything else the link does is read.
/// </summary>
public interface ISkalarkiControl
{
    /// <summary>Lights every output of one board, or of every enabled connected board when
    /// null, for the configured duration, then turns them off. Returns an ECAM word for the
    /// page: "n LIT", "NO LINK", "NO DEVICE", "FAILED".</summary>
    Task<string> LampTestAsync(SkalarkiDeviceId? device, CancellationToken cancellationToken);

    /// <summary>Resets one board's controller. Returns "SENT", "NO LINK" or "FAILED".</summary>
    Task<string> HardwareResetAsync(SkalarkiDeviceId device, bool hard, CancellationToken cancellationToken);

    /// <summary>Starts a hardware check over the enabled boards with the given prompts.</summary>
    void StartCheck(IReadOnlyDictionary<SkalarkiDeviceId, string> prompts);

    /// <summary>Starts the running check over from the top; nothing when none is running.</summary>
    void RestartCheck();

    /// <summary>Ends the running check.</summary>
    void StopCheck();
}
