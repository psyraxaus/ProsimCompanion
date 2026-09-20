using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Gsx.Gate;

/// <summary>What the arrival-gate dispatcher should do on this evaluation.</summary>
public enum GateDispatchStep
{
    /// <summary>Preconditions not met (not Ready, no destination, on the ground before
    /// departure, backoff running) — stay armed, re-evaluate on the next state change.</summary>
    Wait,

    /// <summary>Airborne and GSX still has the departure (or no) airport loaded: drive the
    /// in-flight "Select airport" menu so GSX loads the destination context.</summary>
    PickAirport,

    /// <summary>GSX has the destination loaded and the aircraft is not yet parked: send
    /// <c>gate.select</c> now.</summary>
    Send,

    /// <summary>GSX has the destination loaded but the aircraft is already stopped at a stand
    /// with engines off — <c>selectGate</c> refuses while parked (Handler Scripts Developer
    /// Guide), so a send would only produce a misleading not_found.</summary>
    TooLate,
}

/// <summary>Inputs to <see cref="GateDispatchPlanner.Decide"/>; all read from stores.</summary>
public sealed record GateDispatchInputs(
    bool GsxReady,
    string? LoadedAirportIcao,
    string? DestinationIcao,
    FlightPhase Phase,
    FlightDataSnapshot? Data,
    DateTimeOffset NowUtc,
    DateTimeOffset? LastAirportPickAttemptUtc,
    int AirportPickAttempts);

/// <summary>
/// Pure decision core for the arrival-gate dispatch (2026-09-21 review, issue #44/#75).
/// GSX loads the destination airport only when the aircraft reaches it on the ground — and
/// asks "Select Position" the same second — unless someone picks the airport from the
/// in-flight "Select airport" menu first (the Handler Scripts guide's onSelectGateInFlight
/// moment). Every previous gate.select went out on the ground, mostly after GSX already had
/// the aircraft at a stand, where selectGate refuses; the refusal surfaced as not_found and
/// was chased as a name-matching bug for six weeks. This planner sends in the air.
/// </summary>
public static class GateDispatchPlanner
{
    /// <summary>Quiet time between in-flight airport picks: the destination joins GSX's
    /// nearby list only within range, and each attempt opens the GSX menu on screen.</summary>
    public static readonly TimeSpan AirportPickBackoff = TimeSpan.FromMinutes(2);

    /// <summary>Attempts before giving up on the in-flight pick (the ground taxi-in path
    /// still runs when GSX loads the airport itself after landing).</summary>
    public const int MaxAirportPickAttempts = 12;

    public static GateDispatchStep Decide(GateDispatchInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (!inputs.GsxReady || string.IsNullOrWhiteSpace(inputs.DestinationIcao))
        {
            return GateDispatchStep.Wait;
        }

        var destination = inputs.DestinationIcao.Trim();
        var loaded = inputs.LoadedAirportIcao?.Trim();
        if (string.Equals(loaded, destination, StringComparison.OrdinalIgnoreCase))
        {
            return IsParked(inputs) ? GateDispatchStep.TooLate : GateDispatchStep.Send;
        }

        if (!IsAirborneForPick(inputs.Phase))
        {
            return GateDispatchStep.Wait;
        }

        if (inputs.AirportPickAttempts >= MaxAirportPickAttempts)
        {
            return GateDispatchStep.Wait;
        }

        if (inputs.LastAirportPickAttemptUtc is { } last && inputs.NowUtc - last < AirportPickBackoff)
        {
            return GateDispatchStep.Wait;
        }

        return GateDispatchStep.PickAirport;
    }

    /// <summary>The in-flight window: from cruise until the approach. Climb is excluded — the
    /// destination is rarely in GSX's nearby list yet and the pilot is busy.</summary>
    public static bool IsAirborneForPick(FlightPhase phase)
        => phase is FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach;

    /// <summary>On the ground, stopped, engines off — GSX considers this "parked" and refuses
    /// a gate change without a revoke. Rolling out or taxiing in is still fine.</summary>
    public static bool IsParked(GateDispatchInputs inputs)
    {
        var data = inputs.Data;
        if (data is null || !data.IsValid)
        {
            // No data: judge by phase alone.
            return inputs.Phase is FlightPhase.Shutdown;
        }

        return data.OnGround
            && data.GroundSpeedKt <= ParkingConflictGate.StoppedGroundSpeedKt
            && !data.AnyEngineRunning;
    }
}
