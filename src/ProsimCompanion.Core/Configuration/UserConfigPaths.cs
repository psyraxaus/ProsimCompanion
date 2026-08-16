namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Canonical locations of the USER-EDITABLE config content (checklists, abnormals, voice
/// commands, phrase pools, ATC requests). These live under
/// <c>%LOCALAPPDATA%\ProsimCompanion\config</c> — deliberately OUTSIDE the install directory
/// (ADR-0007, issue #55): editing files under an installer-managed folder cost a flight test
/// when the edits landed in a look-alike copy, and an installer update can legitimately
/// overwrite anything beside the exe. The shipped defaults still deploy to
/// <c>{app}\config</c>; <see cref="UserConfigSeeder"/> mirrors them here with
/// keep-user-edits semantics at every startup. <c>settings.json</c> is NOT part of this —
/// it stays beside the exe (installer-owned keys, WPF lockout prevention).
/// </summary>
public static class UserConfigPaths
{
    /// <summary>Root of the user-editable config tree, created by the seeder on startup.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProsimCompanion",
        "config");

    /// <summary>Per-phase checklist files plus the <c>sets</c> subfolder.</summary>
    public static string Checklists => Path.Combine(Root, "checklists");

    /// <summary>ECAM abnormal procedure + memory drill definitions.</summary>
    public static string Abnormals => Path.Combine(Root, "abnormals");

    /// <summary>Expected-aircraft-state definitions (cold-and-dark switch set, issue #63).</summary>
    public static string AircraftStates => Path.Combine(Root, "aircraft-states");

    /// <summary>Resolves a file directly under the user config root (e.g. <c>commands.json</c>).</summary>
    public static string File(string relativePath) => Path.Combine(Root, relativePath);
}
