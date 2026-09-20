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
    public static ArrivalGateRestoreAction Decide(
        ArrivalGateState? state,
        DateTimeOffset nowUtc,
        FlightPhase phase,
        string? ofpDestinationIcao)
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

        if (state.Fired)
        {
            return ArrivalGateRestoreAction.DispatchGsxOnly;
        }

        return IsPastCruiseEntry(phase)
            ? ArrivalGateRestoreAction.DispatchBoth
            : ArrivalGateRestoreAction.Queue;
    }

    /// <summary>Phases at or after the cruise transition the auto-fire keys on — a queue
    /// restored here would otherwise wait for a transition that already happened.</summary>
    public static bool IsPastCruiseEntry(FlightPhase phase)
        => phase is FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach
            or FlightPhase.LandingRollout or FlightPhase.TaxiIn;
}
