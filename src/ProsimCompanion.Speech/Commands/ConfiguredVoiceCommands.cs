using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Commands;

/// <summary>
/// File-driven voice-command engine (Prosim2FO's VoiceCommandService, clean-rewritten): loads
/// config/commands.json, contributes its phrases to the idle grammar, and on a match either
/// runs the command's momentary-press sequence (write press → hold → write restore → settle)
/// and speaks its confirmation, or — for step-less query commands — speaks the answer with
/// live {token} values (F/O baro, MCDU take-off perf, departure runway). Hot-reloads on file
/// change (300 ms debounce; a malformed file keeps the previous commands). Every step write is
/// validated against the loaded set's file-derived allow-list, which is itself bounded by
/// <see cref="VoiceCommandWriteGate"/> — the file can never widen the write surface beyond
/// FCU push-buttons and MCDU keys. Executions are serialized so two commands (or a quick
/// double-trigger) never interleave their key presses on the MCDU.
/// </summary>
public sealed class ConfiguredVoiceCommands : IVoiceFeature, IDisposable
{
    private const string VoiceTag = "voice.command";

    /// <summary>Hold for the single verify-retry press (issue #109) — deliberately far above
    /// the 150 ms default, so a switch state ProSim samples slowly is still observed.</summary>
    private const int VerifyRetryHoldMs = 600;

    private readonly IProsimDataRefs _dataRefs;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly SpokenTokenSource _tokens;
    private readonly ILogger<ConfiguredVoiceCommands> _logger;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _gate = new();

    private VoiceCommandSet _set = VoiceCommandSet.Empty;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _initialized;

    private readonly IOptionsMonitor<SpeechOptions>? _speech;
    private readonly Core.State.ConfigProblemStore? _configProblems;

    /// <summary>Optional <paramref name="configProblems"/>: a malformed commands.json surfaces
    /// on the web UI (issue #74) instead of living only in the log file.</summary>
    public ConfiguredVoiceCommands(
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        SpokenTokenSource tokens,
        ILogger<ConfiguredVoiceCommands> logger,
        IOptionsMonitor<SpeechOptions>? speech = null,
        Core.State.ConfigProblemStore? configProblems = null)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _tokens = tokens;
        _logger = logger;
        _speech = speech;
        _configProblems = configProblems;
    }

    /// <summary>Seat-relative side of a commands.json dataref (authored for the default
    /// left-seat-human geometry: CDU2 keys, EFIS2 baro). Allow-list checks stay on the
    /// authored name; only the executed side flips.</summary>
    private string Side(string dataref)
        => Recognition.PilotSeatMap.Map(dataref,
            _speech is not null && Recognition.PilotSeatMap.HumanIsRightSeat(_speech.CurrentValue));

    // User tree, not the install dir (ADR-0007) — seeded from shipped defaults at startup.
    private static string CommandsPath
        => Core.Configuration.UserConfigPaths.File("commands.json");

    public bool Enabled => true;

    public IEnumerable<string> Phrases
    {
        get
        {
            EnsureInitialized();
            return CurrentSet.Phrases;
        }
    }

    public bool ValueParse => false;

    /// <summary>Replaces the loaded set directly (tests; also marks the service initialized so
    /// the lazy file load never clobbers an injected set).</summary>
    public void Load(VoiceCommandSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_gate)
        {
            _set = set;
            _initialized = true;
        }
    }

    public bool TryHandle(string utterance)
    {
        EnsureInitialized();
        var set = CurrentSet;
        if (!set.TryResolve(utterance, out var resolved))
        {
            return false;
        }

        _ = ExecuteAsync(set, resolved);
        return true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _executionGate.Dispose();
    }

    private VoiceCommandSet CurrentSet
    {
        get
        {
            lock (_gate)
            {
                return _set;
            }
        }
    }

    private void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }

        ReloadFromFile();
        StartWatching();
    }

    private void ReloadFromFile()
    {
        VoiceCommandParseResult result;
        try
        {
            if (!File.Exists(CommandsPath))
            {
                _logger.LogInformation("No commands.json at {Path}; no configured voice commands loaded", CommandsPath);
                lock (_gate)
                {
                    _set = VoiceCommandSet.Empty;
                }

                _configProblems?.ClearArea(Core.State.ConfigAreas.Commands);
                return;
            }

            result = VoiceCommandConfigParser.Parse(File.ReadAllText(CommandsPath));
        }
        catch (Exception ex)
        {
            // Malformed mid-edit file: keep the previous commands (predecessor behaviour),
            // but say so on the web UI too (issue #74).
            _logger.LogError(ex, "Failed to parse commands.json — keeping previous commands");
            _configProblems?.Report(Core.State.ConfigAreas.Commands, CommandsPath, ex.Message);
            return;
        }

        _configProblems?.ClearArea(Core.State.ConfigAreas.Commands);

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("commands.json: {Warning}", warning);
        }

        lock (_gate)
        {
            _set = result.Set;
        }

        _logger.LogInformation(
            "Loaded {Commands} voice command(s), {Phrases} phrase(s), {Writes} allow-listed dataref(s)",
            result.Set.Commands.Count, result.Set.Phrases.Count, result.Set.WriteAllowList.Count);

        if (result.Set.Commands.Any(c => c.Say.Contains('{')))
        {
            _tokens.Prime();
        }
    }

    private void StartWatching()
    {
        try
        {
            var directory = Path.GetDirectoryName(CommandsPath);
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(directory, "commands.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch commands.json; hot-reload disabled");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Editors fire several events per save — debounce, then reload once the file settles.
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            try
            {
                ReloadFromFile();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "commands.json hot-reload failed");
            }
        }, null, 300, Timeout.Infinite);
    }

    private async Task ExecuteAsync(VoiceCommandSet set, ResolvedVoiceCommand resolved)
    {
        var command = resolved.Definition;
        var label = command.Label;
        try
        {
            if (resolved.BlockReason is not null)
            {
                _logger.LogWarning(
                    "Voice command \"{Command}\" refused: {Reason}", label, resolved.BlockReason);
                await SpeakAsync("Unable — that command is not permitted.").ConfigureAwait(false);
                return;
            }

            if (command.Steps.Count == 0)
            {
                // Spoken query — read cached datarefs, no writes.
                await SpeakAsync(ApplyTokens(command.Say)).ConfigureAwait(false);
                _eventLog.Record("voicecommand.query", new { phrase = label });
                return;
            }

            if (command.Steps.Any(s => VoiceCommandConfigParser.IsPlaceholder(s.Dataref)))
            {
                _logger.LogWarning("Voice command \"{Command}\" has unfilled dataref(s) — not executed", label);
                await SpeakAsync($"{label} is not configured yet.").ConfigureAwait(false);
                return;
            }

            // Serialize: one command's full press sequence completes before the next starts,
            // so rapid triggers queue instead of interleaving on the MCDU.
            async Task RunStepsAsync(int? holdOverrideMs)
            {
                await _executionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _logger.LogInformation(
                        "Executing voice command \"{Command}\" ({Steps} step(s))", label, command.Steps.Count);
                    foreach (var step in command.Steps)
                    {
                        // Belt-and-braces: the write must be on the set's own file-derived
                        // allow-list (which VoiceCommandWriteGate bounded at parse time).
                        if (!set.WriteAllowList.Contains(step.Dataref))
                        {
                            throw new InvalidOperationException(
                                $"Dataref '{step.Dataref}' is not on the loaded voice-command write allow-list.");
                        }

                        // Human pacing (Prosim2FO's Humanize): jittered hold, and a jittered
                        // gap with occasional think pauses below — configured times are the
                        // functional minimums and are never undercut.
                        var humanize = _speech?.CurrentValue.Humanize ?? new HumanizeOptions();
                        await _dataRefs.WriteAsync(Side(step.Dataref), step.Press).ConfigureAwait(false);
                        if (step.Restore is { } restore)
                        {
                            var holdMs = holdOverrideMs ?? (step.HoldMs > 0 ? step.HoldMs : 150);
                            await Task.Delay(HumanTiming.Hold(humanize, holdMs, Random.Shared))
                                .ConfigureAwait(false);
                            await _dataRefs.WriteAsync(Side(step.Dataref), restore).ConfigureAwait(false);
                        }

                        if (step.DelayMs > 0)
                        {
                            await Task.Delay(HumanTiming.Gap(
                                humanize, step.DelayMs, HumanTiming.IsPageKey(step.Dataref), Random.Shared))
                                .ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    _executionGate.Release();
                }
            }

            await RunStepsAsync(null).ConfigureAwait(false);
            _eventLog.Record("voicecommand.executed", new { phrase = label, steps = command.Steps.Count });

            // Cross-check before confirming (issue #49): a press that did not take must
            // yield an honest negative, never a confident readback of a non-event.
            if (command.Verify is { } verify && !VoiceCommandConfigParser.IsPlaceholder(verify.Dataref))
            {
                var verified = await VerifyEffectAsync(verify).ConfigureAwait(false);
                var retried = false;
                if (!verified && command.Steps.Count == 1 && command.Steps[0].Restore is not null)
                {
                    // One retry with a deliberately long hold (issue #109): three "set
                    // standard" presses at the standard 150 ms hold never latched STD on the
                    // 2026-08-23 flight — ProSim plausibly never sampled the pressed state.
                    // Single-press commands only: re-running an MCDU key sequence would
                    // double-type it.
                    retried = true;
                    _logger.LogInformation(
                        "Voice command \"{Command}\" verify failed — retrying once with a {RetryHoldMs} ms hold",
                        label, VerifyRetryHoldMs);
                    await RunStepsAsync(VerifyRetryHoldMs).ConfigureAwait(false);
                    verified = await VerifyEffectAsync(verify).ConfigureAwait(false);
                }

                _eventLog.Record("voicecommand.verified", new
                {
                    phrase = label,
                    dataref = verify.Dataref,
                    expected = verify.Expected,
                    ok = verified,
                    retried,
                });
                if (!verified)
                {
                    _logger.LogWarning(
                        "Voice command \"{Command}\" verify failed: {Dataref} did not reach {Expected} within {TimeoutMs} ms",
                        label, verify.Dataref, verify.Expected, verify.TimeoutMs);
                    await SpeakAsync(string.IsNullOrWhiteSpace(verify.SayOnFail)
                        ? $"Negative — {label} did not take effect."
                        : ApplyTokens(verify.SayOnFail)).ConfigureAwait(false);
                    return;
                }
            }

            await SpeakAsync(ApplyTokens(command.Say)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice command \"{Command}\" failed", label);
            await SpeakAsync("Unable — see the log.").ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the verify dataref to read the expected value. A short-lived
    /// dynamic subscription (the names are user-authored, no compile-time descriptor can
    /// exist) polled on the cached value — never a network round-trip per read.</summary>
    private async Task<bool> VerifyEffectAsync(VoiceCommandVerify verify)
    {
        using var read = _dataRefs.SubscribeDynamic(Side(verify.Dataref), Core.Aircraft.DataRefTier.Frequent);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Math.Clamp(verify.TimeoutMs, 250, 10_000));
        while (DateTimeOffset.UtcNow <= deadline)
        {
            if (read.RawValue is not null
                && Math.Abs(read.GetValue(double.NaN) - verify.Expected) < 0.5)
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return false;
    }

    private async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(1), Tag: VoiceTag))
            .ConfigureAwait(false);
    }

    private string ApplyTokens(string text) => _tokens.Apply(text);
}
