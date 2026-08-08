using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Seat-relative dataref mapping (speech.pilotSeat). Checklist JSON, commands.json and the
/// code-level control lists are all authored for the DEFAULT geometry — human in the left
/// seat, virtual pilot on the right (CDU2 / A_FC_FO_* / EFIS2). When the human flies from
/// the right seat, every side-dependent dataref the virtual pilot touches is SWAPPED at the
/// execution choke points, and reads of the human's side swap symmetrically. The swap is an
/// involution: applying it twice restores the original, so the same helper serves writes
/// (virtual pilot's side) and monitor reads (human's side).
/// </summary>
public static class PilotSeatMap
{
    private static readonly (string A, string B)[] SwapPairs =
    [
        ("system.analog.A_FC_FO_", "system.analog.A_FC_CAPT_"),
        ("system.switches.S_CDU2_KEY_", "system.switches.S_CDU1_KEY_"),
        ("aircraft.mcdu2.", "aircraft.mcdu1."),
        ("EFIS2_", "EFIS1_"),
    ];

    /// <summary>True when the human flies from the right seat (virtual pilot on the left).</summary>
    public static bool HumanIsRightSeat(SpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PilotSeat.Trim().Equals("right", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Swaps the side of a side-dependent dataref when the human sits right;
    /// identity otherwise (and for side-independent names).</summary>
    public static string Map(string dataref, bool humanIsRightSeat)
    {
        ArgumentNullException.ThrowIfNull(dataref);
        if (!humanIsRightSeat)
        {
            return dataref;
        }

        foreach (var (a, b) in SwapPairs)
        {
            if (dataref.Contains(a, StringComparison.Ordinal))
            {
                return dataref.Replace(a, b, StringComparison.Ordinal);
            }

            if (dataref.Contains(b, StringComparison.Ordinal))
            {
                return dataref.Replace(b, a, StringComparison.Ordinal);
            }
        }

        return dataref;
    }
}
