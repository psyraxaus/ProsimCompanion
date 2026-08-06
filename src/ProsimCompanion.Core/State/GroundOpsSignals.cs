namespace ProsimCompanion.Core.State;

/// <summary>
/// Cross-feature ground-operation edges, published by the GSX pillar and consumed by the
/// flight-data pillar (feature projects never reference each other — this Core hub is the
/// seam). Events fire on the publisher's thread; subscribers must not block.
/// </summary>
public sealed class GroundOpsSignals
{
    /// <summary>GSX Refueling service went Active — the predecessor's preliminary-loadsheet
    /// trigger.</summary>
    public event Action? RefuelServiceActive;

    /// <summary>GSX Boarding completed — the final-loadsheet trigger (after a crew-realism
    /// delay owned by the subscriber).</summary>
    public event Action? BoardingCompleted;

    /// <summary>The ground-ops cycle reset for a new turnaround (raised at the GSX arrival
    /// reset). Loadsheet caches/edition counters reset here.</summary>
    public event Action? FlightCycleReset;

    /// <summary>GSX de-icing completed; the argument is the applied fluid type from
    /// <c>L:FSDT_GSX_DEICING_TYPE</c> (1=Type I … 4=Type IV, 0 = unknown). Arms the holdover
    /// card.</summary>
    public event Action<int>? DeiceCompleted;

    public void RaiseRefuelServiceActive() => RefuelServiceActive?.Invoke();

    public void RaiseBoardingCompleted() => BoardingCompleted?.Invoke();

    public void RaiseFlightCycleReset() => FlightCycleReset?.Invoke();

    public void RaiseDeiceCompleted(int fluidType) => DeiceCompleted?.Invoke(fluidType);
}
