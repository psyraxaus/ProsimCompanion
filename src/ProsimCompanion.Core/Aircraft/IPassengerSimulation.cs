namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Synthetic cabin loading for headless setups (the predecessor's W&amp;B SIMULATE tool):
/// GENERATE deals a random booked map for a pax count and loads the cabin to match without a
/// boarding run; CLEAR empties the cabin. GSX boarding writes the same seat-occupation dataref,
/// so a subsequent boarding overwrites whatever was simulated — this is a manual tool, not a
/// competing automation. Implemented by ProsimCompanion.Prosim (owner of the gateway write
/// path); surfaces resolve it optionally and degrade when absent.
/// </summary>
public interface IPassengerSimulation
{
    /// <summary>
    /// Generates <paramref name="count"/> passengers (clamped to cabin capacity): writes the
    /// booked seat map + passenger statistics — the name manifest follows the booked string —
    /// and the seat-occupation string, from which ProSim derives zone loads and CG.
    /// Returns false when any write fails (gateway unreachable); never throws.
    /// </summary>
    Task<bool> GenerateAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties the cabin: an all-false seat-occupation string (ProSim zeroes the zone loads
    /// itself). The booked map is deliberately left alone — clearing empties the aircraft,
    /// not the plan. Returns false when the write fails; never throws.
    /// </summary>
    Task<bool> ClearAsync(CancellationToken cancellationToken = default);
}
