using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The flight-state snapshot (issue #167): what an app restart in flight used to lose —
/// the OFP, the block/flight stamps, the fuel log, the minima, the chrono, the cabin and
/// notification once-per-flight latches — kept in one file and taken back at the next start.
/// <para>
/// <b>Restore</b> waits for the evidence (<see cref="FlightStateSnapshotRules"/>): the
/// aircraft title and the EFB's plan flag arrive from ProSim seconds after startup, and
/// nothing is restored or written until they say which flight this is. Parts come back in
/// their declared order, each behind its own guard; one log line per part and one
/// <c>flight-state-restored</c> session event.
/// </para>
/// <para>
/// <b>Write</b> on any part's change, debounced to at most one write every
/// <see cref="WriteDelay"/>, atomically. <b>Invalidate</b> (delete) on a new OFP request id,
/// a ColdAndDark commit, the cycle reset the stamps honour, and the finalised Shutdown —
/// after the last two the file stays gone until the next leg shows itself (a new OFP, a
/// push, a taxi), so a restart at the gate between legs restores nothing of the old one.
/// </para>
/// <para>
/// <b>Plan-only fetch</b>: with nothing valid to restore and the aircraft already airborne
/// on an MCDU plan, the OFP is fetched from SimBrief and parsed into the store without
/// touching the EFB (the ground importer's full import is for the ground; in flight it
/// re-rolls the passengers). The OFP page's Fetch button stays the forced full import.
/// </para>
/// </summary>
public sealed class FlightStateSnapshotService : IStartupModule, IDisposable
{
    public const string RestoredEvent = "flight-state-restored";
    public const string PlanFetchedEvent = "ofp.fetched-in-flight";
    public static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PlanFetchRetry = TimeSpan.FromSeconds(60);
    private const int PlanFetchMaxAttempts = 3;

    private readonly IReadOnlyList<IFlightStatePart> _parts;
    private readonly FlightStateSnapshotStore _file;
    private readonly OfpStore _ofp;
    private readonly IFlightPhaseSource _flight;
    private readonly GroundOpsSignals _signals;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<FlightStateOptions> _options;
    private readonly ILogger<FlightStateSnapshotService> _logger;
    private readonly SessionFinalizer? _finalizer;
    private readonly ISimbriefImporter? _importer;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IDataRefSubscription<string?>? _aircraftTitle;
    private readonly IDataRefSubscription<bool>? _planImported;
    private readonly IDataRefSubscription<string?>? _fmsOrigin;
    private readonly IDataRefSubscription<string?>? _fmsDestination;
    private readonly object _gate = new();
    private readonly Timer _writeTimer;

    private FlightStateSnapshotFile? _loaded;
    private bool _decided;
    private bool _restored;
    private bool _writesSuspended;
    private bool _dirty;
    private bool _started;
    private bool _disposed;
    private string? _lastOfpRequestId;
    private int _planFetchAttempts;
    private DateTimeOffset _lastPlanFetch = DateTimeOffset.MinValue;
    private bool _planFetchRunning;

    public FlightStateSnapshotService(
        IEnumerable<IFlightStatePart> parts,
        FlightStateSnapshotStore file,
        OfpStore ofp,
        IFlightPhaseSource flight,
        GroundOpsSignals signals,
        JsonlEventLog eventLog,
        IOptionsMonitor<FlightStateOptions> options,
        ILogger<FlightStateSnapshotService> logger,
        SessionFinalizer? finalizer = null,
        ISimbriefImporter? importer = null,
        IProsimDataRefs? dataRefs = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _parts = parts.OrderBy(p => p.RestoreOrder).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        _file = file;
        _ofp = ofp;
        _flight = flight;
        _signals = signals;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
        _finalizer = finalizer;
        _importer = importer;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _writeTimer = new Timer(_ => WriteNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (dataRefs is not null)
        {
            _aircraftTitle = dataRefs.Subscribe(ProsimDataRefNames.AircraftTitle);
            _planImported = dataRefs.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported);
            _fmsOrigin = dataRefs.Subscribe(ProsimDataRefNames.FmsOrigin);
            _fmsDestination = dataRefs.Subscribe(ProsimDataRefNames.FmsDestination);
        }
    }

    /// <summary>True once the startup check has run to a verdict (tests and the status line).</summary>
    public bool Decided
    {
        get
        {
            lock (_gate)
            {
                return _decided;
            }
        }
    }

    /// <summary>True when this session took state back from a snapshot.</summary>
    public bool Restored
    {
        get
        {
            lock (_gate)
            {
                return _restored;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        if (!_options.CurrentValue.SnapshotEnabled)
        {
            _logger.LogInformation("Flight-state snapshot is off (flightState.snapshotEnabled) — nothing is restored or written");
            return;
        }

        _lastOfpRequestId = _ofp.Current?.RequestId;
        _loaded = _file.Load();
        if (_loaded is null)
        {
            lock (_gate)
            {
                _decided = true;
            }

            _logger.LogDebug("No flight-state snapshot to restore");
        }
        else
        {
            _logger.LogInformation(
                "Flight-state snapshot found ({Origin}-{Destination} {FlightNumber}, written {WrittenUtc:yyyy-MM-dd HH:mm}Z) — waiting for the aircraft and EFB to confirm the flight",
                _loaded.Identity.OriginIcao, _loaded.Identity.DestinationIcao, _loaded.Identity.FlightNumber, _loaded.WrittenUtc);
        }

        foreach (var part in _parts)
        {
            part.Changed += OnPartChanged;
        }

        _flight.PhaseChanged += OnPhaseChanged;
        _signals.FlightCycleReset += OnFlightCycleReset;
        if (_finalizer is not null)
        {
            _finalizer.Finalized += OnShutdownFinalized;
        }

        foreach (var subscription in Evidence())
        {
            subscription.ValueChanged += OnEvidenceChanged;
        }

        EvaluateRestore();
    }

    private IEnumerable<IDataRefSubscription> Evidence()
    {
        if (_aircraftTitle is not null)
        {
            yield return _aircraftTitle;
        }

        if (_planImported is not null)
        {
            yield return _planImported;
        }

        if (_fmsOrigin is not null)
        {
            yield return _fmsOrigin;
        }

        if (_fmsDestination is not null)
        {
            yield return _fmsDestination;
        }
    }

    private void OnEvidenceChanged(object? sender, EventArgs e) => EvaluateRestore();

    /// <summary>Runs the startup rule against the evidence received so far. Idempotent once
    /// decided; exposed so a test can drive it without ProSim pushes.</summary>
    public void EvaluateRestore()
    {
        FlightStateSnapshotFile? snapshot;
        FlightStateRestoreVerdict verdict;
        string reason;
        lock (_gate)
        {
            if (_decided || _loaded is null)
            {
                return;
            }

            snapshot = _loaded;
            var planImported = _planImported is { RawValue: not null } imported ? imported.Value : (bool?)null;
            (verdict, reason) = FlightStateSnapshotRules.Validate(
                snapshot,
                _clock(),
                TimeSpan.FromHours(Math.Max(0.1, _options.CurrentValue.SnapshotMaxAgeHours)),
                planImported,
                _fmsOrigin is { RawValue: not null } o ? o.Value : null,
                _fmsDestination is { RawValue: not null } d ? d.Value : null,
                _aircraftTitle is { RawValue: not null } t ? t.Value : null);
            if (verdict == FlightStateRestoreVerdict.Wait)
            {
                return;
            }

            // A cold and dark aircraft is a new flight whatever the EFB still says: the
            // previous leg's minima, log and latches have no business in it.
            if (verdict == FlightStateRestoreVerdict.Restore && _flight.CurrentPhase == FlightPhase.ColdAndDark)
            {
                (verdict, reason) = (FlightStateRestoreVerdict.Discard, "aircraft is cold and dark — a new flight");
            }

            _decided = true;
            _loaded = null;
        }

        try
        {
            if (verdict == FlightStateRestoreVerdict.Restore)
            {
                ApplyRestore(snapshot, reason);
            }
            else
            {
                _logger.LogInformation("Flight-state snapshot discarded: {Reason}", reason);
                _eventLog.Record("flight-state-discarded", new { reason });
                _file.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flight-state restore failed — continuing without it");
        }

        MaybeFetchPlanOnly();
    }

    private void ApplyRestore(FlightStateSnapshotFile snapshot, string reason)
    {
        var items = new List<string>();
        foreach (var part in _parts)
        {
            if (!snapshot.Parts.TryGetValue(part.Key, out var node) || node is null)
            {
                continue;
            }

            try
            {
                part.Restore(node);
                items.Add(part.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flight-state part {Part} could not be restored — skipped", part.Key);
            }
        }

        var ageMinutes = Math.Round((_clock() - snapshot.WrittenUtc).TotalMinutes);
        lock (_gate)
        {
            _restored = items.Count > 0;
            _lastOfpRequestId = _ofp.Current?.RequestId;
        }

        _logger.LogInformation(
            "Flight state restored from the snapshot written {AgeMinutes} min ago ({Reason}): {Items}",
            ageMinutes, reason, items.Count == 0 ? "nothing to restore" : string.Join(", ", items));
        _eventLog.Record(RestoredEvent, new
        {
            items,
            snapshotAgeMinutes = ageMinutes,
            reason,
            sessionId = snapshot.SessionId,
            origin = snapshot.Identity.OriginIcao,
            destination = snapshot.Identity.DestinationIcao,
            flightNumber = snapshot.Identity.FlightNumber,
        });
    }

    // ---- writes -------------------------------------------------------------------------------

    private void OnPartChanged()
    {
        var ofpRequestId = _ofp.Current?.RequestId;
        lock (_gate)
        {
            if (!_decided || _disposed)
            {
                return;
            }

            // A new plan is a new leg: the old file goes at once, the new state follows.
            if (!string.IsNullOrEmpty(ofpRequestId) && !string.Equals(ofpRequestId, _lastOfpRequestId, StringComparison.Ordinal))
            {
                var hadPrevious = _lastOfpRequestId is not null;
                _lastOfpRequestId = ofpRequestId;
                if (hadPrevious)
                {
                    _file.Clear();
                    _logger.LogInformation("Flight-state snapshot invalidated: new OFP {RequestId}", ofpRequestId);
                }

                _writesSuspended = false;
            }

            if (_writesSuspended)
            {
                return;
            }

            _dirty = true;
            _writeTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void WriteNow()
    {
        try
        {
            lock (_gate)
            {
                if (!_dirty || _writesSuspended || !_decided || _disposed)
                {
                    return;
                }

                _dirty = false;
            }

            _file.Save(Build());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flight-state snapshot write failed");
        }
    }

    /// <summary>The document as the parts see it now. Exposed for tests.</summary>
    public FlightStateSnapshotFile Build()
    {
        var parts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var part in _parts)
        {
            try
            {
                if (part.Capture() is { } node)
                {
                    parts[part.Key] = node;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Flight-state part {Part} could not be captured — left out", part.Key);
            }
        }

        var ofp = _ofp.Current;
        return new FlightStateSnapshotFile(
            FlightStateSnapshotFile.CurrentVersion,
            _clock(),
            System.IO.Path.GetFileNameWithoutExtension(_eventLog.Path),
            new FlightStateIdentity(
                ofp?.RequestId,
                ofp?.OriginIcao,
                ofp?.DestinationIcao,
                ofp?.FlightNumber,
                _aircraftTitle is { RawValue: not null } title ? title.Value : null),
            parts);
    }

    // ---- invalidation -------------------------------------------------------------------------

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current == FlightPhase.ColdAndDark)
        {
            Invalidate("ColdAndDark", suspend: true);
        }
        else if (e.Current is FlightPhase.PushbackAndStart or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll)
        {
            // The next leg is under way: the file may be written again.
            lock (_gate)
            {
                _writesSuspended = false;
            }
        }

        MaybeFetchPlanOnly();
    }

    /// <summary>The cycle reset the stamps honour (not the one on the Shutdown edge, which
    /// <see cref="FlightTimesCore.ResetForNewCycle"/> ignores): a leg is starting over.</summary>
    private void OnFlightCycleReset()
    {
        if (_flight.CurrentPhase is FlightPhase.LandingRollout or FlightPhase.TaxiIn or FlightPhase.Shutdown)
        {
            return;
        }

        Invalidate("flight cycle reset", suspend: false);
    }

    private void OnShutdownFinalized() => Invalidate("shutdown finalised", suspend: true);

    private void Invalidate(string trigger, bool suspend)
    {
        lock (_gate)
        {
            if (!_decided || _disposed)
            {
                return;
            }

            _dirty = false;
            _writeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _writesSuspended = suspend;
        }

        _file.Clear();
        _logger.LogInformation("Flight-state snapshot invalidated: {Trigger}{Suspended}", trigger,
            suspend ? " — not written again until the next leg starts" : "");
    }

    // ---- plan-only fetch ----------------------------------------------------------------------

    private void MaybeFetchPlanOnly()
    {
        if (_importer is null || _fmsOrigin is null || _fmsDestination is null)
        {
            return;
        }

        string? origin;
        string? destination;
        lock (_gate)
        {
            if (!_decided || _planFetchRunning || _ofp.Current is not null || !_flight.CurrentPhase.IsAirborne())
            {
                return;
            }

            origin = _fmsOrigin.Value;
            destination = _fmsDestination.Value;
            if (!FmsPlanRules.IsPlanPresent(origin, destination))
            {
                return;
            }

            var now = _clock();
            if (_planFetchAttempts >= PlanFetchMaxAttempts || now - _lastPlanFetch < PlanFetchRetry)
            {
                return;
            }

            _planFetchAttempts++;
            _lastPlanFetch = now;
            _planFetchRunning = true;
        }

        _ = Task.Run(() => FetchPlanOnlyAsync(origin!, destination!));
    }

    private async Task FetchPlanOnlyAsync(string fmsOrigin, string fmsDestination)
    {
        try
        {
            _logger.LogInformation(
                "App started in flight with no snapshot to restore and an MCDU plan {Origin}-{Destination}: fetching the OFP from SimBrief (plan only)",
                fmsOrigin, fmsDestination);
            var ofp = await _importer!.TryFetchPlanOnlyAsync("flight-state (in-flight start)").ConfigureAwait(false);
            if (ofp is null)
            {
                return;
            }

            if (!string.Equals(ofp.OriginIcao, fmsOrigin, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ofp.DestinationIcao, fmsDestination, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SimBrief OFP {OfpOrigin}-{OfpDestination} is not the MCDU plan {FmsOrigin}-{FmsDestination} — not loaded; use Fetch OFP for a full import",
                    ofp.OriginIcao, ofp.DestinationIcao, fmsOrigin, fmsDestination);
                return;
            }

            if (_ofp.Current is not null)
            {
                return; // something else (the pilot's Fetch) got there first
            }

            _ofp.Set(ofp);
            _logger.LogInformation(
                "OFP fetched after an in-flight start (plan only): {Origin}-{Destination} {FlightNumber}, request {RequestId} — EFB untouched",
                ofp.OriginIcao, ofp.DestinationIcao, ofp.FlightNumber, ofp.RequestId);
            _eventLog.Record(PlanFetchedEvent, new
            {
                requestId = ofp.RequestId,
                origin = ofp.OriginIcao,
                destination = ofp.DestinationIcao,
                flightNumber = ofp.FlightNumber.Length > 0 ? ofp.FlightNumber : null,
                phase = _flight.CurrentPhase.ToString(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "In-flight OFP fetch failed");
        }
        finally
        {
            lock (_gate)
            {
                _planFetchRunning = false;
            }
        }
    }

    public void Dispose()
    {
        bool flush;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            flush = _dirty && _decided && !_writesSuspended;
            _disposed = true;
        }

        _writeTimer.Dispose();
        foreach (var part in _parts)
        {
            part.Changed -= OnPartChanged;
        }

        _flight.PhaseChanged -= OnPhaseChanged;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        if (_finalizer is not null)
        {
            _finalizer.Finalized -= OnShutdownFinalized;
        }

        foreach (var subscription in Evidence())
        {
            subscription.ValueChanged -= OnEvidenceChanged;
        }

        if (flush)
        {
            // A clean exit keeps the last state: the next start is the restart case.
            try
            {
                _file.Save(Build());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Flight-state snapshot final write failed");
            }
        }

        _aircraftTitle?.Dispose();
        _planImported?.Dispose();
        _fmsOrigin?.Dispose();
        _fmsDestination?.Dispose();
    }
}
