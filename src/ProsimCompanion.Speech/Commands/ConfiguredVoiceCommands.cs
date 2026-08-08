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

    // F/O (EFIS2) baro — the copilot's side, carried from Prosim2FO's CalloutValues.
    // B_FCU_EFIS2_BARO_STD is the boolean STD *state* gate (the S_… switch is the momentary).
    private const string FoBaroStdRef = "system.gates.B_FCU_EFIS2_BARO_STD";
    private const string FoBaroModeRef = ProsimDataRefNames.Efis2BaroMode; // 0:inHg 1:hPa
    private const string FoBaroHpaRef = ProsimDataRefNames.Efis2BaroHpa;
    private const string FoBaroInchRef = ProsimDataRefNames.Efis2BaroInch;

    private const string V1Ref = "aircraft.fms.perf.takeOff.v1";
    private const string VrRef = "aircraft.fms.perf.takeOff.vr";
    private const string V2Ref = "aircraft.fms.perf.takeOff.v2";
    private const string FlexRef = "aircraft.fms.perf.takeOff.flexTemp";

    private readonly IProsimDataRefs _dataRefs;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<BriefingOptions> _briefingOptions;
    private readonly ILogger<ConfiguredVoiceCommands> _logger;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _gate = new();

    private VoiceCommandSet _set = VoiceCommandSet.Empty;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _initialized;

    private readonly IOptionsMonitor<SpeechOptions>? _speech;

    public ConfiguredVoiceCommands(
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        IOptionsMonitor<BriefingOptions> briefingOptions,
        ILogger<ConfiguredVoiceCommands> logger,
        IOptionsMonitor<SpeechOptions>? speech = null)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(briefingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _briefingOptions = briefingOptions;
        _logger = logger;
        _speech = speech;
    }

    /// <summary>Seat-relative side of a commands.json dataref (authored for the default
    /// left-seat-human geometry: CDU2 keys, EFIS2 baro). Allow-list checks stay on the
    /// authored name; only the executed side flips.</summary>
    private string Side(string dataref)
        => Recognition.PilotSeatMap.Map(dataref,
            _speech is not null && Recognition.PilotSeatMap.HumanIsRightSeat(_speech.CurrentValue));

    private static string CommandsPath
        => Path.Combine(AppContext.BaseDirectory, "config", "commands.json");

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
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
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

                return;
            }

            result = VoiceCommandConfigParser.Parse(File.ReadAllText(CommandsPath));
        }
        catch (Exception ex)
        {
            // Malformed mid-edit file: keep the previous commands (predecessor behaviour).
            _logger.LogError(ex, "Failed to parse commands.json — keeping previous commands");
            return;
        }

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
            PrimeTokenSubscriptions();
        }
    }

    /// <summary>Registers the token datarefs up front so the caches are warm by the time a
    /// spoken query fires — a lazy first subscribe would answer "unavailable" once.</summary>
    private void PrimeTokenSubscriptions()
    {
        try
        {
            Sub(FoBaroStdRef, DataRefTier.Normal);
            Sub(FoBaroModeRef, DataRefTier.Normal);
            Sub(FoBaroHpaRef, DataRefTier.Normal);
            Sub(FoBaroInchRef, DataRefTier.Normal);
            Sub(V1Ref, DataRefTier.Infrequent);
            Sub(VrRef, DataRefTier.Infrequent);
            Sub(V2Ref, DataRefTier.Infrequent);
            Sub(FlexRef, DataRefTier.Infrequent);
        }
        catch (Exception ex)
        {
            // Degraded mode (ProSim absent) — queries answer "unavailable" until it returns.
            _logger.LogDebug(ex, "Voice-command token subscriptions unavailable");
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

                    await _dataRefs.WriteAsync(Side(step.Dataref), step.Press).ConfigureAwait(false);
                    if (step.Restore is { } restore)
                    {
                        await Task.Delay(step.HoldMs > 0 ? step.HoldMs : 150).ConfigureAwait(false);
                        await _dataRefs.WriteAsync(Side(step.Dataref), restore).ConfigureAwait(false);
                    }

                    if (step.DelayMs > 0)
                    {
                        await Task.Delay(step.DelayMs).ConfigureAwait(false);
                    }
                }

                _eventLog.Record("voicecommand.executed", new { phrase = label, steps = command.Steps.Count });
            }
            finally
            {
                _executionGate.Release();
            }

            await SpeakAsync(ApplyTokens(command.Say)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice command \"{Command}\" failed", label);
            await SpeakAsync("Unable — see the log.").ConfigureAwait(false);
        }
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

    private string ApplyTokens(string text)
        => string.IsNullOrEmpty(text) || !text.Contains('{')
            ? text
            : SpokenValueFormatting.ApplyTokens(text, BuildTokenValues());

    /// <summary>Snapshots the token values from the cached subscriptions (registered once on
    /// first use — never a per-read round-trip).</summary>
    private CommandTokenValues BuildTokenValues()
    {
        try
        {
            var std = Sub(FoBaroStdRef, DataRefTier.Normal);
            bool? stdState = std.RawValue is null ? null : (bool?)std.GetValue(false);
            var hpaMode = Sub(FoBaroModeRef, DataRefTier.Normal).GetValue(1) == 1;
            var hpa = Sub(FoBaroHpaRef, DataRefTier.Normal).GetValue(0.0);
            var inches = Sub(FoBaroInchRef, DataRefTier.Normal).GetValue(0.0);

            var runway = FlightJsonRouteReader.Read().DepartureRunway;
            if (string.IsNullOrWhiteSpace(runway))
            {
                runway = _briefingOptions.CurrentValue.DepartureRunway;
            }

            return new CommandTokenValues(
                Altimeter: SpokenValueFormatting.Altimeter(stdState, hpaMode, hpa, inches),
                Qnh: SpokenValueFormatting.Altimeter(stdState, hpaMode: true, hpa, inches),
                V1: SpokenValueFormatting.Speed(Perf(V1Ref)),
                Vr: SpokenValueFormatting.Speed(Perf(VrRef)),
                V2: SpokenValueFormatting.Speed(Perf(V2Ref)),
                Flex: SpokenValueFormatting.Speed(Perf(FlexRef)),
                Runway: SpokenValueFormatting.Runway(runway));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice-command token snapshot failed");
            return CommandTokenValues.Unavailable;
        }
    }

    private double? Perf(string dataref)
    {
        var sub = Sub(dataref, DataRefTier.Infrequent);
        return sub.RawValue is null ? null : sub.GetValue(0.0);
    }

    private IDataRefSubscription Sub(string dataref, DataRefTier tier)
    {
        lock (_reads)
        {
            // Seat-relative reads (EFIS baro etc.); cached under the MAPPED name so a seat
            // change picks up the other side on the next new subscription.
            dataref = Side(dataref);
            if (!_reads.TryGetValue(dataref, out var read))
            {
                read = _dataRefs.Subscribe(dataref, tier);
                _reads[dataref] = read;
            }

            return read;
        }
    }
}
