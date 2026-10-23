using System.Collections.Frozen;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>
/// The FO-side-only write gate for the FO's own duties (issue #188). <c>ProsimWriteGate</c>
/// allows the whole <c>S_FCU_</c> / <c>A_FCU_</c> prefixes — wide enough for the captain's EFIS1
/// too — so this feature narrows to exactly four names: the FO baro knob (STD/QNH push-pull),
/// the FO baro value input, the FO ND mode and the FO ND range. Authored for the default seat
/// (human left, FO on EFIS2) and seat-mapped once (<see cref="PilotSeatMap"/>): with the human
/// right-seated the FO's side IS EFIS1 and EFIS2 is then refused. A test enumerates every
/// EFIS1 name in <see cref="ProsimDataRefNames"/> and proves it fails here with the default seat.
/// </summary>
public static class FoSideDutiesGate
{
    /// <summary>The four FO-side names as authored (default seat).</summary>
    public static readonly IReadOnlyList<string> AuthoredNames =
    [
        ProsimDataRefNames.Efis2BaroStd,
        ProsimDataRefNames.Efis2BaroHpaInput,
        ProsimDataRefNames.Efis2NdMode,
        ProsimDataRefNames.Efis2NdZoom,
    ];

    private static readonly FrozenSet<string> Left = AuthoredNames.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> Right = AuthoredNames.Select(n => PilotSeatMap.Map(n, humanIsRightSeat: true)).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when the FO may write this (seat-mapped) name.</summary>
    public static bool IsAllowed(string name, bool humanIsRightSeat)
        => !string.IsNullOrWhiteSpace(name) && (humanIsRightSeat ? Right : Left).Contains(name);

    /// <summary>Throws for any name outside the FO's four; the message names the seat.</summary>
    public static void EnsureAllowed(string name, bool humanIsRightSeat)
    {
        if (!IsAllowed(name, humanIsRightSeat))
        {
            throw new InvalidOperationException(
                $"Dataref '{name}' is not an FO-side duty write (human in the {(humanIsRightSeat ? "right" : "left")} seat). " +
                "The FO sets only its own baro knob, baro value, ND mode and ND range.");
        }
    }
}
