using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>The cross-feature signals the relay derives from GSX service lifecycle edges.</summary>
public enum GroundOpsRelaySignal
{
    RefuelServiceActive,
    RefuelCompleted,
    BoardingStarted,
    BoardingCompleted,
    DeboardingCompleted,
    DeiceCompleted,
}

/// <summary>
/// Pure mapping + once-per-cycle memory behind <see cref="GsxGroundOpsSignalRelay"/>
/// (issue #90). The lifecycle tracker latches each (service, event) once per cycle, but two
/// paths leak a second edge inside one turnaround: a service that drops out of the mirror
/// (Couatl/GSX restart, aircraft swap) loses its latches and re-fires on the next reading,
/// and the de-ice match is a substring over the service id, so two spellings in one mirror
/// ("Deice" + "De-Ice") each complete. The milestone consumers (prelim/final loadsheet,
/// outbound notifications, the holdover card) must hear each milestone once per cycle, so the
/// memory lives here, keyed by the SIGNAL rather than the service id, and is cleared only by
/// the real flight-cycle reset — never by a mirror vanish.
/// <para>The two phase-evidence signals (<see cref="GroundOpsRelaySignal.BoardingStarted"/>,
/// <see cref="GroundOpsRelaySignal.DeboardingCompleted"/>) are deliberately NOT latched: the
/// phase engine is idempotent and must never be starved of evidence, e.g. a sim reload on the
/// ground that never passes the arrival reset.</para>
/// Not thread-safe — the relay serializes calls (tracker events arrive on one thread).
/// </summary>
public sealed class GroundOpsSignalRelayCore
{
    private readonly HashSet<GroundOpsRelaySignal> _raisedThisCycle = [];

    /// <summary>Maps one lifecycle edge to its signal, or null when the edge carries no
    /// cross-feature meaning. Pure; no memory involved.</summary>
    public static GroundOpsRelaySignal? Map(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(serviceId);

        if (serviceId.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase))
        {
            return lifecycleEvent switch
            {
                GsxServiceLifecycleEvent.Active => GroundOpsRelaySignal.RefuelServiceActive,
                GsxServiceLifecycleEvent.Completed => GroundOpsRelaySignal.RefuelCompleted,
                _ => null,
            };
        }

        if (serviceId.Equals(GsxServiceIds.Boarding, StringComparison.OrdinalIgnoreCase))
        {
            return lifecycleEvent switch
            {
                GsxServiceLifecycleEvent.Active => GroundOpsRelaySignal.BoardingStarted,
                GsxServiceLifecycleEvent.Completed => GroundOpsRelaySignal.BoardingCompleted,
                _ => null,
            };
        }

        if (serviceId.Equals(GsxServiceIds.Deboarding, StringComparison.OrdinalIgnoreCase))
        {
            return lifecycleEvent == GsxServiceLifecycleEvent.Completed
                ? GroundOpsRelaySignal.DeboardingCompleted
                : null;
        }

        // Service id tolerance: GSX has shipped "Deice"/"De-Ice"/"Deicing" spellings.
        if (lifecycleEvent == GsxServiceLifecycleEvent.Completed
            && serviceId.Replace("-", "").Contains("deic", StringComparison.OrdinalIgnoreCase))
        {
            return GroundOpsRelaySignal.DeiceCompleted;
        }

        return null;
    }

    /// <summary>True for the signals that fire at most once per flight cycle; false for the
    /// phase-evidence signals that always pass through.</summary>
    public static bool IsOncePerCycle(GroundOpsRelaySignal signal)
        => signal is not (GroundOpsRelaySignal.BoardingStarted or GroundOpsRelaySignal.DeboardingCompleted);

    /// <summary>Decides whether an edge is relayed: returns the signal to raise, or null when
    /// the edge maps to nothing or its once-per-cycle signal already went out. Marks the
    /// signal raised when it returns it.</summary>
    public GroundOpsRelaySignal? TryTake(string serviceId, GsxServiceLifecycleEvent lifecycleEvent, out bool suppressed)
    {
        suppressed = false;
        var signal = Map(serviceId, lifecycleEvent);
        if (signal is null)
        {
            return null;
        }

        if (!IsOncePerCycle(signal.Value))
        {
            return signal;
        }

        if (!_raisedThisCycle.Add(signal.Value))
        {
            suppressed = true;
            return null;
        }

        return signal;
    }

    /// <summary>True when the signal has already been relayed this cycle.</summary>
    public bool HasRaised(GroundOpsRelaySignal signal) => _raisedThisCycle.Contains(signal);

    /// <summary>The flight-cycle boundary (GSX arrival reset): every milestone may fire again.</summary>
    public void Reset() => _raisedThisCycle.Clear();
}
