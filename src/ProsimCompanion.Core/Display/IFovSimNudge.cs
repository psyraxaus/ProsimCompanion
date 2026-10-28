namespace ProsimCompanion.Core.Display;

/// <summary>
/// The best-effort poke that makes a freshly written <c>cameras.cfg</c> zoom visible without
/// reloading the aircraft: cockpit zoom back to 50 % (the slider is relative to
/// <c>InitialZoom</c>) and a cockpit view reset. Implemented over SimConnect in the Sim
/// project; the Display &amp; FOV page only uses the answer for its "takes effect on the next
/// aircraft load" line. Never a reason to refuse the file write.
/// </summary>
public interface IFovSimNudge
{
    /// <summary>Writes the two camera SimVars. False when MSFS is not connected or the sim
    /// refused either write (logged at Debug) — the pilot reloads the aircraft instead.</summary>
    Task<bool> NudgeAsync(CancellationToken cancellationToken = default);
}
