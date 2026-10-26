namespace ProsimCompanion.Web.Components.Diagnostics;

/// <summary>A point on the traced drawing, in its own units (see <see cref="A320TopView"/>).</summary>
public readonly record struct DrawingPoint(int X, int Y);

/// <summary>
/// Where the ground services meet the aircraft on the traced drawing (owner idea 2026-10-27:
/// anchor points, not screen pixels). Every value is in the drawing's units and was measured
/// on its pixels; the GSX page scales them with the hull, so the connectors land on the same
/// spot at every window size. Change a value here and every line, dot and door marker that
/// uses it moves together.
/// </summary>
public static class A320Anchors
{
    /// <summary>Fuselage side lines where the constant section begins.</summary>
    public const int FuselageLeft = A320TopView.FuselageLeft;
    public const int FuselageRight = A320TopView.FuselageRight;

    /// <summary>The radome tip: where the tug pushes.</summary>
    public static readonly DrawingPoint NoseTip = new(819, 42);

    /// <summary>The ground-power receptacle: right side, just aft of the cockpit glazing (which ends at row 141).</summary>
    public static readonly DrawingPoint GpuReceptacle = new(FuselageRight, 172);

    /// <summary>The refuel panel: under the right wing behind the engine (nacelle x 1033–1122),
    /// at mid-chord (leading edge 693, trailing edge 870 at that station).</summary>
    public static readonly DrawingPoint RefuelPanel = new(1078, 780);

    /// <summary>Door rectangles as drawn: forward pair rows 210–258, aft pair rows 1317–1365,
    /// each just inside the fuselage line. The point is the rectangle's centre.</summary>
    public static readonly DrawingPoint Door1L = new(752, 234);
    public static readonly DrawingPoint Door1R = new(886, 234);
    public static readonly DrawingPoint Door4L = new(752, 1341);
    public static readonly DrawingPoint Door4R = new(886, 1341);

    /// <summary>Top-left corner of each drawn door rectangle, for the open-door markers.</summary>
    public static readonly DrawingPoint Door1LRect = new(735, 210);
    public static readonly DrawingPoint Door1RRect = new(871, 210);
    public static readonly DrawingPoint Door4LRect = new(741, 1317);
    public static readonly DrawingPoint Door4RRect = new(866, 1317);

    /// <summary>Doors the drawing does not show (overwing exits, cargo): placed on the fuselage
    /// line at the real aircraft's proportions.</summary>
    public static readonly DrawingPoint Door2L = new(FuselageLeft, 820);
    public static readonly DrawingPoint Door2R = new(FuselageRight, 820);
    public static readonly DrawingPoint Door3L = new(FuselageLeft, 900);
    public static readonly DrawingPoint Door3R = new(FuselageRight, 900);
    public static readonly DrawingPoint CargoForward = new(FuselageRight, 470);
    public static readonly DrawingPoint CargoAft = new(FuselageRight, 1180);
    public static readonly DrawingPoint CargoBulk = new(FuselageRight, 1400);

    /// <summary>Every anchor, for the sanity test that keeps them inside the drawing.</summary>
    public static IReadOnlyList<(string Name, DrawingPoint Point)> All { get; } =
    [
        (nameof(NoseTip), NoseTip), (nameof(GpuReceptacle), GpuReceptacle), (nameof(RefuelPanel), RefuelPanel),
        (nameof(Door1L), Door1L), (nameof(Door1R), Door1R), (nameof(Door4L), Door4L), (nameof(Door4R), Door4R),
        (nameof(Door2L), Door2L), (nameof(Door2R), Door2R), (nameof(Door3L), Door3L), (nameof(Door3R), Door3R),
        (nameof(CargoForward), CargoForward), (nameof(CargoAft), CargoAft), (nameof(CargoBulk), CargoBulk),
    ];
}
