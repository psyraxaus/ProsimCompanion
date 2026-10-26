namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the "Export diagnostics" support bundle (Logs page). The bundle is the one
/// artefact a user sends in with a bug report: the newest session event logs, the newest
/// CMTrace app logs, a redacted copy of settings.json and the version banner, zipped with a
/// manifest of SHA-256 hashes. Sizes are bounded here so a bundle stays mailable.
/// </summary>
public sealed class DiagnosticsOptions : IOptionSection
{
    public static string SectionName => "diagnostics";

    /// <summary>Hard bounds for <see cref="BundleSessionCount"/>.</summary>
    public const int MinSessions = 1;
    public const int MaxSessions = 20;

    /// <summary>Hard bounds for <see cref="BundleLogDays"/>.</summary>
    public const int MinLogDays = 1;
    public const int MaxLogDays = 14;

    /// <summary>How many of the newest <c>session-*.jsonl</c> files go into the bundle. Five
    /// covers a company day plus the restart-before-the-bug that support usually asks for.</summary>
    public int BundleSessionCount { get; set; } = 5;

    /// <summary>How many of the newest daily CMTrace app logs go into the bundle.</summary>
    public int BundleLogDays { get; set; } = 3;

    /// <summary>
    /// Off by default: the wire trace (raw GSX / gateway frames) reached 24 MB in one smoke
    /// test and is only useful when a protocol question is on the table.
    /// </summary>
    public bool IncludeWireTrace { get; set; }

    /// <summary>Floor for <see cref="EcamTickMs"/>: four snapshots a second is as fast as a
    /// human reads a page, and every tick walks every store.</summary>
    public const int MinEcamTickMs = 250;

    /// <summary>Ceiling for <see cref="EcamTickMs"/>.</summary>
    public const int MaxEcamTickMs = 10_000;

    /// <summary>How often the ECAM-style diagnostics pages rebuild their snapshot. One second
    /// matches the aircraft displays; lower only when chasing a flapping link.</summary>
    [OptionField(FieldKind.Number, "diagnostics.ecamTickMs", Suffix = "ms", Min = "250", Max = "10000", Advanced = true)]
    public int EcamTickMs { get; set; } = 1000;

    /// <summary>
    /// The host labels printed above the VOICE page pipeline nodes, keyed by the host part of
    /// a configured STT / LLM / TTS URL ("10.0.1.30" → "MAC MINI"). A host with no entry shows
    /// its address. Nothing is assumed about which machine runs what: the defaults are the
    /// addresses themselves and the user renames them on the Diagnostics settings page.
    /// </summary>
    public Dictionary<string, string> EcamHostLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
