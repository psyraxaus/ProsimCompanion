using ProsimCompanion.Core.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace ProsimCompanion.App.Logging;

/// <summary>
/// The live level switches behind the Serilog pipeline. <see cref="Apply"/> is called at startup
/// and again on every settings change, so levels (and the wire-trace flag) adjust mid-session
/// without a restart.
/// </summary>
public sealed class LoggingLevels
{
    /// <summary>Application sources that follow <see cref="LoggingOptions.DefaultLevel"/> unless
    /// individually overridden.</summary>
    private static readonly string[] AppSources =
    [
        "ProsimCompanion.App",
        "ProsimCompanion.Core",
        "ProsimCompanion.Gsx",
        "ProsimCompanion.Prosim",
        "ProsimCompanion.Sim",
        "ProsimCompanion.Web",
    ];

    /// <summary>Framework sources that default to Warning to keep request noise out of the logs.</summary>
    private static readonly string[] FrameworkSources =
    [
        "Microsoft",
        "System.Net.Http",
    ];

    private readonly Dictionary<string, LoggingLevelSwitch> _switches;
    private volatile bool _wireTraceEnabled;

    public LoggingLevels()
    {
        _switches = new Dictionary<string, LoggingLevelSwitch>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in AppSources)
        {
            _switches[source] = new LoggingLevelSwitch(LogEventLevel.Information);
        }
        foreach (var source in FrameworkSources)
        {
            _switches[source] = new LoggingLevelSwitch(LogEventLevel.Warning);
        }
    }

    /// <summary>Master switch for sources without a namespace override.</summary>
    public LoggingLevelSwitch DefaultLevel { get; } = new(LogEventLevel.Information);

    /// <summary>All per-source switches, for wiring into MinimumLevel.Override.</summary>
    public IReadOnlyDictionary<string, LoggingLevelSwitch> SourceSwitches => _switches;

    /// <summary>Current wire-trace flag (see <see cref="Core.Logging.IWireTrace"/>).</summary>
    public bool WireTraceEnabled => _wireTraceEnabled;

    /// <summary>Applies options to the live switches. Unknown override keys are ignored (the
    /// switch set is fixed at the subsystem-namespace granularity).</summary>
    public void Apply(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var defaultLevel = ParseLevel(options.DefaultLevel, LogEventLevel.Information);
        DefaultLevel.MinimumLevel = defaultLevel;

        foreach (var source in AppSources)
        {
            _switches[source].MinimumLevel = options.Overrides.TryGetValue(source, out var configured)
                ? ParseLevel(configured, defaultLevel)
                : defaultLevel;
        }

        foreach (var source in FrameworkSources)
        {
            _switches[source].MinimumLevel = options.Overrides.TryGetValue(source, out var configured)
                ? ParseLevel(configured, LogEventLevel.Warning)
                : LogEventLevel.Warning;
        }

        _wireTraceEnabled = options.WireTrace;
    }

    private static LogEventLevel ParseLevel(string? text, LogEventLevel fallback)
        => Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var level) ? level : fallback;
}
