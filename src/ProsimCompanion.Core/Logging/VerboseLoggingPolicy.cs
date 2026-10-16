using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Logging;

/// <summary>
/// The pure rules behind "verbose logging switches itself off" (owner pick 2026-10-17). A
/// Debug/Verbose level or the wire trace is a hunt, not a daily setting: the 2026-10-10/11
/// flight bundle carried 50 MB of wire frames and 20 000 Debug lines a day because the sim PC
/// had been left on Debug. After <see cref="Window"/> the app returns to Information unless
/// <see cref="LoggingOptions.KeepVerbose"/> says otherwise. The watchdog in the App project
/// owns the clock and the settings write; everything decidable without them lives here.
/// </summary>
public static class VerboseLoggingPolicy
{
    /// <summary>How long a verbose setting may stay on by itself.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>The quiet level the app returns to.</summary>
    public const string QuietLevel = "Information";

    /// <summary>True for the two levels below Information (Serilog names, any casing).</summary>
    public static bool IsVerboseLevel(string? level)
        => level is not null
            && (level.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                || level.Equals("Verbose", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when anything in the options produces a verbose stream: the default
    /// level, any per-source override, or the wire trace.</summary>
    public static bool IsVerbose(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.WireTrace
            || IsVerboseLevel(options.DefaultLevel)
            || options.Overrides.Values.Any(IsVerboseLevel);
    }

    /// <summary>When the watchdog will turn the verbose setting off, or null when nothing is
    /// verbose, the pilot asked to keep it, or the start is unknown.</summary>
    public static DateTimeOffset? RevertAt(LoggingOptions options, DateTimeOffset? verboseSinceUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        return verboseSinceUtc is { } since && IsVerbose(options) && !options.KeepVerbose
            ? since + Window
            : null;
    }

    /// <summary>The same options with every verbose stream closed: Information for a verbose
    /// default, verbose overrides dropped (they fall back to the default), wire trace off.
    /// Everything else is carried over unchanged.</summary>
    public static LoggingOptions Quiet(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, level) in options.Overrides)
        {
            if (!IsVerboseLevel(level))
            {
                overrides[source] = level;
            }
        }

        return new LoggingOptions
        {
            DefaultLevel = IsVerboseLevel(options.DefaultLevel) ? QuietLevel : options.DefaultLevel,
            Overrides = overrides,
            WireTrace = false,
            KeepVerbose = options.KeepVerbose,
            MirrorToSession = options.MirrorToSession,
        };
    }

    /// <summary>True when two option sets drive the log pipeline the same way — the levels,
    /// the overrides, the wire trace and the session mirror. A settings save that touched
    /// another section reloads the options object without changing any of these, and the
    /// "levels reloaded" line is only worth writing when they did (55 of them on 2026-10-17).</summary>
    public static bool SameLevels(LoggingOptions a, LoggingOptions b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!string.Equals(a.DefaultLevel, b.DefaultLevel, StringComparison.OrdinalIgnoreCase)
            || a.WireTrace != b.WireTrace
            || a.MirrorToSession != b.MirrorToSession
            || a.Overrides.Count != b.Overrides.Count)
        {
            return false;
        }

        foreach (var (source, level) in a.Overrides)
        {
            if (!b.Overrides.TryGetValue(source, out var other)
                || !string.Equals(level, other, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
