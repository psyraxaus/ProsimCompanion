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
/// and the live fuel; each newly stamped fix is a <c>fuel.log.fix</c> session event. Runs
/// with the voice FO off — the page is the consumer — and degrades to an empty log without
/// an OFP, a position or ProSim.
/// </summary>
public sealed class FuelLogService : IStartupModule, IDisposable
{
    public const string FixEvent = "fuel.log.fix";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly FuelLogStore _store;
    private readonly OfpStore _ofp;
    private readonly FlightProgressStore _progress;
    private readonly FlightTimesStore _times;
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
        JsonlEventLog eventLog,
        ILogger<FuelLogService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _ofp = ofp;
        _progress = progress;
        _times = times;
        _eventLog = eventLog;
        _logger = logger;
        _fuelTotal = dataRefs.Subscribe(ProsimDataRefNames.FuelTotal);
    }

    public void Start()
    {
        _ofp.Changed += OnOfpChanged;
        _timer = new Timer(_ => ProcessTick(DateTimeOffset.UtcNow), null, TimeSpan.FromSeconds(3), Poll);
    }

    private void OnOfpChanged(object? sender, EventArgs e) => ProcessTick(DateTimeOffset.UtcNow);

    /// <summary>One evaluation, clock supplied; the timer calls this. Never throws past the
    /// timer thread — a failed tick is logged and the log keeps its last state.</summary>
    public void ProcessTick(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            try
            {
                var fob = _fuelTotal.RawValue is null || _fuelTotal.IsStale ? (double?)null : _fuelTotal.Value;
                var (next, stamped) = FuelLogCore.Advance(
                    _store.Snapshot(), _ofp.Current, _progress.Snapshot().Position, fob, _times.Snapshot().TakeoffUtc, nowUtc);
                _store.Update(_ => next);

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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fuel log tick failed; the log keeps its last state");
            }
        }
    }

    public void Dispose()
    {
        _ofp.Changed -= OnOfpChanged;
        _timer?.Dispose();
        _fuelTotal.Dispose();
    }
}
