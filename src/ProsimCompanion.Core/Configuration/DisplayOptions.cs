namespace ProsimCompanion.Core.Configuration;

/// <summary>Where a physical display sits in the pilot's view. Exactly one is the Main
/// display: the one straight ahead whose geometry decides the sim's single field of view.</summary>
public enum DisplayRole
{
    /// <summary>The forward window. Its vertical field of view is what MSFS gets.</summary>
    Main,

    /// <summary>A side window to the left of the main one (Multi Window).</summary>
    Left,

    /// <summary>A side window to the right of the main one (Multi Window).</summary>
    Right,

    /// <summary>A window above the main one.</summary>
    Overhead,

    /// <summary>Anything else; listed for the Multi Window read-out only.</summary>
    Other,
}

/// <summary>
/// The pilot's physical displays and how true-to-life the sim's field of view should be
/// (<c>display</c>, 2026-10-28; design: <c>claude/fov-tool-design.md</c>). From the size,
/// aspect and eye distance of the Main display the app computes the vertical FOV a 1:1 view
/// needs and writes it as <c>InitialZoom</c> into the aircraft's saved custom camera
/// (<c>cameras.cfg</c> under the MSFS 2024 LocalCache — never a package folder, see
/// <see cref="Display.CamerasCfgEditor"/>). Secondary displays are read-out only: their ideal
/// FOV and their Multi Window rotation offsets are shown, nothing is written for them.
/// </summary>
public sealed class DisplayOptions : IOptionSection
{
    public static string SectionName => "display";

    /// <summary>How far from the sim's default view (49.2° vertical) towards the true 1:1
    /// view the applied FOV goes: 1.0 = exactly life-size, 0.0 = leave the default. A
    /// hardware cockpit with the TV as the forward window wants 1.0 even though that is much
    /// tighter than the default; a desk pilot may prefer a blend. Edited as a 0–100 % slider.</summary>
    public double Realism { get; set; } = 1.0;

    /// <summary>The displays, one row each. The default is a neutral single screen, not any
    /// particular rig: 32", 16:9, 0.8 m straight ahead.</summary>
    public List<DisplayEntry> Displays { get; set; } = [new DisplayEntry()];

    /// <summary>Also write the pitch component of <c>InitialPbh</c> so a screen whose centre
    /// sits above or below the eye gets its camera tilted to match. Off by default: most
    /// pilots have the screen centred and the saved view's own pitch is what they want.</summary>
    [OptionField(FieldKind.Bool, "display.applyPitch")]
    public bool ApplyPitch { get; set; }

    /// <summary>The Main display, or null when there is not exactly one (the page shows a
    /// warning and the calculator refuses to plan).</summary>
    public DisplayEntry? SingleMain()
        => Displays.Count(d => d.Role == DisplayRole.Main) == 1
            ? Displays.First(d => d.Role == DisplayRole.Main)
            : null;
}

/// <summary>One flat physical display, as the pilot measured it.</summary>
public sealed class DisplayEntry
{
    /// <summary>A label for the page ("Main", "Left TV").</summary>
    [OptionField(FieldKind.Text, "display.row.name", Normalize = FieldNormalization.Trim)]
    public string Name { get; set; } = "Main";

    /// <summary>See <see cref="DisplayRole"/>. A dropdown on the page's grid line.</summary>
    [OptionField(FieldKind.Select, "display.row.role", Choices = "displayRole")]
    public DisplayRole Role { get; set; } = DisplayRole.Main;

    /// <summary>Screen diagonal in inches — the number on the box. The page offers a
    /// centimetre toggle that converts on the way in and out.</summary>
    [OptionField(FieldKind.Number, "display.row.diagonal", Min = "1", Max = "300", Step = "0.1", Suffix = "in")]
    public double DiagonalInches { get; set; } = 32;

    /// <summary>Aspect ratio, width part (16 of 16:9).</summary>
    [OptionField(FieldKind.Number, "display.row.aspectWidth", Min = "1", Max = "64", Step = "1")]
    public int AspectWidth { get; set; } = 16;

    /// <summary>Aspect ratio, height part (9 of 16:9).</summary>
    [OptionField(FieldKind.Number, "display.row.aspectHeight", Min = "1", Max = "64", Step = "1")]
    public int AspectHeight { get; set; } = 9;

    /// <summary>Eye to the centre of the screen, in metres, measured straight along the line
    /// of sight to that screen.</summary>
    [OptionField(FieldKind.Number, "display.row.distance", Min = "0.1", Max = "10", Step = "0.01", Suffix = "m")]
    public double DistanceMetres { get; set; } = 0.8;

    /// <summary>Where the screen's centre is, left or right of straight ahead, in degrees;
    /// positive = to the right. Becomes the Multi Window lateral rotation.</summary>
    [OptionField(FieldKind.Number, "display.row.yaw", Min = "-180", Max = "180", Step = "0.1", Suffix = "°")]
    public double YawDegrees { get; set; }

    /// <summary>How far the screen's centre sits above the pilot's eye, in metres; negative
    /// = below. Decides the camera pitch (when <see cref="DisplayOptions.ApplyPitch"/> is
    /// on) and the Multi Window vertical rotation.</summary>
    [OptionField(FieldKind.Number, "display.row.centreAboveEye", Min = "-3", Max = "3", Step = "0.01", Suffix = "m")]
    public double CentreAboveEyeMetres { get; set; }
}
