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
    /// <summary>The roster file name in the user config tree (ADR-0007).</summary>
    public const string FileName = "fo-roster.json";

    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly FoRosterStateFile _stateFile;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoRosterService> _logger;
    private readonly SettingsWriter? _settings;
    private readonly string _path;
    private readonly RosterFileBank _bank;
    private readonly object _gate = new();
    private readonly Random _random = new();

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
        _settings = settings;
        _path = Path.Combine(string.IsNullOrWhiteSpace(configDirectory) ? UserConfigPaths.Root : configDirectory, FileName);
        _bank = new RosterFileBank(_path, logger, problems);
        _state = _stateFile.Load();
    }

    /// <summary>The dice, injectable for deterministic tests: a non-negative integer below the
    /// bound. Null = <see cref="Random"/>.</summary>
    public Func<int, int>? Next { get; init; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The roster file in the user config tree.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public bool IsActive
    {
        get
        {
            var options = _options.CurrentValue;
            return options.Roster.IsActive(options.Enabled);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FoRosterEntry> Pool
    {
        get
        {
            lock (_gate)
            {
                return _bank.Current;
            }
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
                var id = _options.CurrentValue.Roster.IsPinned ? _state.PinnedId : _state.CurrentId;
                return Find(id);
            }
        }
    }

    /// <inheritdoc />
    public FoRosterEntry? Draw(string reason, DateTimeOffset nowUtc, string? drawnFor = null, string? dayId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        FoRosterEntry? picked;
        string? previous;
        lock (_gate)
        {
            previous = _state.CurrentId;
            picked = FoRosterDraw.Pick(_bank.Current, previous, Next ?? DefaultNext);
            if (picked is null)
            {
                _logger.LogWarning("FO roster draw requested ({Reason}) but {Path} holds no officers", reason, _path);
                return null;
            }

            _state.PreviousId = previous;
            _state.CurrentId = picked.Id;
            _state.DrawnFor = drawnFor ?? _state.DrawnFor;
            _state.DrawnDayId = dayId;
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

    /// <inheritdoc />
    public void AdoptDay(string drawnFor, string? dayId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawnFor);
        lock (_gate)
        {
            if (string.Equals(_state.DrawnFor, drawnFor, StringComparison.Ordinal))
            {
                return;
            }

            _state.DrawnFor = drawnFor;
            _state.DrawnDayId = dayId;
            _state.IntroDone = false;
            _stateFile.Save(_state);
        }

        Changed?.Invoke();
    }

    /// <summary>The duty day that opened after the draw on the same sim date is the FO's day:
    /// from here the FO is pinned until that day ends (ADR-0019 §8). A no-op when the state
    /// already names it.</summary>
    public void BindDay(string dayId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dayId);
        lock (_gate)
        {
            if (_state.CurrentId is null || string.Equals(_state.DrawnDayId, dayId, StringComparison.Ordinal))
            {
                return;
            }

            _state.DrawnDayId = dayId;
            _stateFile.Save(_state);
        }

        _logger.LogDebug("FO roster: the FO on duty is bound to duty day {DayId}", dayId);
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public bool Pin(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        FoRosterEntry? entry;
        lock (_gate)
        {
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
        => id is null ? null : _bank.Current.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>The roster file on the shared user-config-bank contract (review 2026-11-05):
    /// re-read after its last-write time moved, problems on the Config page, an unreadable
    /// file keeps the last good roster.</summary>
    private sealed class RosterFileBank(string path, ILogger logger, ConfigProblemStore? problems)
        : UserConfigFileBank<IReadOnlyList<FoRosterEntry>>(path, ConfigAreas.FoRoster, logger, problems, [])
    {
        public new IReadOnlyList<FoRosterEntry> Current => base.Current;

        protected override (IReadOnlyList<FoRosterEntry> Value, IReadOnlyList<string> Problems) Parse(string json)
        {
            var result = FoRosterFile.Parse(json);
            return (result.Officers, result.Problems);
        }

        protected override void OnAbsent()
            => logger.LogInformation("FO roster file {Path} is absent — no officers to draw from", FilePath);

        protected override void OnLoaded(IReadOnlyList<FoRosterEntry> value)
            => logger.LogInformation("fo-roster.json loaded ({Count} officers)", value.Count);
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
