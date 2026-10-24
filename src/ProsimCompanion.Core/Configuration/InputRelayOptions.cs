namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the vJoy input relay (<c>inputRelay</c>, 2026-10-24, owner option A): a remote
/// surface (the Stream Deck plugin's Camera view key on a second PC) asks this app, over the command API,
/// to press a button on a <b>vJoy</b> virtual joystick on the sim PC. ChasePlane (and anything
/// else that binds controller buttons) sees a real button press, which is the only way a view
/// change can reach it from another PC — a virtual joystick exists only on the PC that presses
/// it, and ChasePlane has no network interface of its own.
/// <para>
/// Off by default: this actuates input on the sim PC, so it is opt-in like the command API
/// that carries it. Needs the vJoy driver installed on the sim PC (one device with enough
/// buttons); the app loads <c>vJoyInterface.dll</c> from the driver's folder at first use and
/// never ships it. Contract: docs/integrations/cockpit-switches.md ("vJoy relay").
/// </para>
/// </summary>
public sealed class InputRelayOptions : IOptionSection
{
    public static string SectionName => "inputRelay";

    /// <summary>Master switch. While off, <c>input.*</c> commands answer <c>unavailable</c>
    /// and the DLL is never loaded.</summary>
    [OptionField(FieldKind.Bool, "setup.inputRelay.enable")]
    public bool Enabled { get; set; }

    /// <summary>Full path of <c>vJoyInterface.dll</c> (64-bit). Empty = look in the vJoy
    /// driver's default folder (<c>C:\Program Files\vJoy\x64</c>).</summary>
    [OptionField(FieldKind.Text, "setup.inputRelay.dll", TextKind = TextFieldKind.FilePath, BrowseStartIn = "VJoy", BrowseFilter = "vJoyInterface.dll|vJoyInterface.dll", Normalize = FieldNormalization.Trim, DependsOn = "Enabled")]
    public string VJoyInterfacePath { get; set; } = "";

    /// <summary>The vJoy device number (1–16) the app acquires. One device per owner: pick one
    /// no other program on this PC (SPAD, FSUIPC, another Stream Deck plugin) acquires.</summary>
    [OptionField(FieldKind.Number, "setup.inputRelay.device", Min = "1", Max = "16", DependsOn = "Enabled")]
    public int VJoyDevice { get; set; } = 1;

    /// <summary>How long a relayed press holds the button. ChasePlane and MSFS register a
    /// press from ~50 ms; 150 ms is safe without feeling sluggish.</summary>
    [OptionField(FieldKind.Number, "setup.inputRelay.hold", Suffix = "ms", Min = "20", Max = "2000", Advanced = true)]
    public int HoldMs { get; set; } = 150;

    /// <summary>Named buttons — <c>input.pressView {"name":"wing"}</c> presses the button a
    /// row maps, so a remote key names a view instead of a number. Edited as rows on the
    /// Setup page; the raw <c>input.vjoyPress {"button":5}</c> needs no row.</summary>
    public List<InputViewButton> Views { get; set; } = [];
}

/// <summary>One named button of the vJoy relay (a ChasePlane view, usually).</summary>
public sealed class InputViewButton
{
    /// <summary>The name a remote key sends (compared trimmed, case-insensitively).</summary>
    [OptionField(FieldKind.Text, "setup.inputRelay.view.name", Normalize = FieldNormalization.Trim)]
    public string Name { get; set; } = "";

    /// <summary>The vJoy button (1–128) ChasePlane has this view bound to.</summary>
    [OptionField(FieldKind.Number, "setup.inputRelay.view.button", Min = "1", Max = "128")]
    public int Button { get; set; } = 1;
}
