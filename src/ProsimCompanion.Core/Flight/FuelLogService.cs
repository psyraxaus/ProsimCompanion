using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Keeps the <see cref="FuelLogStore"/> current (issue #154): every five seconds the pure
/// <see cref="FuelLogCore.Advance"/> runs on the OFP, the progress position, the takeoff time
/// and the live fuel; each newly stamped fix is a <c>fuel.log.fix</c> session event, each fix
/// passed unrecorded a <c>fuel.log.skipped</c> one (issue #167). Runs with the voice FO off —
/// the page is the consumer — and degrades to an empty log without an OFP, a position or
/// ProSim. The log is the <c>fuelLog</c> part of the flight-state snapshot: rows already
/// stamped come back after a restart instead of being re-stamped at the restart's second.
/// <para>Fix times are on the SIMULATED clock, like the takeoff time they are compared with
/// (2026-10-05, EFHK departure flown at 03:54Z sim / 19:44Z real: every fix after the origin
/// row was stamped with the PC clock and read 948 minutes late).</para>
/// </summary>
public sealed class FuelLogService : IStartupModule, IDisposable, IFlightStatePart
{
    public const string FixEvent = "fuel.log.fix";
    public const string SkippedEvent = "fuel.log.skipped";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly FuelLogStore _store;
    private readonly OfpStore _ofp;
    private readonly FlightProgressStore _progress;
    private readonly FlightTimesStore _times;
    private readonly ISimClock _clock;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FuelLogService> _logger;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly object _gate = new();
    private Timer? _timer;

    public FuelLogService(
        FuelLogStore store,
        OfpStore ofp,
        FlightProgressStore progress,
        FlightTimesStore times,
        IProsimDataRefs dataRefs,
        ISimClock clock,
        JsonlEventLog eventLog,
        ILogger<FuelLogService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _ofp = ofp;
        _progress = progress;
        _times = times;
        _clock = clock;
        _eventLog = eventLog;
        _logger = logger;
        _fuelTotal = dataRefs.Subscribe(ProsimDataRefNames.FuelTotal);
    }

    public void Start()
    {
        _ofp.Changed += OnOfpChanged;
        _timer = new Timer(_ => ProcessTick(), null, TimeSpan.FromSeconds(3), Poll);
    }

    // ---- flight-state snapshot part (#167) ----------------------------------------------------

    public string Key => "fuelLog";

    /// <summary>After the OFP (10) and the stamps (20): the next tick keeps a restored log
    /// only while its plan key and takeoff time agree with those.</summary>
    public int RestoreOrder => 30;

    public event Action? Changed;

    public JsonNode? Capture()
    {
        var log = _store.Snapshot();
        return log.HasPlan && log.LastHandled is not null
            ? JsonSerializer.SerializeToNode(log, FlightStateSnapshotStore.JsonOptions)
            : null;
    }

    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var log = node.Deserialize<FuelLogSnapshot>(FlightStateSnapshotStore.JsonOptions);
        if (log is null || !log.HasPlan)
        {
            return;
        }

        lock (_gate)
        {
            _store.Update(_ => log);
        }

        _logger.LogInformation(
            "Fuel log restored from the flight-state snapshot: {Origin}-{Destination}, {Passed} of {Count} fixes stamped",
            log.OriginIcao, log.DestinationIcao, log.PassedCount, log.Rows.Count);
    }

    // -------------------------------------------------------------------------------------------

    private void OnOfpChanged(object? sender, EventArgs e) => ProcessTick();

    /// <summary>One evaluation on the flight clock — the simulated time when it is live, the
    /// same clock <see cref="FlightTimesTracker"/> stamps the takeoff with.</summary>
    public void ProcessTick() => ProcessTick(_clock.UtcNowOrReal);

    /// <summary>One evaluation, clock supplied; the timer calls this. Never throws past the
    /// timer thread — a failed tick is logged and the log keeps its last state.</summary>
    public void ProcessTick(DateTimeOffset nowUtc)
    {
        var changed = false;
        lock (_gate)
        {
            try
            {
                var fob = _fuelTotal.RawValue is null || _fuelTotal.IsStale ? (double?)null : _fuelTotal.Value;
                var times = _times.Snapshot();
                var before = _store.Snapshot();
                var (next, stamped, skipped) = FuelLogCore.Advance(
                    before, _ofp.Current, _progress.Snapshot().Position, fob, times.TakeoffUtc, nowUtc, times.TakeoffUnknown);
                _store.Update(_ => next);
                changed = !ReferenceEquals(before, next) && before != next;

                foreach (var row in stamped)
                {
                    _logger.LogInformation(
                        "Fuel log: {Fix} passed with {FobKg:F0} kg, plan {PlanKg} ({DeltaKg:+0;-0;0} kg)",
                        row.Ident, row.ActualFobKg, row.PlannedFobKg, row.DeltaKg ?? 0);
                    _eventLog.Record(FixEvent, new
                    {
                        index = row.Index,
                        fix = row.Ident,
                        atUtc = row.ActualTimeUtc,
                        plannedAtUtc = row.PlannedTimeUtc(next.TakeoffUtc),
                        fobKg = row.ActualFobKg,
                        plannedFobKg = row.PlannedFobKg,
                        deltaKg = row.DeltaKg,
                        deltaMinutes = row.DeltaMinutes(next.TakeoffUtc),
                    });
                }

                foreach (var row in skipped)
                {
                    var reason = next.TakeoffUnknown && row.Index == 0
                        ? "takeoff time unknown (app started in flight)"
                        : "passed before the app was watching (restart or gap)";
                    _logger.LogInformation("Fuel log: {Fix} not recorded — {Reason}", row.Ident, reason);
                    _eventLog.Record(SkippedEvent, new { index = row.Index, fix = row.Ident, reason });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fuel log tick failed; the log keeps its last state");
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _ofp.Changed -= OnOfpChanged;
        _timer?.Dispose();
        _fuelTotal.Dispose();
    }
}
