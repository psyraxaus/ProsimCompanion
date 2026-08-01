namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Runtime-adjustable logging configuration (hot-reloaded — no restart needed). Levels use
/// Serilog names: Verbose, Debug, Information, Warning, Error, Fatal.
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "logging";

    /// <summary>Minimum level for all ProsimCompanion sources without an override.</summary>
    public string DefaultLevel { get; set; } = "Information";

    /// <summary>
    /// Per-source overrides, keyed by namespace prefix (e.g. "ProsimCompanion.Prosim": "Debug").
    /// Framework sources ("Microsoft", "Microsoft.AspNetCore") default to Warning unless
    /// overridden here.
    /// </summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, raw protocol traffic (GraphQL bodies, GSX Remote API frames) is written to the
    /// separate wire-trace CMTrace file. Off by default — verbose by design.
    /// </summary>
    public bool WireTrace { get; set; }
}
