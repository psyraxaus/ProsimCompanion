using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Display;

/// <summary>One display's physical size and the view angles it subtends from the eye.</summary>
/// <param name="WidthMetres">Panel width.</param>
/// <param name="HeightMetres">Panel height.</param>
/// <param name="VerticalFovDegrees">The angle the panel's height subtends: what MSFS calls FOV.</param>
/// <param name="HorizontalFovDegrees">The angle the width subtends (MSFS derives it from the
/// window aspect; shown for the pilot, never written).</param>
/// <param name="PitchOffsetDegrees">Where the panel centre is above the eye, as an angle;
/// positive = above. The camera must look UP by this much, which in MSFS's
/// <c>InitialPbh</c> (positive pitch looks down) is the NEGATIVE of this value.</param>
public sealed record DisplayGeometry(
    double WidthMetres,
    double HeightMetres,
    double VerticalFovDegrees,
    double HorizontalFovDegrees,
    double PitchOffsetDegrees);

/// <summary>One secondary display's line of the Multi Window read-out.</summary>
/// <param name="Display">The row as configured.</param>
/// <param name="Geometry">Its own size and angles.</param>
/// <param name="LateralRotationDegrees">The Multi Window lateral rotation: the row's yaw.</param>
/// <param name="VerticalRotationDegrees">The Multi Window vertical rotation: the row's pitch offset.</param>
/// <param name="IdealVerticalFovDegrees">The FOV this display would want on its own.</param>
/// <param name="DeltaFromAppliedDegrees">Ideal minus applied: how far off this display is
/// because MSFS renders every window with the Main display's FOV.</param>
public sealed record FovPlanRow(
    DisplayEntry Display,
    DisplayGeometry Geometry,
    double LateralRotationDegrees,
    double VerticalRotationDegrees,
    double IdealVerticalFovDegrees,
    double DeltaFromAppliedDegrees);

/// <summary>What the Apply button will write and what the pilot should see afterwards.</summary>
/// <param name="Main">The Main display's geometry.</param>
/// <param name="TrueVerticalFovDegrees">The 1:1 vertical FOV of the Main display.</param>
/// <param name="AppliedVerticalFovDegrees">After the realism blend.</param>
/// <param name="InitialZoom">The <c>InitialZoom</c> value for the applied FOV.</param>
/// <param name="ExpectedFovRadians">The applied FOV in radians, 4 dp — the number Developer
/// Mode → Debug → Aircraft → Camera Blend shows as <c>Fov</c>.</param>
/// <param name="PitchDegrees">The <c>InitialPbh</c> pitch for the Main display (MSFS sign:
/// negative looks up), written only when <see cref="DisplayOptions.ApplyPitch"/> is on.</param>
/// <param name="Rows">One line per secondary display.</param>
public sealed record FovPlan(
    DisplayGeometry Main,
    double TrueVerticalFovDegrees,
    double AppliedVerticalFovDegrees,
    double InitialZoom,
    double ExpectedFovRadians,
    double PitchDegrees,
    IReadOnlyList<FovPlanRow> Rows);

/// <summary>
/// The pure maths behind the Display &amp; FOV page (design: <c>claude/fov-tool-design.md</c>).
/// MSFS computes a camera's vertical field of view from its zoom as
/// <c>FOV = arctan(InitialZoom × 3.3125)</c> (MSFS 2024 SDK, cameras.cfg reference), so the
/// zoom for a wanted FOV is <c>tan(vFOV) / 3.3125</c>. The common default
/// <c>InitialZoom = 0.35</c> gives the ~49° vertical the community measured on Asobo aircraft,
/// which is the anchor the realism blend starts from. Nothing here touches the sim or a file.
/// </summary>
public static class FovCalculator
{
    /// <summary>The SDK's zoom-to-FOV factor: <c>FOV = atan(zoom × 3.3125)</c>.</summary>
    public const double ZoomFactor = 3.3125;

    /// <summary>The vertical FOV of the sim's default view (<c>InitialZoom = 0.35</c>), the
    /// anchor of the realism blend.</summary>
    public const double SimDefaultVerticalFovDegrees = 49.2;

    private const double MetresPerInch = 0.0254;

    /// <summary>A display's panel size and view angles from its diagonal, aspect, distance
    /// and height above the eye.</summary>
    public static DisplayGeometry Compute(DisplayEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DiagonalInches <= 0 || entry.AspectWidth <= 0 || entry.AspectHeight <= 0 || entry.DistanceMetres <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Diagonal, aspect and distance must all be positive.");
        }

        var diagonal = entry.DiagonalInches * MetresPerInch;
        var ratio = (double)entry.AspectWidth / entry.AspectHeight;
        var height = diagonal / Math.Sqrt(1 + ratio * ratio);
        var width = height * ratio;
        var vertical = 2 * Math.Atan(height / (2 * entry.DistanceMetres));
        var horizontal = 2 * Math.Atan(width / (2 * entry.DistanceMetres));
        var pitch = Math.Atan(entry.CentreAboveEyeMetres / entry.DistanceMetres);
        return new DisplayGeometry(width, height, Degrees(vertical), Degrees(horizontal), Degrees(pitch));
    }

    /// <summary>The <c>InitialZoom</c> that gives a vertical FOV: <c>tan(vFOV) / 3.3125</c>.</summary>
    public static double InitialZoomForVerticalFov(double vFovDegrees)
    {
        if (vFovDegrees <= 0 || vFovDegrees >= 180)
        {
            throw new ArgumentOutOfRangeException(nameof(vFovDegrees), "A vertical FOV lies strictly between 0° and 180°.");
        }

        return Math.Tan(Radians(vFovDegrees)) / ZoomFactor;
    }

    /// <summary>The vertical FOV an <c>InitialZoom</c> gives: <c>atan(zoom × 3.3125)</c>.</summary>
    public static double VerticalFovForInitialZoom(double initialZoom)
    {
        if (initialZoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialZoom), "InitialZoom must be positive.");
        }

        return Degrees(Math.Atan(initialZoom * ZoomFactor));
    }

    /// <summary>Blends the true FOV with the sim default: <c>49.2 + realism × (true − 49.2)</c>.
    /// Realism outside 0–1 is clamped.</summary>
    public static double BlendRealism(double trueVFov, double realism)
    {
        var blend = Math.Clamp(realism, 0.0, 1.0);
        return SimDefaultVerticalFovDegrees + blend * (trueVFov - SimDefaultVerticalFovDegrees);
    }

    /// <summary>The full plan for the configured displays. Exactly one Main display is
    /// required: it decides the single FOV MSFS renders every window with.</summary>
    /// <exception cref="InvalidOperationException">Zero or several Main displays.</exception>
    public static FovPlan Plan(DisplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mains = options.Displays.Count(d => d.Role == DisplayRole.Main);
        if (mains != 1)
        {
            throw new InvalidOperationException($"Exactly one display must have the role Main; {mains} found.");
        }

        var mainEntry = options.Displays.First(d => d.Role == DisplayRole.Main);
        var main = Compute(mainEntry);
        var applied = BlendRealism(main.VerticalFovDegrees, options.Realism);
        var zoom = InitialZoomForVerticalFov(applied);
        var rows = options.Displays
            .Where(d => d.Role != DisplayRole.Main)
            .Select(d =>
            {
                var geometry = Compute(d);
                return new FovPlanRow(
                    d,
                    geometry,
                    d.YawDegrees,
                    geometry.PitchOffsetDegrees,
                    geometry.VerticalFovDegrees,
                    geometry.VerticalFovDegrees - applied);
            })
            .ToList();

        return new FovPlan(
            main,
            main.VerticalFovDegrees,
            applied,
            zoom,
            Math.Round(Radians(applied), 4),
            -main.PitchOffsetDegrees,
            rows);
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;
}
