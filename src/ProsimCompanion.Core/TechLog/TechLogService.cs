using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Sessions;

namespace ProsimCompanion.Core.TechLog;

/// <summary>
/// The tech-log store and lifecycle, UI- and speech-agnostic so the web page (Web → Core
/// only) and the voice brief (Speech) share one source of truth. Never writes datarefs or
/// injects failures — defects are procedural paperwork. The store write is atomic
/// (temp-then-move) and a corrupt file is moved aside and rebuilt, never a crash.
/// </summary>
public interface ITechLogService
{
    /// <summary>All defects, most recently raised first.</summary>
    IReadOnlyList<TechLogDefect> Defects { get; }

    /// <summary>Open/deferred defects, most-due first (ascending due date).</summary>
    IReadOnlyList<TechLogDefect> OpenDefects { get; }

    /// <summary>Live master toggle (options are hot-reloadable).</summary>
    bool IsEnabled { get; }

    /// <summary>Raised after any store mutation, on the mutating thread.</summary>
    event Action? Changed;

    /// <summary>Raised when random wear adds a defect at shutdown, so the speech layer can
    /// announce it without Core referencing the arbiter.</summary>
    event Action<TechLogDefect>? WearRaised;

    /// <summary>Adds a defect, or replaces the defect with the same id (idempotent update).</summary>
    TechLogDefect RaiseDefect(TechLogDefect defect);

    /// <summary>Builds a ready-to-raise draft with the category-derived due date and the
    /// simulated MEL reference filled in.</summary>
    TechLogDefect NewDraft(string source, string title, MelCategory category);

    /// <summary>Marks a defect rectified. False when not found or already rectified.</summary>
    bool RectifyDefect(string id);

    /// <summary>Deletes a defect entirely (UI affordance). False when not found.</summary>
    bool RemoveDefect(string id);

    /// <summary>Whole days until (negative: past) the MEL due date; <see cref="int.MaxValue"/>
    /// when the due date is missing or unparseable (treated as "no rectification date").</summary>
    int DaysRemaining(TechLogDefect defect);

    /// <summary>True for an open defect past its due date. Advisory only — never grounds the aircraft.</summary>
    bool IsOverdue(TechLogDefect defect);

    /// <summary>Representative repair interval (days) for a category, from options.</summary>
    int RepairDaysForCategory(MelCategory category);

    /// <summary>The verbatim preflight brief for the given open items (facts only, never styled).</summary>
    string BuildBrief(IReadOnlyList<TechLogDefect> open);

    /// <summary>"overdue by N day(s)" / "due today" / "N day(s) remaining" / "no rectification date".</summary>
    string DaysPhrase(TechLogDefect defect);
}

/// <summary>
/// File-backed <see cref="ITechLogService"/>; also the tech-log
/// <see cref="ISessionFinalizationStep"/> (sector fold + optional random wear, after the
/// debrief and logbook steps have read the session log). Auto-rectify runs at start and on the
/// ColdAndDark/Preflight edges so an expired item closes before the preflight brief reads it.
/// </summary>
public sealed class TechLogService : ITechLogService, ISessionFinalizationStep, IDisposable
{
    /// <summary>Per-flight chance of a wear defect when <c>techLog.randomWear</c> is on.</summary>
    public const double WearProbability = 0.08;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IOptionsMonitor<TechLogOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly IFlightPhaseSource _flight;
    private readonly ILogger<TechLogService> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private TechLogStore _store = new();
    private WearPool _wearPool = new();
    private bool _started;

    public TechLogService(
        IOptionsMonitor<TechLogOptions> options,
        JsonlEventLog eventLog,
        IFlightPhaseSource flight,
        ILogger<TechLogService> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _eventLog = eventLog;
        _flight = flight;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event Action? Changed;

    public event Action<TechLogDefect>? WearRaised;

    public bool IsEnabled => _options.CurrentValue.Enabled;

    string ISessionFinalizationStep.Name => "techlog";

    int ISessionFinalizationStep.Order => 30;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            Load();
            LoadWearPool();
        }

        _flight.PhaseChanged += OnPhaseChanged;
        CheckAutoRectify();
        RaiseChanged(); // a UI subscribed before Start() picks up the loaded defects
        _logger.LogInformation("Tech log started ({Open} open, {Total} total defect(s))",
            OpenDefects.Count, Defects.Count);
    }

    public void Dispose() => _flight.PhaseChanged -= OnPhaseChanged;

    // ---- snapshots ----

    public IReadOnlyList<TechLogDefect> Defects
    {
        get
        {
            lock (_gate)
            {
                return _store.Defects
                    .OrderByDescending(d => d.RaisedDate, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<TechLogDefect> OpenDefects
    {
        get
        {
            lock (_gate)
            {
                return _store.Defects
                    .Where(d => d.IsOpen)
                    .OrderBy(d => d.DueDate, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    // ---- raise / rectify / remove ----

    public TechLogDefect RaiseDefect(TechLogDefect defect)
    {
        ArgumentNullException.ThrowIfNull(defect);

        if (string.IsNullOrWhiteSpace(defect.Id))
        {
            defect.Id = NewId();
        }

        lock (_gate)
        {
            var existing = _store.Defects.FindIndex(
                d => string.Equals(d.Id, defect.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _store.Defects[existing] = defect; // idempotent update by id
            }
            else
            {
                _store.Defects.Add(defect);
            }

            Save();
        }

        _eventLog.Record("techlog.raised", new
        {
            id = defect.Id,
            title = defect.Title,
            source = defect.Source,
            category = defect.Category.ToString(),
            melReference = defect.MelReference,
            dueDate = defect.DueDate,
        });
        _logger.LogInformation("Tech log: raised {Id} '{Title}' ({Category}, due {Due})",
            defect.Id, defect.Title, defect.Category, defect.DueDate);
        RaiseChanged();
        return defect;
    }

    public TechLogDefect NewDraft(string source, string title, MelCategory category)
    {
        var days = RepairDaysForCategory(category);
        return new TechLogDefect
        {
            Id = NewId(),
            RaisedDate = Today(),
            RaisedFlight = CurrentSessionId(),
            Source = string.IsNullOrWhiteSpace(source) ? "manual" : source,
            Title = title?.Trim() ?? "",
            MelReference = $"MEL (SIM) CAT {category}",
            Category = category,
            RepairIntervalDays = days,
            DueDate = UtcToday().AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Status = DefectStatus.Deferred,
        };
    }

    public bool RectifyDefect(string id)
    {
        TechLogDefect? defect;
        lock (_gate)
        {
            defect = _store.Defects.FirstOrDefault(
                d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (defect is null || defect.Status == DefectStatus.Rectified)
            {
                return false;
            }

            defect.Status = DefectStatus.Rectified;
            defect.RectifiedDate = Today();
            defect.RectifiedFlight = CurrentSessionId();
            Save();
        }

        _eventLog.Record("techlog.rectified", new { id = defect.Id, title = defect.Title });
        _logger.LogInformation("Tech log: rectified {Id} '{Title}'", defect.Id, defect.Title);
        RaiseChanged();
        return true;
    }

    public bool RemoveDefect(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _store.Defects.RemoveAll(
                d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            _eventLog.Record("techlog.removed", new { id });
            RaiseChanged();
        }

        return removed;
    }

    // ---- due dates ----

    public int RepairDaysForCategory(MelCategory category)
    {
        var options = _options.CurrentValue;
        return Math.Max(0, category switch
        {
            MelCategory.A => options.CategoryADays,
            MelCategory.B => options.CategoryBDays,
            MelCategory.C => options.CategoryCDays,
            _ => options.CategoryDDays,
        });
    }

    public int DaysRemaining(TechLogDefect defect)
    {
        ArgumentNullException.ThrowIfNull(defect);

        // Exact-format parse: a hand-edited store with a malformed date degrades to "no
        // rectification date" rather than a bogus overdue flag.
        if (!DateTime.TryParseExact(defect.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var due))
        {
            return int.MaxValue;
        }

        return (int)(due.Date - UtcToday()).TotalDays;
    }

    public bool IsOverdue(TechLogDefect defect) => defect.IsOpen && DaysRemaining(defect) < 0;

    // ---- brief text (facts read verbatim — never styled) ----

    public string BuildBrief(IReadOnlyList<TechLogDefect> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        var parts = new List<string>
        {
            open.Count == 1
                ? "We're carrying one M E L item."
                : $"We're carrying {open.Count} M E L items.",
        };
        foreach (var defect in open)
        {
            var segment = defect.Title;
            if (!string.IsNullOrWhiteSpace(defect.MelReference))
            {
                segment += $", {defect.MelReference}";
            }

            if (!string.IsNullOrWhiteSpace(defect.OperationalImplications))
            {
                segment += $", {defect.OperationalImplications}";
            }

            segment += $", {DaysPhrase(defect)}";
            parts.Add(segment + ".");
        }

        return string.Join(" ", parts);
    }

    public string DaysPhrase(TechLogDefect defect)
    {
        var days = DaysRemaining(defect);
        if (days == int.MaxValue)
        {
            return "no rectification date";
        }

        if (days < 0)
        {
            return $"overdue by {-days} day{(days == -1 ? "" : "s")}";
        }

        if (days == 0)
        {
            return "due today";
        }

        return $"{days} day{(days == 1 ? "" : "s")} remaining";
    }

    // ---- lifecycle ----

    /// <summary>Tech-log finalization: fold sectors carried, then the optional wear roll.
    /// Runs after the debrief/logbook steps so this flight's counts are already extracted.</summary>
    Task ISessionFinalizationStep.RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.CurrentValue.Enabled)
        {
            FoldSectors(context.SessionId);
            if (_options.CurrentValue.RandomWear)
            {
                TryRandomWear(context.SessionId, Random.Shared.NextDouble, Random.Shared.Next);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Increments SectorsCarried once per session for each open defect — idempotent
    /// via the per-defect CountedSessions guard, so a re-run of finalization is a no-op.</summary>
    public void FoldSectors(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var changed = false;
        lock (_gate)
        {
            foreach (var defect in _store.Defects.Where(d => d.IsOpen))
            {
                if (defect.CountedSessions.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                defect.SectorsCarried++;
                defect.CountedSessions.Add(sessionId);
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            _eventLog.Record("techlog.carried", new { sessionId, open = OpenDefects.Count });
            RaiseChanged();
        }
    }

    /// <summary>
    /// Rolls the random-wear chance with injectable randomness (test affordance — the
    /// production path passes <see cref="Random.Shared"/>). Returns the raised defect, or null
    /// when the roll missed or the pool is empty. Raises <see cref="WearRaised"/> on success.
    /// </summary>
    public TechLogDefect? TryRandomWear(string? sessionId, Func<double> nextDouble, Func<int, int> nextIndex)
    {
        ArgumentNullException.ThrowIfNull(nextDouble);
        ArgumentNullException.ThrowIfNull(nextIndex);

        List<WearPoolEntry> entries;
        lock (_gate)
        {
            entries = _wearPool.Entries;
        }

        if (entries.Count == 0 || nextDouble() > WearProbability)
        {
            return null;
        }

        var entry = entries[Math.Clamp(nextIndex(entries.Count), 0, entries.Count - 1)];
        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            return null;
        }

        var category = entry.Category?.Trim().ToUpperInvariant() switch
        {
            "A" => MelCategory.A,
            "B" => MelCategory.B,
            "D" => MelCategory.D,
            _ => MelCategory.C,
        };
        var defect = NewDraft("randomWear", entry.Title, category);
        if (!string.IsNullOrWhiteSpace(entry.MelReference))
        {
            defect.MelReference = entry.MelReference.Trim();
        }

        defect.OperationalImplications = entry.Implications?.Trim() ?? "";
        defect.Placard = string.IsNullOrWhiteSpace(entry.Placard) ? null : entry.Placard.Trim();
        RaiseDefect(defect);

        _eventLog.Record("techlog.wear", new { id = defect.Id, title = defect.Title, sessionId });
        try
        {
            WearRaised?.Invoke(defect);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WearRaised subscriber threw");
        }

        return defect;
    }

    /// <summary>Auto-closes overdue open items when <c>techLog.autoRectifyOnDueDate</c> is on.
    /// Public so tests can drive it without phase edges.</summary>
    public void CheckAutoRectify()
    {
        if (!_options.CurrentValue.AutoRectifyOnDueDate)
        {
            return;
        }

        List<TechLogDefect> due;
        lock (_gate)
        {
            due = _store.Defects.Where(d => d.IsOpen && DaysRemaining(d) < 0).ToList();
        }

        foreach (var defect in due)
        {
            RectifyDefect(defect.Id);
            _eventLog.Record("techlog.autorectified", new { id = defect.Id, title = defect.Title });
        }
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        // Checked at the start-of-flight edges so an item that expired between sessions is
        // closed before the preflight brief would read it.
        if (e.Current is FlightPhase.ColdAndDark or FlightPhase.Preflight)
        {
            CheckAutoRectify();
        }
    }

    // ---- persistence ----

    private string ResolvePath()
    {
        var configured = _options.CurrentValue.Path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProsimCompanion",
            "techlog.json");
    }

    private void Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            _store = new TechLogStore();
            return;
        }

        try
        {
            _store = JsonSerializer.Deserialize<TechLogStore>(File.ReadAllText(path), JsonOptions)
                ?? new TechLogStore();
        }
        catch (Exception ex)
        {
            // Move the unreadable file aside (never delete user data) and start fresh.
            var backup = path + $".corrupt-{_time.GetUtcNow():yyyyMMdd-HHmmss}.bak";
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch (Exception moveEx)
            {
                // Best effort — a locked/permission-denied file just stays put; starting
                // fresh matters more than the backup.
                _logger.LogDebug(moveEx, "Could not move corrupt store aside");
            }

            _logger.LogWarning(ex, "Tech log corrupt — backed up to {Backup} and starting fresh", backup);
            _store = new TechLogStore();
        }
    }

    private void Save()
    {
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(_store, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write tech log {Path}", path);
        }
    }

    private void LoadWearPool()
    {
        try
        {
            var configured = _options.CurrentValue.WearPoolPath;
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "config", "techlog", "wear-pool.json")
                : configured;
            if (!File.Exists(path))
            {
                _wearPool = new WearPool();
                return;
            }

            _wearPool = JsonSerializer.Deserialize<WearPool>(File.ReadAllText(path), JsonOptions)
                ?? new WearPool();
            _logger.LogInformation("Random-wear pool loaded ({Count} entries)", _wearPool.Entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load wear pool");
            _wearPool = new WearPool();
        }
    }

    // ---- helpers ----

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tech log Changed subscriber threw");
        }
    }

    private string? CurrentSessionId() => Path.GetFileNameWithoutExtension(_eventLog.Path);

    private DateTime UtcToday() => _time.GetUtcNow().UtcDateTime.Date;

    private string Today() => UtcToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string NewId()
        => "def-" + _time.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
}
