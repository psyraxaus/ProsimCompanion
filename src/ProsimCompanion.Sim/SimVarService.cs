using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Sim;

/// <summary>
/// The application-wide <see cref="ISimVars"/> implementation. Owns the subscription table;
/// the live SimConnect session (managed by <see cref="SimConnectService"/>) attaches as the
/// backend on connect. Mirrors the ProSim dataref service's degrade behaviour: without a
/// backend, cached values remain readable (stale-flagged).
/// </summary>
public sealed class SimVarService : ISimVars
{
    private readonly DataRefSubscriptionTable _table;
    private readonly ILogger<SimVarService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _units = new(StringComparer.OrdinalIgnoreCase);
    private ISimVarBackend? _backend;

    public SimVarService(ILogger<SimVarService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _table = new DataRefSubscriptionTable(
            (name, ex) => _logger.LogError(ex, "SimVar subscriber handler for {SimVar} threw", name));

        _table.RegistrationNeeded += (name, interval) =>
        {
            string? unit;
            ISimVarBackend? backend;
            lock (_gate)
            {
                _units.TryGetValue(name, out unit);
                backend = _backend;
            }
            if (unit is not null)
            {
                backend?.EnsureRegistered(name, unit, interval);
            }
        };
        _table.RegistrationReleased += name =>
        {
            ISimVarBackend? backend;
            lock (_gate)
            {
                _units.Remove(name);
                backend = _backend;
            }
            backend?.Unregister(name);
        };
    }

    /// <inheritdoc />
    public IDataRefSubscription Subscribe(string simVarName, string unit, DataRefTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simVarName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        lock (_gate)
        {
            // The first subscription for a name fixes its unit (documented on ISimVars).
            _ = _units.TryAdd(simVarName, unit);
        }

        return _table.Subscribe(simVarName, tier);
    }

    /// <inheritdoc />
    public async Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default)
    {
        SimWriteGate.EnsureAllowed(simVarName);

        ISimVarBackend? backend;
        lock (_gate)
        {
            backend = _backend;
        }

        if (backend is null)
        {
            throw new InvalidOperationException(
                "MSFS is not connected — the SimVar write cannot be delivered. Callers should " +
                "treat this as transient and retry once SimConnect reports Connected.");
        }

        await backend.WriteValueAsync(simVarName, value, cancellationToken).ConfigureAwait(false);
    }

    internal void AttachBackend(ISimVarBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        List<(string Name, string Unit, int IntervalMs)> registrations = [];
        lock (_gate)
        {
            _backend = backend;
            foreach (var (name, intervalMs) in _table.ActiveRegistrations())
            {
                if (_units.TryGetValue(name, out var unit))
                {
                    registrations.Add((name, unit, intervalMs));
                }
            }
        }

        foreach (var (name, unit, intervalMs) in registrations)
        {
            backend.EnsureRegistered(name, unit, intervalMs);
        }
    }

    internal void DetachBackend()
    {
        lock (_gate)
        {
            _backend = null;
        }
        _table.MarkAllStale();
    }

    internal void UpdateFromSim(string name, double value)
        => _table.UpdateValue(name, value, DateTimeOffset.UtcNow);
}

/// <summary>Transport a connected SimConnect session provides to <see cref="SimVarService"/>.</summary>
internal interface ISimVarBackend
{
    void EnsureRegistered(string simVarName, string unit, int intervalMs);
    void Unregister(string simVarName);

    /// <summary>Writes a FLOAT64 value. Called only after the write gate has passed.</summary>
    Task WriteValueAsync(string simVarName, double value, CancellationToken cancellationToken);
}
