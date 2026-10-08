using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Gate;

/// <summary>What to do with a persisted arrival gate at startup.</summary>
public enum ArrivalGateRestoreAction
{
    /// <summary>Nothing persisted, too old, or for another flight — start empty.</summary>
    Ignore,

    /// <summary>Re-queue only; the cruise transition (or Send Now) fires it as usual.</summary>
    Queue,

    /// <summary>Re-queue and dispatch now to both targets — the queue had not fired yet and
    /// the flight is already past the point where the cruise transition would.</summary>
    DispatchBoth,

    /// <summary>Re-queue and re-arm GSX only: the previous run already sent the ATC
    /// assignment, and only GSX's in-memory armed request died with the process.</summary>
    DispatchGsxOnly,
}

/// <summary>
/// Pure restore decision for the persisted arrival gate (2026-09-20 EGLL: two in-flight app
/// restarts dropped the confirmed gate). Kept free of I/O so the rules are unit-testable.
/// </summary>
public static class ArrivalGateRestorePlan
{
    /// <summary>Older than this and the file describes a previous day's flight, not the one
    /// in progress. A long-haul leg with a restart at the very end stays inside it.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(18);

    /// <param name="state">The persisted queue, or null.</param>
    /// <param name="nowUtc">Clock.</param>
    /// <param name="phase">The committed phase at restore time (Unknown at startup is normal).</param>
    /// <param name="ofpDestinationIcao">The loaded OFP's destination, empty when no OFP.</param>
    /// <param name="ofpFlightNumber">The loaded OFP's flight number (issue #102: a second
    /// identity beside the destination so a different flight to the same airport within the
    /// age window does not inherit the gate). Empty/null when no OFP, or the OFP has none —
    /// then only the destination is compared. The SimBrief request id was deliberately NOT
    /// used: pilots regenerate the OFP for the same leg (fuel, routing) and every regeneration
    /// gets a new request id while the flight — and its gate — stays the same.</param>
    public static ArrivalGateRestoreAction Decide(
        ArrivalGateState? state,
        DateTimeOffset nowUtc,
        FlightPhase phase,
        string? ofpDestinationIcao,
        string? ofpFlightNumber = null)
    {
        if (state is null || string.IsNullOrWhiteSpace(state.Gate))
        {
            return ArrivalGateRestoreAction.Ignore;
        }

        if (nowUtc - state.SavedAtUtc > MaxAge || state.SavedAtUtc > nowUtc + TimeSpan.FromMinutes(5))
        {
            return ArrivalGateRestoreAction.Ignore;
        }

        // A different destination on file than the plan now loaded is another flight's gate.
        var ofp = ArrivalGatePlan.Normalize(ofpDestinationIcao);
        var saved = ArrivalGatePlan.Normalize(state.DestinationIcao);
        if (ofp.Length > 0 && saved.Length > 0 && !string.Equals(ofp, saved, StringComparison.Ordinal))
        {
            return ArrivalGateRestoreAction.Ignore;
        }

        // Same destination, different flight number: another flight (a same-day return to
        // the same airport under a different number). Compared only when both sides know it.
        var ofpFlight = ArrivalGatePlan.Normalize(ofpFlightNumber);
        var savedFlight = ArrivalGatePlan.Normalize(state.FlightNumber);
        if (ofpFlight.Length > 0 && savedFlight.Length > 0 && !string.Equals(ofpFlight, savedFlight, StringComparison.Ordinal))
        {
            return ArrivalGateRestoreAction.Ignore;
        }

        if (state.Fired)
        {
            return ArrivalGateRestoreAction.DispatchGsxOnly;
        }

        return IsPastCruiseEntry(phase)
            ? ArrivalGateRestoreAction.DispatchBoth
            : ArrivalGateRestoreAction.Queue;
    }

    /// <summary>Phases at or after the cruise transition the auto-fire keys on — a queue
    /// restored here would otherwise wait for a transition that already happened. The same
    /// set as <see cref="ArrivalGatePlan.IsAutoFirePhase"/> (issue #102 made the auto-fire
    /// itself cover these phases; the restore decision keeps its own name for its callers).</summary>
    public static bool IsPastCruiseEntry(FlightPhase phase) => ArrivalGatePlan.IsAutoFirePhase(phase);
}
