using ProsimCompanion.Core.Hardware.Skalarki;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The Skalarki hardware link (PANELS diagnostics page, 2026-10-27): where the SkalarkiIO
/// Profiler's SDK server is and which boards to watch. The SDK assemblies are the user's
/// (they ship with the Profiler) and are loaded at runtime from <see cref="SdkPath"/> into
/// their own load context — never referenced by the app at compile time and never
/// redistributed (docs/integrations/skalarki.md, ADR-0017).
/// </summary>
public sealed class SkalarkiOptions : IOptionSection
{
    public static string SectionName => "skalarki";

    /// <summary>The Profiler's SDK server port when nothing else is configured: the value
    /// the Profiler itself defaults to (<c>skalarkiServerPORT</c> in its Settings.config); the
    /// SDK has no default of its own.</summary>
    public const int DefaultProfilerPort = 53000;

    /// <summary>Floor for <see cref="ReconnectBackoffMs"/>: a tighter loop hammers a Profiler
    /// that is simply not running.</summary>
    public const int MinReconnectBackoffMs = 1000;

    /// <summary>Off by default: most users have no Skalarki hardware, and off means no PANELS
    /// page, no caution, no log line.</summary>
    [OptionField(FieldKind.Bool, "skalarkiSettings.enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Folder holding <c>SkalarkiIO.SDK.dll</c> and the three support assemblies beside it
    /// (<c>SkalarkiIO.SDK.Telemetry</c>, <c>System.Threading.Tasks.Dataflow</c>,
    /// <c>Microsoft.Diagnostics.Tracing.EventSource</c>). The Profiler's own <c>Tools</c> folder
    /// has all four. Empty keeps the link off with guidance on the page.
    /// </summary>
    [OptionField(FieldKind.Text, "skalarkiSettings.sdkPath", TextKind = TextFieldKind.FolderPath, BrowseStartIn = "SkalarkiSdk", Normalize = FieldNormalization.Trim, DependsOn = "Enabled")]
    public string SdkPath { get; set; } = "";

    /// <summary>The PC running the Profiler (the one the USB boards are plugged into), which
    /// need not be the PC running this app.</summary>
    [OptionField(FieldKind.Text, "skalarkiSettings.host", TextKind = TextFieldKind.Host, Normalize = FieldNormalization.Trim, DependsOn = "Enabled")]
    public string ProfilerHost { get; set; } = "127.0.0.1";

    /// <summary>The Profiler's SDK server port.</summary>
    [OptionField(FieldKind.Number, "skalarkiSettings.port", Min = "1", Max = "65535", DependsOn = "Enabled")]
    public int ProfilerPort { get; set; } = DefaultProfilerPort;

    /// <summary>Which descriptor catalogue the bridge registers for the watched boards.</summary>
    [OptionField(FieldKind.Select, "skalarkiSettings.catalogue", Choices = "skalarkiCatalogue", DependsOn = "Enabled")]
    public SkalarkiCatalogue Catalogue { get; set; } = SkalarkiCatalogue.A32X;

    /// <summary>
    /// The boards to watch, by SDK name (<c>GLARE</c>, <c>MCDU1</c>, …). Empty means every board
    /// the Profiler reports as connected. Edited with the tick boxes on the Skalarki settings
    /// page, which lists the boards the Profiler has reported.
    /// </summary>
    public List<string> EnabledDevices { get; set; } = [];

    /// <summary>
    /// Display names per board, by SDK name ("RMP2" → "RMP FO"). A board without an entry
    /// shows its default avionics label. Edited on the Skalarki settings page, one text field
    /// per board.
    /// </summary>
    public Dictionary<string, string> DeviceLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long LAMP TEST holds every output of the chosen boards on.</summary>
    [OptionField(FieldKind.Number, "skalarkiSettings.lampTestMs", Suffix = "ms", Min = "500", Max = "10000", DependsOn = "Enabled")]
    public int LampTestMs { get; set; } = 2000;

    /// <summary>Pause between connection attempts while the Profiler cannot be reached.</summary>
    [OptionField(FieldKind.Number, "skalarkiSettings.reconnectBackoffMs", Suffix = "ms", Min = "1000", Max = "60000", Advanced = true, DependsOn = "Enabled")]
    public int ReconnectBackoffMs { get; set; } = 5000;

    /// <summary>The configured backoff, never below the floor.</summary>
    public TimeSpan ReconnectBackoff => TimeSpan.FromMilliseconds(Math.Max(MinReconnectBackoffMs, ReconnectBackoffMs));

    /// <summary>The enabled boards as ids; unknown names are ignored.</summary>
    public IReadOnlyList<SkalarkiDeviceId> EnabledDeviceIds()
    {
        var ids = new List<SkalarkiDeviceId>();
        foreach (var name in EnabledDevices)
        {
            if (SkalarkiDevices.TryParse(name, out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>The label for a board: the user's, else the default avionics label.</summary>
    public string LabelFor(SkalarkiDeviceId device)
        => DeviceLabels.TryGetValue(SkalarkiDevices.SdkName(device), out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim()
            : SkalarkiDevices.DefaultLabel(device);
}
