using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Shared display mapping for the Flight Phase card: the seven-block progress strip ("pill")
/// and the spaced-upper phase text. One place for the mapping so the dashboard and the
/// change log (<see cref="FlightStatusChangeLog"/>, issue #110) can never disagree about
/// what the pilot was shown.
/// </summary>
public static class FlightStatusPresentation
{
    /// <summary>Progress-strip block labels, left to right (Prosim2GSX FlightStatusPanel clone).</summary>
    public static readonly string[] BlockLabels =
        ["PREFLIGHT", "DEPARTURE", "PUSHBACK", "TAXI OUT", "FLIGHT", "TAXI IN", "ARRIVAL"];

    /// <summary>Index of the highlighted block. The strip is NOT a pure phase view: on the
    /// ground pre-push it also reads the GSX departure sequence, so DEPARTURE can light while
    /// the phase text still says COLD AND DARK — the exact contradictory read of issue #110.</summary>
    public static int ActiveBlock(FlightPhase phase, bool departureStarted, bool departureComplete) => phase switch
    {
        FlightPhase.Unknown or FlightPhase.ColdAndDark or FlightPhase.Preflight =>
            departureStarted && !departureComplete ? 1 : 0,
        FlightPhase.PushbackAndStart => 2,
        FlightPhase.TaxiOut or FlightPhase.TakeoffRoll => 3,
        FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach => 4,
        FlightPhase.LandingRollout or FlightPhase.TaxiIn => 5,
        FlightPhase.Shutdown => 6,
        _ => 0,
    };

    /// <summary>The label of the highlighted block for the given inputs.</summary>
    public static string ActiveBlockLabel(FlightPhase phase, bool departureStarted, bool departureComplete)
        => BlockLabels[ActiveBlock(phase, departureStarted, departureComplete)];

    /// <summary>"PushbackAndStart" renders as "PUSHBACK AND START" — the phase text exactly
    /// as the Flight Phase card displays it.</summary>
    public static string PhaseDisplay(FlightPhase phase)
    {
        var pascal = phase.ToString();
        var result = new System.Text.StringBuilder(pascal.Length + 4);
        foreach (var ch in pascal)
        {
            if (char.IsUpper(ch) && result.Length > 0)
            {
                result.Append(' ');
            }
            result.Append(char.ToUpperInvariant(ch));
        }
        return result.ToString();
    }
}
