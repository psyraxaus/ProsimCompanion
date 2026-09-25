namespace ProsimCompanion.Core.Diagnostics;

/// <summary>
/// The running build, answered once and shared: version, commit, runtime and OS. Every
/// artefact a user might send in for support — the session event log, the CMTrace log, the
/// diagnostics bundle, the "copy version" button — stamps itself with this, so a report can
/// be attributed to an exact build without asking. The update banner and the telemetry
/// summary read the same object, which is why the version can never disagree between them.
/// </summary>
public interface IAppBuildInfo
{
    /// <summary>Semantic version without build metadata ("0.5.0-rc.2").</summary>
    string Version { get; }

    /// <summary>The full assembly informational version, build metadata included
    /// ("0.5.0-rc.2+1a2b3c4" when the build carried a source revision).</summary>
    string InformationalVersion { get; }

    /// <summary>The source revision after the '+' of the informational version; null when the
    /// build carried none.</summary>
    string? Commit { get; }

    /// <summary>The runtime the process runs on (".NET 10.0.1").</summary>
    string Runtime { get; }

    /// <summary>The operating system description ("Microsoft Windows 10.0.19045").</summary>
    string Os { get; }

    /// <summary>The process architecture ("X64").</summary>
    string Architecture { get; }

    /// <summary>One human-readable line naming the build and its host — the text a user
    /// pastes into a GitHub issue and the first line of the bundle's versions.txt.</summary>
    string Describe();
}
