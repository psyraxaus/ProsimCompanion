namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Canonical locations of the app-WRITTEN user data (session event logs, rolling app logs) —
/// the sibling of <see cref="UserConfigPaths"/>, which owns the user-EDITED config tree.
/// Introduced with the telemetry API (issue #94): the sessions and logs directories were
/// previously composed inline at each writer's registration, and a reader in another project
/// had no single place to agree with them.
/// </summary>
public static class UserDataPaths
{
    /// <summary>The app's LocalApplicationData root (logs, sessions, logbook, tech log …).</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProsimCompanion");

    /// <summary>JSONL session event logs (<c>session-*.jsonl</c>), written by JsonlEventLog.</summary>
    public static string Sessions => Path.Combine(Root, "sessions");

    /// <summary>Rolling CMTrace app + wire logs, written by the Serilog file sinks.</summary>
    public static string Logs => Path.Combine(Root, "logs");
}
