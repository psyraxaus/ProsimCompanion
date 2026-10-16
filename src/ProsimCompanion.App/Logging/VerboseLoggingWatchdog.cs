using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;

namespace ProsimCompanion.App.Logging;

/// <summary>
/// Turns verbose logging off again after <see cref="VerboseLoggingPolicy.Window"/> (owner
/// pick 2026-10-17): a Debug level or the wire trace is for a hunt, and the sim PC had been
/// left on Debug with the wire trace on for days (50 MB of frames per flight day). The start
/// of the verbose period is kept in <c>state\verbose-logging.json</c> so restarts do not
/// reset the clock. Reverting writes the quiet values through <see cref="SettingsWriter"/>,
/// the same path the Logs page uses, so the file and every open page follow at once.
/// <c>logging.keepVerbose</c> stops the clock.
/// </summary>
public sealed class VerboseLoggingWatchdog : BackgroundService, IVerboseLoggingStatus
{
    private readonly IOptionsMonitor<LoggingOptions> _options;
    private readonly SettingsWriter _writer;
    private readonly ILogger<VerboseLoggingWatchdog> _logger;
    private readonly string _stateFile;
    private readonly object _gate = new();
    private DateTimeOffset? _since;
    private Timer? _timer;
    private bool _keptNoted;

    public VerboseLoggingWatchdog(
        IOptionsMonitor<LoggingOptions> options,
        SettingsWriter writer,
        ILogger<VerboseLoggingWatchdog> logger)
        : this(options, writer, logger, Path.Combine(UserDataPaths.Root, "state", "verbose-logging.json"))
    {
    }

    internal VerboseLoggingWatchdog(
        IOptionsMonitor<LoggingOptions> options,
        SettingsWriter writer,
        ILogger<VerboseLoggingWatchdog> logger,
        string stateFile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        _options = options;
        _writer = writer;
        _logger = logger;
        _stateFile = stateFile;
    }

    /// <inheritdoc />
    public DateTimeOffset? VerboseSinceUtc
    {
        get
        {
            lock (_gate)
            {
                return _since;
            }
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? RevertAtUtc => VerboseLoggingPolicy.RevertAt(_options.CurrentValue, VerboseSinceUtc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Evaluate();
        using var subscription = _options.OnChange(_ => Evaluate());
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    private void Evaluate()
    {
        try
        {
            var options = _options.CurrentValue;
            var now = DateTimeOffset.UtcNow;
            if (!VerboseLoggingPolicy.IsVerbose(options))
            {
                ClearSince();
                _keptNoted = false;
                return;
            }

            var since = EnsureSince(now);
            if (options.KeepVerbose)
            {
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                if (!_keptNoted)
                {
                    _keptNoted = true;
                    _logger.LogInformation(
                        "Verbose logging is kept on by logging.keepVerbose (default {Level}, wire trace {WireTrace}); the log files grow accordingly",
                        options.DefaultLevel, options.WireTrace);
                }

                return;
            }

            var revertAt = since + VerboseLoggingPolicy.Window;
            if (revertAt <= now)
            {
                Revert(options, since);
                return;
            }

            // One shot at the deadline; re-armed on every options change. Capped at a day so a
            // clock jump can never park it in the far future.
            var delay = revertAt - now;
            if (delay > VerboseLoggingPolicy.Window)
            {
                delay = VerboseLoggingPolicy.Window;
            }

            _timer ??= new Timer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
            _logger.LogInformation(
                "Verbose logging is on (default {Level}, wire trace {WireTrace}) since {Since}; it switches back to Information at {RevertAt} unless logging.keepVerbose is set",
                options.DefaultLevel, options.WireTrace, since.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                revertAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Verbose-logging watchdog could not read or write its state file {File}", _stateFile);
        }
    }

    private void Revert(LoggingOptions options, DateTimeOffset since)
    {
        var quiet = VerboseLoggingPolicy.Quiet(options);
        _logger.LogWarning(
            "Verbose logging had been on since {Since} (over {Hours} h): default level back to {Level}, wire trace off. Set logging.keepVerbose on the Logs page to keep it",
            since.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            (int)VerboseLoggingPolicy.Window.TotalHours, quiet.DefaultLevel);
        ClearSince();
        _writer.Set<LoggingOptions, string>(o => o.DefaultLevel, quiet.DefaultLevel);
        _writer.Set<LoggingOptions, bool>(o => o.WireTrace, false);
        if (quiet.Overrides.Count != options.Overrides.Count)
        {
            _writer.Set<LoggingOptions, Dictionary<string, string>>(o => o.Overrides, quiet.Overrides);
        }
    }

    private DateTimeOffset EnsureSince(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_since is { } known)
            {
                return known;
            }

            var stored = ReadState();
            _since = stored ?? now;
            if (stored is null)
            {
                WriteState(_since.Value);
            }

            return _since.Value;
        }
    }

    private void ClearSince()
    {
        lock (_gate)
        {
            _since = null;
        }

        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (File.Exists(_stateFile))
        {
            File.Delete(_stateFile);
        }
    }

    private DateTimeOffset? ReadState()
    {
        if (!File.Exists(_stateFile))
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile));
        return state?.SinceUtc;
    }

    private void WriteState(DateTimeOffset since)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(new State(since)));
    }

    private sealed record State(DateTimeOffset SinceUtc);
}
