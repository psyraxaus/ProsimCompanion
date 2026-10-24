using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Switches;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.DataRefs;

namespace ProsimCompanion.Prosim.Switches;

/// <summary>
/// The cockpit-switch relay (2026-10-24, owner option A): keeps one push subscription per
/// configured switch (and its lamp indicators), publishes their key-face state to
/// <see cref="CockpitSwitchStateStore"/>, and presses a switch when a Stream Deck key asks —
/// a direct SDK write of the <c>system.switches.S_*</c> dataref, the same thing the pilot's
/// hardware does through ProSim's hardware mapping. Replaces the owner's FSUIPC-offset chain
/// for the three leak measurement valves and opens every other catalogued switch.
/// <para>
/// Write safety: this service is the only thing that widens <see cref="ProsimWriteGate"/>,
/// and only for names that are BOTH a configured row AND in the switch catalogue AND in the
/// <c>S_</c> family. Subscriptions are the read-model rule made concrete: register once,
/// read the cache, never poll. Degrade-not-fail: with ProSim away every press answers
/// <c>unavailable</c>; a row naming an unknown switch is skipped with one log line.
/// </para>
/// </summary>
public sealed class CockpitSwitchService : IHostedService, ICockpitSwitchControl, IDisposable
{
    public const string PressEvent = "cockpit.press";
    private const string SwitchFamily = SwitchCatalog.Prefix + "S_";

    private readonly IOptionsMonitor<CockpitSwitchesOptions> _options;
    private readonly IProsimDataRefs _dataRefs;
    private readonly SwitchCatalogStore _catalog;
    private readonly CockpitSwitchStateStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CockpitSwitchService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, IDataRefSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedRows = new(StringComparer.Ordinal);
    private IReadOnlyList<CockpitSwitchRow> _rows = [];
    private IDisposable? _optionsChange;
    private IDisposable? _catalogChange;

    public CockpitSwitchService(
        IOptionsMonitor<CockpitSwitchesOptions> options,
        IProsimDataRefs dataRefs,
        SwitchCatalogStore catalog,
        CockpitSwitchStateStore store,
        JsonlEventLog eventLog,
        ILogger<CockpitSwitchService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _dataRefs = dataRefs;
        _catalog = catalog;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ProsimWriteGate.ConfiguredSwitchAllowed = IsConfiguredSwitch;
        _optionsChange = _options.OnChange(_ => Rebuild());
        _catalogChange = _catalog.Observe(_ => Rebuild());
        Rebuild();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The write-gate hook: a name may be written only while it is a configured row,
    /// a catalogued switch, and in the <c>S_</c> family (no breakers, no typed-in names).</summary>
    internal bool IsConfiguredSwitch(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(SwitchFamily, StringComparison.Ordinal))
        {
            return false;
        }

        var options = _options.CurrentValue;
        if (!options.Enabled || !options.Rows.Any(r => string.Equals(r.DataRef.Trim(), name, StringComparison.Ordinal)))
        {
            return false;
        }

        return _catalog.Snapshot().Find(name) is not null;
    }

    /// <inheritdoc />
    public async Task<CommandResult> PressAsync(string id, bool longPress, CancellationToken cancellationToken = default)
    {
        var (row, verdict) = Resolve(id);
        if (row is null)
        {
            return verdict!;
        }

        var name = row.DataRef.Trim();
        var values = _catalog.Snapshot().Find(name)?.Values ?? [];
        var current = CurrentValue(name, out var stale);
        if (current is null || stale)
        {
            return CommandResult.Unavailable($"ProSim is not connected (no live value for {row.Name} yet).");
        }

        try
        {
            if (row.PressMode == CockpitPressMode.Momentary)
            {
                await _dataRefs.PressMomentaryAsync(name, cancellationToken).ConfigureAwait(false);
                Record(row, name, current.Value, null, "momentary");
                return CommandResult.Ok($"{row.Name}: pressed.");
            }

            var next = CockpitSwitchLogic.NextValue(row.PressMode, current.Value, values, longPress);
            await _dataRefs.WriteAsync(name, next, cancellationToken).ConfigureAwait(false);
            Record(row, name, current.Value, next, longPress ? "cycleBack" : row.PressMode.ToString().ToLowerInvariant());
            return CommandResult.Ok($"{row.Name}: {CockpitSwitchLogic.Label(values, current.Value)} → {CockpitSwitchLogic.Label(values, next)}.");
        }
        catch (InvalidOperationException ex)
        {
            // The gate refused (row/catalogue changed under us) or ProSim dropped mid-call.
            _logger.LogWarning(ex, "Cockpit switch {Id} press refused", row.Id);
            return CommandResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> SetAsync(string id, int value, CancellationToken cancellationToken = default)
    {
        var (row, verdict) = Resolve(id);
        if (row is null)
        {
            return verdict!;
        }

        var name = row.DataRef.Trim();
        var values = _catalog.Snapshot().Find(name)?.Values ?? [];
        if (values.Count > 0 && values.All(v => v.Value != value))
        {
            return CommandResult.PreconditionFailed(
                $"{row.Name} has no position {value} — choices: {string.Join(", ", values.Select(v => $"{v.Value} = {v.Label}"))}.");
        }

        var current = CurrentValue(name, out var stale);
        if (current is null || stale)
        {
            return CommandResult.Unavailable($"ProSim is not connected (no live value for {row.Name} yet).");
        }

        if (current == value)
        {
            return CommandResult.AlreadySatisfied($"{row.Name} is already {CockpitSwitchLogic.Label(values, value)}.");
        }

        try
        {
            await _dataRefs.WriteAsync(name, value, cancellationToken).ConfigureAwait(false);
            Record(row, name, current.Value, value, "set");
            return CommandResult.Ok($"{row.Name}: {CockpitSwitchLogic.Label(values, current.Value)} → {CockpitSwitchLogic.Label(values, value)}.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cockpit switch {Id} set refused", row.Id);
            return CommandResult.Failed(ex.Message);
        }
    }

    private (CockpitSwitchRow? Row, CommandResult? Verdict) Resolve(string id)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return (null, CommandResult.Unavailable("Cockpit switches are off — Settings → Cockpit Switches."));
        }

        var row = CockpitSwitchLogic.FindRow(options.Rows, id);
        if (row is null)
        {
            var known = options.Rows.Select(r => r.Id.Trim()).Where(r => r.Length > 0).ToList();
            return (null, CommandResult.PreconditionFailed(known.Count == 0
                ? $"No cockpit switch '{id}' — add rows on Settings → Cockpit Switches."
                : $"No cockpit switch '{id}' — known: {string.Join(", ", known)}."));
        }

        var name = row.DataRef.Trim();
        if (!name.StartsWith(SwitchFamily, StringComparison.Ordinal) || _catalog.Snapshot().Find(name) is null)
        {
            return (null, CommandResult.PreconditionFailed(
                $"{row.Name}: '{name}' is not a switch in ProSim's catalogue (system.switches.S_*) — pick it again on the page."));
        }

        return (row, null);
    }

    private int? CurrentValue(string name, out bool stale)
    {
        IDataRefSubscription? subscription;
        lock (_gate)
        {
            _subscriptions.TryGetValue(name, out subscription);
        }

        stale = subscription?.IsStale ?? true;
        return ReadInt(subscription);
    }

    private static int? ReadInt(IDataRefSubscription? subscription)
    {
        if (subscription?.RawValue is null)
        {
            return null;
        }

        var value = subscription.GetValue(int.MinValue);
        return value == int.MinValue ? null : value;
    }

    private void Record(CockpitSwitchRow row, string name, int from, int? to, string mode)
    {
        _logger.LogInformation(
            "Cockpit switch {Id} ({DataRef}) {Mode}: {From} → {To} for {Source}",
            row.Id, name, mode, from, to?.ToString() ?? "press", CommandRegistry.CurrentSource ?? "command");
        _eventLog.Record(PressEvent, new { id = row.Id, dataRef = name, mode, from, to, source = CommandRegistry.CurrentSource ?? "command" });
    }

    /// <summary>(Re)subscribe to exactly the datarefs the rows need, then publish.</summary>
    private void Rebuild()
    {
        lock (_gate)
        {
            var options = _options.CurrentValue;
            var catalog = _catalog.Snapshot();
            var rows = new List<CockpitSwitchRow>();
            var needed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in options.Rows)
            {
                var name = row.DataRef.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (!name.StartsWith(SwitchFamily, StringComparison.Ordinal) || catalog.Find(name) is null)
                {
                    if (_warnedRows.Add(name))
                    {
                        _logger.LogWarning("Cockpit switch row {Id} names {DataRef}, which is not a catalogued system.switches.S_* switch — row skipped", row.Id, name);
                    }

                    continue;
                }

                rows.Add(row);
                needed.Add(name);
                foreach (var indicator in new[] { row.UpperIndicator, row.LowerIndicator })
                {
                    if (!string.IsNullOrWhiteSpace(indicator))
                    {
                        needed.Add(indicator.Trim());
                    }
                }
            }

            foreach (var gone in _subscriptions.Keys.Where(k => !needed.Contains(k)).ToList())
            {
                _subscriptions[gone].ValueChanged -= OnValueChanged;
                _subscriptions[gone].Dispose();
                _subscriptions.Remove(gone);
            }

            foreach (var name in needed.Where(n => !_subscriptions.ContainsKey(n)))
            {
                var subscription = _dataRefs.SubscribeDynamic(name, DataRefTier.Normal);
                subscription.ValueChanged += OnValueChanged;
                _subscriptions[name] = subscription;
            }

            _rows = rows;
        }

        Publish();
    }

    private void OnValueChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        List<CockpitSwitchState> states;
        lock (_gate)
        {
            var catalog = _catalog.Snapshot();
            states = new List<CockpitSwitchState>(_rows.Count);
            foreach (var row in _rows)
            {
                var name = row.DataRef.Trim();
                _subscriptions.TryGetValue(name, out var main);
                var upper = string.IsNullOrWhiteSpace(row.UpperIndicator) ? null : Lookup(row.UpperIndicator.Trim());
                var lower = string.IsNullOrWhiteSpace(row.LowerIndicator) ? null : Lookup(row.LowerIndicator.Trim());
                states.Add(CockpitSwitchLogic.Evaluate(
                    row,
                    ReadInt(main),
                    ReadInt(upper),
                    ReadInt(lower),
                    main?.IsStale ?? true,
                    catalog.Find(name)?.Values ?? []));
            }
        }

        _store.Update(_ => new CockpitSwitchesSnapshot(states));
    }

    private IDataRefSubscription? Lookup(string name) => _subscriptions.TryGetValue(name, out var s) ? s : null;

    public void Dispose()
    {
        _optionsChange?.Dispose();
        _catalogChange?.Dispose();
        if (ReferenceEquals(ProsimWriteGate.ConfiguredSwitchAllowed?.Target, this))
        {
            ProsimWriteGate.ConfiguredSwitchAllowed = null;
        }

        lock (_gate)
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.ValueChanged -= OnValueChanged;
                subscription.Dispose();
            }

            _subscriptions.Clear();
            _rows = [];
        }
    }
}
