using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The one writer of ProsimCompanion's own tracking LVARs (<see cref="CompanionLvarNames"/>):
/// the per-service done latches, the turnaround and prep-done flags and the loadsheet
/// editions. A write that fails because MSFS is not connected is queued — latest value per
/// LVAR — and flushed on the SimConnect disconnected→connected edge (issue #76 item 2: the
/// 2026-08-15 flight lost a service-done write to a SimConnect gap, so the next restart's
/// resync was missing evidence). The three bulk resets live here as well, so a reset issued
/// while MSFS is down is applied when it comes back: before this class the new-plan clear
/// wrote the LVARs directly from the automation (issue #230), so a different plan loaded while
/// MSFS was down cleared nothing and the next restart resynced to the OLD leg's progress.
/// Every method is safe to fire and forget; nothing here throws to its caller.
/// </summary>
public sealed class CompanionTrackingLvars : IDisposable
{
    /// <summary>The one-shot services with a done-LVAR (config can reorder/skip them, but the
    /// id vocabulary is fixed by GSX). The resync subscribes exactly these.</summary>
    public static readonly IReadOnlyList<string> TrackedServices =
    [
        GsxServiceIds.Refueling, GsxServiceIds.Catering, GsxServiceIds.Boarding,
        GsxServiceIds.Deboarding, GsxServiceIds.Water, GsxServiceIds.Lavatory,
        GsxServiceIds.Cleaning, GsxServiceIds.Gpu, GsxServiceIds.DeIce,
    ];

    private readonly ISimVars _simVars;
    private readonly ConnectionStatusStore _connectionStatus;
    private readonly ILogger<CompanionTrackingLvars> _logger;
    private readonly Dictionary<string, double> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();
    private bool _simConnectWasConnected;

    public CompanionTrackingLvars(
        ISimVars simVars,
        ConnectionStatusStore connectionStatus,
        ILogger<CompanionTrackingLvars> logger)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(connectionStatus);
        ArgumentNullException.ThrowIfNull(logger);

        _simVars = simVars;
        _connectionStatus = connectionStatus;
        _logger = logger;
        _simConnectWasConnected = SimConnectConnected();
        _connectionStatus.Changed += OnConnectionStatusChanged;
    }

    public void Dispose() => _connectionStatus.Changed -= OnConnectionStatusChanged;

    /// <summary>Whether SimConnect reported connected at the last status change — what a
    /// caller may say about a write it just issued ("cleared" vs "queued until MSFS connects").
    /// Best effort: a write can still fail on a connection that flaps mid-call; it then queues
    /// itself like any other.</summary>
    public bool IsSimConnected => Volatile.Read(ref _simConnectWasConnected);

    /// <summary>Writes waiting for the next SimConnect connected edge (one per LVAR).</summary>
    public int PendingCount
    {
        get
        {
            lock (_pendingGate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Latches a one-shot service as completed this cycle (lifecycle Completed edge,
    /// or the startup resync backfilling a seeded verdict so the NEXT restart resyncs from the
    /// LVARs alone).</summary>
    public Task MarkServiceDoneAsync(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        return Run("service done", [(CompanionLvarNames.ServiceDone(serviceId).Name, 1)]);
    }

    /// <summary>Latches ground-preparation completion for this gate session.</summary>
    public Task MarkPrepDoneAsync() => Run("prep done", [(CompanionLvarNames.PrepDone.Name, 1)]);

    /// <summary>The arrival boundary: this sim session is now in a turnaround and the next
    /// leg's progress tracking starts clean — TURNAROUND = 1, everything else 0.</summary>
    public Task ResetForArrivalAsync() => Run("arrival reset", AllTracking(turnaround: 1));

    /// <summary>Zeroes every tracking LVAR, the turnaround and prep flags included (stale
    /// detection, issue #33): a ProSim reset means the user started a fresh flight, and prep
    /// wrote ProSim datarefs (chocks/ground power) the reset also cleared.</summary>
    public Task ClearAllAsync() => Run("stale clear", AllTracking(turnaround: 0));

    /// <summary>A new plan is a new leg (ADR-0019 §6) / end of duty (§8): the departure
    /// one-shots and the loadsheet editions start over; the arrival side (deboarding, GPU) and
    /// the turnaround / prep-done flags stay — the aircraft is where it is, and an app restart
    /// after the reset must not re-drive the reposition under the passengers.</summary>
    public Task ClearDepartureSideAsync()
    {
        var writes = new List<(string Name, double Value)>(NewPlanCycleReset.DepartureServices.Count + 2);
        foreach (var serviceId in NewPlanCycleReset.DepartureServices)
        {
            writes.Add((CompanionLvarNames.ServiceDone(serviceId).Name, 0));
        }
        writes.Add((CompanionLvarNames.LoadsheetPrelimEdition.Name, 0));
        writes.Add((CompanionLvarNames.LoadsheetFinalSent.Name, 0));
        return Run("new-leg clear", writes);
    }

    private static List<(string Name, double Value)> AllTracking(double turnaround)
    {
        var writes = new List<(string Name, double Value)>(TrackedServices.Count + 4)
        {
            (CompanionLvarNames.Turnaround.Name, turnaround),
            (CompanionLvarNames.PrepDone.Name, 0),
            (CompanionLvarNames.LoadsheetPrelimEdition.Name, 0),
            (CompanionLvarNames.LoadsheetFinalSent.Name, 0),
        };
        foreach (var serviceId in TrackedServices)
        {
            writes.Add((CompanionLvarNames.ServiceDone(serviceId).Name, 0));
        }
        return writes;
    }

    /// <summary>Runs the writes on the thread pool (callers are event handlers on the raising
    /// thread — a SimConnect call must not run there), in order, and says once when any of
    /// them had to wait for MSFS.</summary>
    private Task Run(string operation, List<(string Name, double Value)> writes)
        => Task.Run(async () =>
        {
            var queued = 0;
            foreach (var (name, value) in writes)
            {
                if (!await TryWriteAsync(name, value).ConfigureAwait(false))
                {
                    queued++;
                }
            }

            if (queued > 0)
            {
                _logger.LogInformation(
                    "Tracking LVARs {Operation}: {Queued} of {Total} write(s) queued until MSFS connects",
                    operation, queued, writes.Count);
            }
            else
            {
                _logger.LogDebug("Tracking LVARs {Operation}: {Total} write(s) done", operation, writes.Count);
            }
        });

    /// <summary>True when the write went to SimConnect; false when it was queued (MSFS not
    /// connected) or failed outright (logged — the gates still apply without it).</summary>
    private async Task<bool> TryWriteAsync(string name, double value)
    {
        try
        {
            await _simVars.WriteAsync(name, value).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            lock (_pendingGate)
            {
                _pending[name] = value;
            }
            _logger.LogDebug(
                "Tracking LVAR write {Name}={Value} queued until MSFS connects: {Reason}",
                name, value, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tracking LVAR write {Name}={Value} failed", name, value);
            return false;
        }
    }

    private bool SimConnectConnected()
        => _connectionStatus.Snapshot()
            .Any(pair => pair.Key == Subsystems.SimConnect && pair.Value == ConnectionState.Connected);

    /// <summary>Flushes the queue on the SimConnect disconnected→connected edge. A write
    /// failing again (connection flapped mid-flush) simply re-queues itself for the next edge.</summary>
    private void OnConnectionStatusChanged(object? sender, EventArgs e)
    {
        var connected = SimConnectConnected();
        if (connected == Volatile.Read(ref _simConnectWasConnected))
        {
            return;
        }
        Volatile.Write(ref _simConnectWasConnected, connected);
        if (!connected)
        {
            return;
        }

        KeyValuePair<string, double>[] pending;
        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            pending = [.. _pending];
            _pending.Clear();
        }

        _logger.LogInformation(
            "SimConnect is back — flushing {Count} queued tracking LVAR write(s)", pending.Length);
        _ = Task.Run(async () =>
        {
            foreach (var (name, value) in pending)
            {
                await TryWriteAsync(name, value).ConfigureAwait(false);
            }
        });
    }
}
