namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The one spelling of "the flight is live enough to act on its data" (review 2026-11-05:
/// small talk, the walkaround, scenario mode and the transition reminder each early-returned
/// on their own copy). <see cref="IFlightPhaseSource.IsLive"/> is the session gate — the sim is
/// in a flight, the phase-critical datarefs are fresh, the sample is plausible; a tick-driven
/// module that then reads the sample needs it valid as well, which is what this adds.
/// </summary>
public static class FlightGate
{
    /// <summary>Live AND holding a valid data sample.</summary>
    public static bool IsLiveFor(FlightStateView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return IsLiveFor(view.IsLive, view.Data);
    }

    /// <summary>Live AND the given sample (from another reader of the same aircraft) is valid.</summary>
    public static bool IsLiveFor(bool live, FlightDataSnapshot? sample) => live && sample is { IsValid: true };
}
