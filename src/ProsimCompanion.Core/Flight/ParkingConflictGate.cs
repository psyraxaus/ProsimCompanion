namespace ProsimCompanion.Core.Flight;

/// <summary>
/// When a GSX "where are you parked?" prompt is a parking conflict worth telling the pilot
/// about, and when it is just GSX asking early. Shared by the GSX question catalogue (which
/// publishes conflicts) and the FO's parking advisory (which speaks them) so both apply the
/// same rule. Pure and deterministic.
/// </summary>
/// <remarks>
/// 2026-09-20 EGLL: GSX raised "Select Position at EGLL/Heathrow" 31 s after touchdown and the
/// FO told the captain, mid landing roll, to "select the stand in the GSX menu, or reposition
/// the aircraft". GSX asks for the arrival parking during taxi-in whenever no gate was
/// pre-selected; that is not a conflict. A conflict needs a parked aircraft.
/// </remarks>
public static class ParkingConflictGate
{
    /// <summary>Ground speed at or below which the aircraft counts as stopped — the same figure
    /// the reposition step waits for.</summary>
    public const double StoppedGroundSpeedKt = 1.0;

    /// <summary>True when the aircraft is on the ground, stopped, with no engine running — the
    /// only state in which "GSX does not recognise the parking" can be true and actionable.
    /// No data (source down) reads as parked: with nothing to contradict it, the prompt is
    /// taken at face value rather than silenced.</summary>
    public static bool IsParkedEnginesOff(FlightDataSnapshot? data)
    {
        if (data is null || !data.IsValid)
        {
            return true;
        }

        return data.OnGround
            && data.GroundSpeedKt <= StoppedGroundSpeedKt
            && !data.AnyEngineRunning
            && !data.EngineStarting;
    }
}
