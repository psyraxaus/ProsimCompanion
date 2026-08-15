using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Checklists;

/// <summary>
/// Visual checklist host: loads the JSON definitions from <c>config/checklists</c> (hot-reload
/// on save, 300 ms debounce — the predecessor's editing loop), holds the active
/// <see cref="ChecklistRunner"/>, subscribes exactly the datarefs the active checklist's
/// conditions reference, and re-evaluates on a 500 ms tick. Degrades cleanly: with ProSim
/// absent every condition reads 0 and auto items simply wait.
///
/// Checklists are grouped into SETS: the per-phase folder files form the default
/// <see cref="DefaultSetName"/> set, and every Prosim2GSX-format file under
/// <c>config/checklists/sets</c> is a further set (one checklist per section, see
/// <see cref="Prosim2GsxChecklistSetLoader"/>). The web page picks the active set; the voice
/// First Officer pins to the default set via <see cref="Definitions(string)"/> so a web-side
/// set switch never changes what the spoken checklists match against.
/// </summary>
public sealed class ChecklistService : IDisposable
{
    /// <summary>Name of the built-in set formed by the per-phase files in
    /// <c>config/checklists</c> — always present (possibly empty), always the startup
    /// selection, and the set the voice FO pins to.</summary>
    public const string DefaultSetName = "A320 (ProsimCompanion)";

    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    private readonly IProsimDataRefs _prosim;
    private readonly IOptionsMonitor<ChecklistOptions> _options;
    private readonly ILogger<ChecklistService> _logger;
    private readonly string _folder;
    private readonly string _setsFolder;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly Dictionary<string, IDataRefSubscription> _subscriptions = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;
    private List<ChecklistSet> _sets = [new(DefaultSetName, [])];
    private string _activeSet = DefaultSetName;
    private ChecklistRunner? _active;
    private DateTimeOffset? _lastLoadedUtc;

    public event EventHandler? Changed;

    /// <summary>Absolute folder the definitions load from. Shown on the web page so an edit
    /// made to a look-alike copy elsewhere is self-diagnosing (issue #55 — a flight test was
    /// lost to exactly that).</summary>
    public string Folder => _folder;

    /// <summary>UTC time of the last (re)load — the page's visible acknowledgement that a
    /// save was picked up (hot reload) or that startup read the folder.</summary>
    public DateTimeOffset? LastLoadedUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastLoadedUtc;
            }
        }
    }

    public ChecklistService(
        IProsimDataRefs prosim,
        IOptionsMonitor<ChecklistOptions> options,
        ILogger<ChecklistService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _prosim = prosim;
        _options = options;
        _logger = logger;
        // User tree, not the install dir (ADR-0007): seeded from the shipped defaults by
        // UserConfigSeeder before the host builds; edits there survive app updates.
        _folder = UserConfigPaths.Checklists;
        _setsFolder = Path.Combine(_folder, "sets");

        Reload();
        StartWatcher();
        _timer = new Timer(_ => Tick(), null, EvaluateInterval, EvaluateInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _watcher?.Dispose();
        _reloadDebounce?.Dispose();
        lock (_lock)
        {
            DisposeSubscriptions();
        }
    }

    /// <summary>Names of every loaded checklist set, default set first.</summary>
    public IReadOnlyList<string> Sets()
    {
        lock (_lock)
        {
            return [.. _sets.Select(set => set.Name)];
        }
    }

    /// <summary>The set the web page is browsing. Voice ignores this — see
    /// <see cref="Definitions(string)"/>.</summary>
    public string ActiveSet
    {
        get
        {
            lock (_lock)
            {
                return _activeSet;
            }
        }
    }

    /// <summary>Switches the active set. Unknown names no-op; switching abandons the open run
    /// (it belongs to the previous set's definitions).</summary>
    public void SelectSet(string name)
    {
        lock (_lock)
        {
            var set = _sets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (set is null || string.Equals(set.Name, _activeSet, StringComparison.Ordinal))
            {
                return;
            }
            _activeSet = set.Name;
            _active = null;
            DisposeSubscriptions();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ChecklistCatalogEntry> Catalog()
    {
        lock (_lock)
        {
            return [.. ActiveDefinitions().Select(definition => new ChecklistCatalogEntry(
                definition.Checklist,
                definition.Order ?? int.MaxValue,
                definition.Items.Count))];
        }
    }

    /// <summary>Snapshot of the ACTIVE set's definitions. The list is a copy; the definitions
    /// themselves are not mutated after load.</summary>
    public IReadOnlyList<ChecklistDefinition> Definitions()
    {
        lock (_lock)
        {
            return [.. ActiveDefinitions()];
        }
    }

    /// <summary>Snapshot of a NAMED set's definitions (empty for unknown names). The voice FO
    /// calls this with <see cref="DefaultSetName"/> so its phrase matching is immune to the web
    /// page's set selection.</summary>
    public IReadOnlyList<ChecklistDefinition> Definitions(string set)
    {
        lock (_lock)
        {
            var match = _sets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, set, StringComparison.OrdinalIgnoreCase));
            return match is null ? [] : [.. match.Definitions];
        }
    }

    public ChecklistView? ActiveView()
    {
        lock (_lock)
        {
            return _active?.Snapshot();
        }
    }

    /// <summary>Name of the checklist after the active one in display order — the "next
    /// checklist" affordance shown at completion.</summary>
    public string? NextChecklistName()
    {
        lock (_lock)
        {
            var definitions = ActiveDefinitions();
            if (_active is null)
            {
                return definitions.Count > 0 ? definitions[0].Checklist : null;
            }
            var index = definitions.FindIndex(definition => definition.Checklist == _active.Name);
            return index >= 0 && index + 1 < definitions.Count ? definitions[index + 1].Checklist : null;
        }
    }

    public void Select(string name)
    {
        lock (_lock)
        {
            var definition = ActiveDefinitions().FirstOrDefault(candidate =>
                string.Equals(candidate.Checklist, name, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                return;
            }

            _active = new ChecklistRunner(definition);
            ResubscribeFor(definition);
            EvaluateActive();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Deselect()
    {
        lock (_lock)
        {
            _active = null;
            DisposeSubscriptions();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ticks the active line. With <see cref="ChecklistOptions.AllowManualOverride"/>
    /// off (the default) auto items refuse the tick — satisfy the condition or skip; with it on
    /// the tick lands with freeze semantics (see <see cref="ChecklistRunner.ForceCheck"/>).</summary>
    public void Check(int index) => Mutate(runner =>
        _options.CurrentValue.AllowManualOverride ? runner.ForceCheck(index) : runner.Check(index));

    public void Skip(int index) => Mutate(runner => runner.Skip(index));

    /// <summary>Voice FO seam: complete/skip a line by index with voice-freeze semantics (see
    /// <see cref="ChecklistRunner.VoiceComplete"/>). No-ops when the named checklist is not the
    /// active one — the user may have opened a different checklist on the web page mid-run, and
    /// the spoken run must never scribble on it.</summary>
    public void VoiceComplete(string checklist, int index)
        => MutateIfActive(checklist, runner => runner.VoiceComplete(index));

    /// <summary>Voice FO seam: skip a line by index (see <see cref="VoiceComplete"/>).</summary>
    public void VoiceSkip(string checklist, int index)
        => MutateIfActive(checklist, runner => runner.VoiceSkip(index));

    public void Restart() => Mutate(runner =>
    {
        runner.Restart();
        return true;
    });

    // ── Internals ────────────────────────────────────────────────────────────────────────

    /// <summary>The active set's definition list. Call under <see cref="_lock"/> only. The
    /// active set always exists — <see cref="Reload"/> and <see cref="SelectSet"/> maintain
    /// the invariant.</summary>
    private List<ChecklistDefinition> ActiveDefinitions()
        => _sets.First(set => string.Equals(set.Name, _activeSet, StringComparison.Ordinal)).Definitions;

    private void MutateIfActive(string checklist, Func<ChecklistRunner, bool> action)
    {
        bool changed;
        lock (_lock)
        {
            if (_active is null
                || !string.Equals(_active.Name, checklist, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            changed = action(_active);
            if (changed)
            {
                EvaluateActive();
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Mutate(Func<ChecklistRunner, bool> action)
    {
        bool changed;
        lock (_lock)
        {
            if (_active is null)
            {
                return;
            }
            changed = action(_active);
            if (changed)
            {
                EvaluateActive();
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Tick()
    {
        bool changed;
        lock (_lock)
        {
            if (_active is null)
            {
                return;
            }
            changed = EvaluateActive();
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool EvaluateActive()
        => _active!.Evaluate(name =>
            _subscriptions.TryGetValue(name, out var subscription) ? subscription.GetValue(0.0) : 0.0);

    private void ResubscribeFor(ChecklistDefinition definition)
    {
        DisposeSubscriptions();
        var datarefs = definition.Items
            .Where(item => item.Verify is not null)
            .SelectMany(item => item.Verify!.ReferencedDatarefs())
            .Distinct(StringComparer.Ordinal);
        foreach (var dataref in datarefs)
        {
            _subscriptions[dataref] = _prosim.Subscribe(dataref, DataRefTier.Normal);
        }
    }

    private void DisposeSubscriptions()
    {
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }

    private void Reload()
    {
        var sets = new List<ChecklistSet> { new(DefaultSetName, LoadDefaultSet()) };
        foreach (var set in LoadProsim2GsxSets())
        {
            if (sets.Any(existing => string.Equals(existing.Name, set.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Duplicate checklist set name '{Name}' — first wins", set.Name);
                continue;
            }
            sets.Add(set);
        }

        lock (_lock)
        {
            _sets = sets;
            _lastLoadedUtc = DateTimeOffset.UtcNow;
            if (!sets.Any(set => string.Equals(set.Name, _activeSet, StringComparison.Ordinal)))
            {
                _activeSet = DefaultSetName; // the selected set's file was deleted mid-session
            }

            // A live run survives a reload only if its checklist still exists in the active
            // set; the run restarts so the statuses always match the (possibly edited)
            // definition.
            if (_active is not null)
            {
                var current = ActiveDefinitions().FirstOrDefault(definition =>
                    string.Equals(definition.Checklist, _active.Name, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    _active = null;
                    DisposeSubscriptions();
                }
                else
                {
                    _active = new ChecklistRunner(current);
                    ResubscribeFor(current);
                    EvaluateActive();
                }
            }
        }
        _logger.LogInformation(
            "Loaded {SetCount} checklist sets ({Count} checklists in the default set) from {Folder}",
            sets.Count, sets[0].Definitions.Count, _folder);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The per-phase folder files (Prosim2FO-compatible, one checklist per file).</summary>
    private List<ChecklistDefinition> LoadDefaultSet()
    {
        var loaded = new List<ChecklistDefinition>();
        try
        {
            if (Directory.Exists(_folder))
            {
                foreach (var file in Directory.EnumerateFiles(_folder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var definition = JsonSerializer.Deserialize<ChecklistDefinition>(
                            File.ReadAllText(file), ChecklistDefinition.JsonOptions);
                        if (definition is null || string.IsNullOrWhiteSpace(definition.Checklist))
                        {
                            _logger.LogWarning("Checklist file {File} has no checklist name — skipped", file);
                            continue;
                        }
                        if (loaded.Any(existing => string.Equals(existing.Checklist, definition.Checklist, StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogWarning("Duplicate checklist name '{Name}' in {File} — first wins", definition.Checklist, file);
                            continue;
                        }
                        loaded.Add(definition);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning("Checklist file {File} failed to parse: {Message}", file, ex.Message);
                    }
                }
            }
            else
            {
                _logger.LogInformation("Checklist folder {Folder} does not exist — no checklists loaded", _folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Checklist folder scan failed");
        }

        loaded.Sort((left, right) =>
        {
            var byOrder = (left.Order ?? int.MaxValue).CompareTo(right.Order ?? int.MaxValue);
            return byOrder != 0 ? byOrder : string.Compare(left.Checklist, right.Checklist, StringComparison.OrdinalIgnoreCase);
        });
        return loaded;
    }

    /// <summary>One set per Prosim2GSX-format file under <c>config/checklists/sets</c> — the
    /// files stay in their native shape so a user's own Prosim2GSX checklist drops straight in.</summary>
    private List<ChecklistSet> LoadProsim2GsxSets()
    {
        var sets = new List<ChecklistSet>();
        try
        {
            if (!Directory.Exists(_setsFolder))
            {
                return sets;
            }
            foreach (var file in Directory.EnumerateFiles(_setsFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var set = Prosim2GsxChecklistSetLoader.Parse(
                        File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), _logger);
                    if (set is null)
                    {
                        _logger.LogWarning("Checklist set file {File} has no sections — skipped", file);
                        continue;
                    }
                    if (sets.Any(existing => string.Equals(existing.Name, set.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning("Duplicate checklist set name '{Name}' in {File} — first wins", set.Name, file);
                        continue;
                    }
                    sets.Add(set);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Checklist set file {File} failed to parse: {Message}", file, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Checklist sets folder scan failed");
        }
        return sets;
    }

    private void StartWatcher()
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }
            _watcher = new FileSystemWatcher(_folder, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                // The sets subfolder hot-reloads too (a dropped-in Prosim2GSX file appears
                // without a restart).
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, _) => DebouncedReload();
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, _) => DebouncedReload();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            _logger.LogWarning(ex, "Checklist hot-reload watcher failed to start (edits need a restart)");
        }
    }

    private void DebouncedReload()
    {
        _reloadDebounce?.Dispose();
        _reloadDebounce = new Timer(_ => Reload(), null, ReloadDebounce, Timeout.InfiniteTimeSpan);
    }
}
