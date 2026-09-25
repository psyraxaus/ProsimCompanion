using ProsimCompanion.Core.Diagnostics;

namespace ProsimCompanion.Core.EventLog;

/// <summary>
/// The payload of <c>session-started</c> and <c>session-rotated</c>: the first line of every
/// session file names the build that wrote it, so a support bundle (or a stray file mailed
/// in) is attributable without asking — the build canary the flight-verification workflow
/// used to infer from event shapes. camelCase like every other payload.
/// </summary>
public sealed record SessionHeader(
    string AppVersion,
    string InformationalVersion,
    string? Commit,
    string Runtime,
    string Os,
    string Architecture,
    string SessionFile,
    double SampleIntervalSeconds,
    string? ActiveProfile)
{
    /// <summary>Event name written when a session file is opened at startup.</summary>
    public const string StartedEvent = "session-started";

    /// <summary>Event name written as the first line of a file opened by a mid-run rotation
    /// (company day mode) — the same payload, so multi-leg files are self-describing too.</summary>
    public const string RotatedEvent = "session-rotated";

    /// <summary>
    /// The Serilog banner template. CMTrace lines carry no properties, so this one line is how
    /// a rolling log file gets attributed to a build; it is logged at startup and again on
    /// every session rotation so a daily file that begins mid-session still carries it.
    /// </summary>
    public const string BannerTemplate =
        "ProsimCompanion {Version} ({Commit}) starting on {Os} / {Runtime}; session log {SessionFile}";

    /// <summary>Builds the payload for <paramref name="sessionPath"/> (file name only is
    /// recorded — the directory is the user's profile path, which is not diagnostic).</summary>
    public static SessionHeader Create(
        IAppBuildInfo build,
        string sessionPath,
        double sampleIntervalSeconds,
        string? activeProfile)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(sessionPath);
        return new SessionHeader(
            build.Version,
            build.InformationalVersion,
            build.Commit,
            build.Runtime,
            build.Os,
            build.Architecture,
            Path.GetFileName(sessionPath),
            sampleIntervalSeconds,
            activeProfile);
    }
}
