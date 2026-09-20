using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>Ground-automation phases (the predecessor's model, minus states that are sequencing
/// flags rather than aircraft states).</summary>
public enum GsxAutomationPhase
{
    SessionStart,
    Preparation,
    PushBack,
    TaxiOut,
    Flight,
    TaxiIn,
    Arrival,
}

/// <summary>Pure mapping from the central flight phase model — automation never re-derives
/// aircraft state itself.</summary>
public static class GsxAutomationPhaseMapper
{
    public static GsxAutomationPhase Map(FlightPhase phase) => phase switch
    {
        FlightPhase.Unknown => GsxAutomationPhase.SessionStart,
        // Departure (boarding underway, still at the gate) is the same ground-automation
        // window as Preflight: the departure sequence is already running inside it.
        FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.Departure => GsxAutomationPhase.Preparation,
        FlightPhase.PushbackAndStart => GsxAutomationPhase.PushBack,
        FlightPhase.TaxiOut or FlightPhase.TakeoffRoll => GsxAutomationPhase.TaxiOut,
        FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach => GsxAutomationPhase.Flight,
        FlightPhase.LandingRollout or FlightPhase.TaxiIn => GsxAutomationPhase.TaxiIn,
        FlightPhase.Shutdown => GsxAutomationPhase.Arrival,
        _ => GsxAutomationPhase.SessionStart,
    };
}
