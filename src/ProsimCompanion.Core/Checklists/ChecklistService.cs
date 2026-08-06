using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Core.Checklists;

/// <summary>
/// Visual checklist host: loads the JSON definitions from <c>config/checklists</c> (hot-reload
/// on save, 300 ms debounce — the predecessor's editing loop), holds the active
/// <see cref="ChecklistRunner"/>, subscribes exactly the datarefs the active checklist's
/// conditions reference, and re-evaluates on a 500 ms tick. Degrades cleanly: with ProSim
/// absent every condition reads 0 and auto items simply wait.
/// </summary>
public sealed class ChecklistService : IDisposable
{
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    private readonly IProsimDataRefs _prosim;
    private readonly ILogger<ChecklistService> _logger;
    private readonly string _folder;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly Dictionary<string, IDataRefSubscription> _subscriptions = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;
    private List<ChecklistDefinition> _definitions = [];
    private ChecklistRunner? _active;

    public event EventHandler? Changed;

    public ChecklistService(IProsimDataRefs prosim, ILogger<ChecklistService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(logger);
        _prosim = prosim;
        _logger = logger;
        _folder = Path.Combine(AppContext.BaseDirectory, "config", "checklists");

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

    public IReadOnlyList<ChecklistCatalogEntry> Catalog()
    {
        lock (_lock)
        {
            return [.. _definitions.Select(definition => new ChecklistCatalogEntry(
                definition.Checklist,
                definition.Order ?? int.MaxValue,
                definition.Items.Count))];
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
            if (_active is null)
            {
                return _definitions.Count > 0 ? _definitions[0].Checklist : null;
            }
            var index = _definitions.FindIndex(definition => definition.Checklist == _active.Name);
            return index >= 0 && index + 1 < _definitions.Count ? _definitions[index + 1].Checklist : null;
        }
    }

    public void Select(string name)
    {
        lock (_lock)
        {
            var definition = _definitions.FirstOrDefault(candidate =>
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

    public void Check(int index) => Mutate(runner => runner.Check(index));

    public void Skip(int index) => Mutate(runner => runner.Skip(index));

    public void Restart() => Mutate(runner =>
    {
        runner.Restart();
        return true;
    });

    // ── Internals ────────────────────────────────────────────────────────────────────────

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

        lock (_lock)
        {
            _definitions = loaded;
            // A live run survives a reload only if its checklist still exists; the run restarts
            // so the statuses always match the (possibly edited) definition.
            if (_active is not null)
            {
                var current = loaded.FirstOrDefault(definition =>
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
        _logger.LogInformation("Loaded {Count} checklists from {Folder}", loaded.Count, _folder);
        Changed?.Invoke(this, EventArgs.Empty);
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
