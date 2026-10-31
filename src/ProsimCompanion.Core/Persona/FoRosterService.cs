using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Persona;

/// <summary>
/// <see cref="IFoRosterService"/> over <c>fo-roster.json</c> in the USER config tree (ADR-0007)
/// and <c>fo-roster-state.json</c> under the data root. The file is re-read on the next access
/// after its last-write time moved (the <c>PhraseBank</c> pattern — no watcher thread); an
/// unparseable file keeps the last good pool and surfaces on the Config page. Pin and Release
/// also flip <c>persona.roster.mode</c> through the typed settings writer, so the page's mode
/// dropdown and the card agree. Every write is one line in the session log.
/// </summary>
public sealed class FoRosterService : IFoRosterService
{
    public const string FileName = "fo-roster.json";

    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly FoRosterStateFile _stateFile;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoRosterService> _logger;
    private readonly ConfigProblemStore? _problems;
    private readonly SettingsWriter? _settings;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Random _random = new();

    private IReadOnlyList<FoRosterEntry> _pool = [];
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private FoRosterState _state;

    /// <param name="configDirectory">Where <c>fo-roster.json</c> lives; null = the user config root.</param>
    /// <param name="settings">The typed settings writer for the mode flip on pin/release; null
    /// (tests) leaves the mode alone.</param>
    public FoRosterService(
        IOptionsMonitor<PersonaOptions> options,
        FoRosterStateFile stateFile,
        JsonlEventLog eventLog,
        ILogger<FoRosterService> logger,
        ConfigProblemStore? problems = null,
        SettingsWriter? settings = null,
        string? configDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _stateFile = stateFile;
        _eventLog = eventLog;
        _logger = logger;
        _problems = problems;
        _settings = settings;
        _path = Path.Combine(string.IsNullOrWhiteSpace(configDirectory) ? UserConfigPaths.Root : configDirectory, FileName);
        _state = _stateFile.Load();
    }

    /// <summary>The dice, injectable for deterministic tests: a non-negative integer below the
    /// bound. Null = <see cref="Random"/>.</summary>
    public Func<int, int>? Next { get; init; }

    public event Action? Changed;

    public string FilePath => _path;

    public bool IsActive
    {
        get
        {
            var options = _options.CurrentValue;
            return options.Roster.IsActive(options.Enabled);
        }
    }

    public IReadOnlyList<FoRosterEntry> Pool
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChangedLocked();
                return _pool;
            }
        }
    }

    public FoRosterState State
    {
        get
        {
            lock (_gate)
            {
                return Copy(_state);
            }
        }
    }

    public FoRosterEntry? Current
    {
        get
        {
            if (!IsActive)
            {
                return null;
            }

            lock (_gate)
            {
                ReloadIfChangedLocked();
                var id = _options.CurrentValue.Roster.IsPinned ? _state.PinnedId : _state.CurrentId;
                return Find(id);
            }
        }
    }

    public FoRosterEntry? Draw(string reason, DateTimeOffset nowUtc, string? drawnFor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        FoRosterEntry? picked;
        string? previous;
        lock (_gate)
        {
            ReloadIfChangedLocked();
            previous = _state.CurrentId;
            picked = FoRosterDraw.Pick(_pool, previous, Next ?? DefaultNext);
            if (picked is null)
            {
                _logger.LogWarning("FO roster draw requested ({Reason}) but {Path} holds no officers", reason, _path);
                return null;
            }

            _state.PreviousId = previous;
            _state.CurrentId = picked.Id;
            _state.DrawnFor = drawnFor ?? _state.DrawnFor;
            _state.IntroDone = false;
            _stateFile.Save(_state);
        }

        _eventLog.Record("persona.rosterDraw", new
        {
            id = picked.Id,
            reason,
            name = picked.Name,
            experience = picked.Experience,
            formality = picked.Formality,
            chattiness = picked.Chattiness,
            confidence = picked.Confidence,
            hoursOnType = picked.HoursOnType,
            voice = picked.Voice,
            previousId = previous,
            drawnFor,
            at = nowUtc,
        });
        _logger.LogInformation("FO roster: {Name} ({Id}) drawn for {DrawnFor} ({Reason})", picked.Name, picked.Id, drawnFor ?? "the current day", reason);
        Changed?.Invoke();
        return picked;
    }

    public void AdoptDay(string drawnFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawnFor);
        lock (_gate)
        {
            if (string.Equals(_state.DrawnFor, drawnFor, StringComparison.Ordinal))
            {
                return;
            }

            _state.DrawnFor = drawnFor;
            _state.IntroDone = false;
            _stateFile.Save(_state);
        }

        Changed?.Invoke();
    }

    public bool Pin(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        FoRosterEntry? entry;
        lock (_gate)
        {
            ReloadIfChangedLocked();
            entry = Find(id);
            if (entry is null)
            {
                return false;
            }

            if (!string.Equals(_state.CurrentId, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                _state.PreviousId = _state.CurrentId;
                _state.CurrentId = entry.Id;
                _state.IntroDone = false; // a new person greets once
            }

            _state.PinnedId = entry.Id;
            _stateFile.Save(_state);
        }

        WriteMode(PersonaRosterOptions.ModePinned);
        _eventLog.Record("persona.rosterPin", new { id = entry.Id, name = entry.Name });
        _logger.LogInformation("FO roster: {Name} ({Id}) pinned", entry.Name, entry.Id);
        Changed?.Invoke();
        return true;
    }

    public void Release()
    {
        string? released;
        lock (_gate)
        {
            released = _state.PinnedId;
            _state.PinnedId = null;
            _stateFile.Save(_state);
        }

        WriteMode(PersonaRosterOptions.ModeDaily);
        _eventLog.Record("persona.rosterRelease", new { id = released });
        _logger.LogInformation("FO roster: pin released ({Id})", released ?? "none");
        Changed?.Invoke();
    }

    public void MarkIntroSpoken()
    {
        lock (_gate)
        {
            if (_state.IntroDone)
            {
                return;
            }

            _state.IntroDone = true;
            _stateFile.Save(_state);
        }
    }

    private int DefaultNext(int bound)
    {
        lock (_random)
        {
            return _random.Next(bound);
        }
    }

    private FoRosterEntry? Find(string? id)
        => id is null ? null : _pool.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    private void WriteMode(string mode)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            _settings.Set<PersonaOptions, string>(o => o.Roster.Mode, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write persona.roster.mode = {Mode} to settings.json", mode);
        }
    }

    private void ReloadIfChangedLocked()
    {
        DateTime writeTime;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(_path); // MinValue-ish when absent
        }
        catch (IOException)
        {
            return;
        }

        if (writeTime == _loadedWriteTimeUtc)
        {
            return;
        }

        _loadedWriteTimeUtc = writeTime;
        if (!File.Exists(_path))
        {
            _pool = [];
            _problems?.ClearArea(ConfigAreas.FoRoster);
            _logger.LogInformation("FO roster file {Path} is absent — no officers to draw from", _path);
            return;
        }

        try
        {
            var result = FoRosterFile.Parse(File.ReadAllText(_path));
            _pool = result.Officers;
            if (result.Problems.Count > 0)
            {
                _problems?.Report(ConfigAreas.FoRoster, _path, string.Join("; ", result.Problems));
                _logger.LogWarning("fo-roster.json: {Problems}", string.Join("; ", result.Problems));
            }
            else
            {
                _problems?.ClearArea(ConfigAreas.FoRoster);
            }

            _logger.LogInformation("fo-roster.json loaded ({Count} officers)", _pool.Count);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            _logger.LogWarning(ex, "fo-roster.json unreadable — keeping the last good roster ({Count} officers)", _pool.Count);
            _problems?.Report(ConfigAreas.FoRoster, _path, ex.Message);
        }
    }

    private static FoRosterState Copy(FoRosterState state) => new()
    {
        CurrentId = state.CurrentId,
        PreviousId = state.PreviousId,
        DrawnFor = state.DrawnFor,
        IntroDone = state.IntroDone,
        PinnedId = state.PinnedId,
    };
}
