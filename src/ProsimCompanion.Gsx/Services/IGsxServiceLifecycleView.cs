namespace ProsimCompanion.Gsx.Services;

/// <summary>
/// The read side of <see cref="GsxServiceLifecycleTracker"/> that the departure pump's
/// inputs gatherer and status board need — a seam narrow enough to fake per test, so the
/// shell's effect ordering is exercised without a live GSX. The tracker's edge rules stay
/// where they are; this interface only names what a reader may ask.
/// </summary>
public interface IGsxServiceLifecycleView
{
    /// <summary>True when the service has completed this cycle (explicitly or via the
    /// return-to-available rule).</summary>
    bool IsCompleted(string serviceId);

    /// <summary>True when the automation has called this service and its cycle has not yet
    /// completed.</summary>
    bool IsPending(string serviceId);

    /// <summary>Cycle flags for every service seen this turnaround, keyed by service id.</summary>
    IReadOnlyDictionary<string, GsxServiceLifecycleTracker.ServiceCycleSnapshot> SnapshotCycles();
}
