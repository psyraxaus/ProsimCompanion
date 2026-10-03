using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Feeds <see cref="FlightProgressCore"/> once a second and publishes to
/// <see cref="FlightProgressStore"/> (issue #145). Owns the airport coordinates of the
/// current OFP: resolved once per OFP through <see cref="AirportLocator"/> (gateway first,
/// Navigraph DFD next), retried a few times while the origin or destination is still unknown
/// — the gateway comes up after the SDK, and a DFD can be configured mid-session.
/// Degrade-not-fail: no position, no airport coordinates or no ProSim simply leaves every
/// figure on its time-based fallback. Startup module; never writes to the aircraft.
/// </summary>
public sealed class FlightProgressService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AirportRetryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProgressEventInterval = TimeSpan.FromMinutes(5);
    private const int MaxAirportAttempts = 10;

    private readonly IFlightPhaseSource _flight;
    private readonly FlightTimesStore _times;
    private readonly OfpStore _ofp;
    private readonly GateStatusStore _gateStatus;
    private readonly GroundOpsSignals _signals;
    private readonly AirportLocator _locator;
    private readonly ISimClock _clock;
    private readonly IOptionsMonitor<FlightStatusOptions> _options;
    private readonly FlightProgressStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FlightProgressService> _logger;
    private readonly IDataRefSubscription<double> _vnavDistanceRemaining;
    private readonly IDataRefSubscription<int> _fmsTimeToDestination;
    private readonly FlightProgressCore _core = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Timer _timer;

    private AirportSet _airports = AirportSet.None;
    private DateTimeOffset _lastAirportAttempt = DateTimeOffset.MinValue;
    private int _airportAttempts;
    private int _resolving;
    private int _ticking;
    private ProgressBasis _loggedFractionBasis = ProgressBasis.None;
    private ProgressBasis _loggedEtaBasis = ProgressBasis.None;
    private DateTimeOffset _lastProgressEvent = DateTimeOffset.MinValue;

    public FlightProgressService(
        IFlightPhaseSource flight,
        FlightTimesStore times,
        OfpStore ofp,
        GateStatusStore gateStatus,
        GroundOpsSignals signals,
        AirportLocator locator,
        IProsimDataRefs dataRefs,
        ISimClock clock,
        IOptionsMonitor<FlightStatusOptions> options,
        FlightProgressStore store,
        JsonlEventLog eventLog,
        ILogger<FlightProgressService> logger)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(gateStatus);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _times = times;
        _ofp = ofp;
        _gateStatus = gateStatus;
        _signals = signals;
        _locator = locator;
        _clock = clock;
        _options = options;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;

        // Recorded only (see the catalog notes): one flight settles what they measure.
        _vnavDistanceRemaining = dataRefs.Subscribe(ProsimDataRefNames.VnavDistanceRemaining);
        _fmsTimeToDestination = dataRefs.Subscribe(ProsimDataRefNames.FmsTimeToDest);

        _signals.FlightCycleReset += OnFlightCycleReset;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TickInterval);
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _core.Reset();
        }
    }

    private void Tick()
    {
        if (_shutdown.IsCancellationRequested || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var ofp = _ofp.Current;
            var airports = CurrentAirports(ofp);
            var view = _flight.Snapshot();
            var inputs = new FlightProgressInputs(
                view.Phase, view.IsLive, view.Data, _times.Snapshot(), ofp, _gateStatus.Snapshot().StdUtc,
                airports.Origin, airports.Destination, airports.Alternate);

            FlightProgressSnapshot snapshot;
            TodApproaching? tod;
            lock (_gate)
            {
                (snapshot, tod) = _core.Evaluate(inputs, _clock.UtcNowOrReal, _options.CurrentValue.TodLeadMinutes);
            }

            _store.Update(_ => snapshot);
            Report(view, snapshot, tod);
        }
        catch (Exception ex)
        {
            // Runs on a timer thread: anything that escapes terminates the process.
            _logger.LogWarning(ex, "Flight progress tick failed; the store keeps its last snapshot");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void Report(FlightStateView view, FlightProgressSnapshot snapshot, TodApproaching? tod)
    {
        var basisChanged = snapshot.FractionBasis != _loggedFractionBasis || snapshot.EtaBasis != _loggedEtaBasis;
        if (basisChanged)
        {
            _logger.LogInformation(
                "Flight progress: fraction {FractionBasis} (was {PreviousFraction}), ETA {EtaBasis} (was {PreviousEta}) — "
                + "position {HasPosition}, origin {Origin}, destination {Destination}, {ToGoNm} nm to go direct",
                snapshot.FractionBasis, _loggedFractionBasis, snapshot.EtaBasis, _loggedEtaBasis,
                snapshot.Position is not null,
                snapshot.Origin?.Source ?? "unknown", snapshot.Destination?.Source ?? "unknown",
                snapshot.DistanceToGoNm);
            _loggedFractionBasis = snapshot.FractionBasis;
            _loggedEtaBasis = snapshot.EtaBasis;
        }

        var now = DateTimeOffset.UtcNow;
        var heartbeat = view.Phase.IsAirborne() && now - _lastProgressEvent >= ProgressEventInterval;
        if (basisChanged || heartbeat || tod is not null)
        {
            _lastProgressEvent = now;
            _eventLog.Record("flight-progress", new
            {
                phase = view.Phase.ToString(),
                fractionBasis = snapshot.FractionBasis.ToString(),
                etaBasis = snapshot.EtaBasis.ToString(),
                lat = snapshot.Position?.LatitudeDeg,
                lon = snapshot.Position?.LongitudeDeg,
                routeNm = snapshot.RouteDistanceNm,
                flownNm = snapshot.DistanceFlownNm,
                toGoNm = snapshot.DistanceToGoNm,
                crossTrackNm = snapshot.CrossTrackNm,
                fraction = snapshot.Fraction,
                etaUtc = snapshot.EtaUtc,
                minutesToTod = snapshot.MinutesToTod,
                groundSpeedKt = view.Data is { } data ? Math.Round(data.GroundSpeedKt) : (double?)null,
                // Calibration only (issue #145): what do these two refs actually measure?
                vnavDistanceRemainingRaw = RawVnavDistance(),
                fmsTimeToDestSecRaw = RawFmsTimeToDestination(),
            });
        }

        if (tod is not null)
        {
            // Consumers (the outbound notifications, #151) get the estimate as a signal; the
            // raise is synchronous and listeners must not block — the dispatcher only queues.
            _signals.RaiseTodApproaching(tod);
            _logger.LogInformation(
                "Top of descent in about {Minutes:F0} min (estimate, 3:1 rule): {ToTodNm:F0} nm to T/D, {ToGoNm:F0} nm to go direct, "
                + "descent {DescentNm:F0} nm from {CruiseAltFt:F0} ft to {ElevationFt} ft at {GroundSpeedKt:F0} kt",
                tod.MinutesToTod, tod.DistanceToTodNm, tod.DistanceToGoNm, tod.DescentDistanceNm,
                tod.CruiseAltitudeFt, tod.DestinationElevationFt, tod.GroundSpeedKt);
            _eventLog.Record("tod-approaching", new
            {
                minutesToTod = Math.Round(tod.MinutesToTod, 1),
                distanceToTodNm = Math.Round(tod.DistanceToTodNm, 1),
                distanceToGoNm = Math.Round(tod.DistanceToGoNm, 1),
                descentDistanceNm = Math.Round(tod.DescentDistanceNm, 1),
                cruiseAltitudeFt = Math.Round(tod.CruiseAltitudeFt),
                destinationElevationFt = tod.DestinationElevationFt,
                groundSpeedKt = Math.Round(tod.GroundSpeedKt),
                leadMinutes = tod.LeadMinutes,
                estimate = true,
                method = "3:1 rule on the great-circle direct distance",
                destination = snapshot.Destination?.Icao,
                vnavDistanceRemainingRaw = RawVnavDistance(),
                fmsTimeToDestSecRaw = RawFmsTimeToDestination(),
            });
        }
    }

    private double? RawVnavDistance()
        => _vnavDistanceRemaining.RawValue is null || _vnavDistanceRemaining.IsStale
            || !double.IsFinite(_vnavDistanceRemaining.Value)
            ? null
            : Math.Round(_vnavDistanceRemaining.Value, 1);

    private int? RawFmsTimeToDestination()
        => _fmsTimeToDestination.RawValue is null || _fmsTimeToDestination.IsStale
            ? null
            : _fmsTimeToDestination.Value;

    // ---- airport coordinates: once per OFP, retried while incomplete ----

    private AirportSet CurrentAirports(OfpData? ofp)
    {
        var key = AirportSet.KeyOf(ofp);
        AirportSet current;
        lock (_gate)
        {
            if (_airports.Key != key)
            {
                _airports = new AirportSet(key, null, null, null);
                _airportAttempts = 0;
                _lastAirportAttempt = DateTimeOffset.MinValue;
            }

            current = _airports;
        }

        if (ofp is not null && !current.Complete(ofp)
            && _airportAttempts < MaxAirportAttempts
            && DateTimeOffset.UtcNow - _lastAirportAttempt >= AirportRetryInterval
            && Interlocked.Exchange(ref _resolving, 1) == 0)
        {
            _airportAttempts++;
            _lastAirportAttempt = DateTimeOffset.UtcNow;
            _ = ResolveAsync(ofp, current, _airportAttempts);
        }

        return current;
    }

    private async Task ResolveAsync(OfpData ofp, AirportSet known, int attempt)
    {
        try
        {
            var token = _shutdown.Token;
            var origin = known.Origin ?? await _locator.FindAsync(ofp.OriginIcao, token).ConfigureAwait(false);
            var destination = known.Destination ?? await _locator.FindAsync(ofp.DestinationIcao, token).ConfigureAwait(false);
            var alternate = known.Alternate ?? await _locator.FindAsync(ofp.AlternateIcao, token).ConfigureAwait(false);
            var resolved = new AirportSet(known.Key, origin, destination, alternate);

            var changed = false;
            lock (_gate)
            {
                // The OFP may have changed while the lookups ran: never install a stale set.
                if (_airports.Key == known.Key && _airports != resolved)
                {
                    _airports = resolved;
                    changed = true;
                }
            }

            if (changed)
            {
                _logger.LogInformation(
                    "Airport coordinates (attempt {Attempt}): {OriginIcao} {Origin}, {DestinationIcao} {Destination}, alternate {AlternateIcao} {Alternate}",
                    attempt, ofp.OriginIcao, Describe(origin), ofp.DestinationIcao, Describe(destination),
                    ofp.AlternateIcao.Length > 0 ? ofp.AlternateIcao : "none", Describe(alternate));
                _eventLog.Record("airport-coordinates", new
                {
                    attempt,
                    origin = Payload(ofp.OriginIcao, origin),
                    destination = Payload(ofp.DestinationIcao, destination),
                    alternate = ofp.AlternateIcao.Length > 0 ? Payload(ofp.AlternateIcao, alternate) : null,
                });
            }
            else if (!resolved.Complete(ofp) && attempt == MaxAirportAttempts)
            {
                _logger.LogWarning(
                    "Airport coordinates still unknown for {OriginIcao} / {DestinationIcao} after {Attempts} attempts — "
                    + "flight progress stays time-based until the next OFP (needs the ProSim gateway or a Navigraph DFD database)",
                    ofp.OriginIcao, ofp.DestinationIcao, attempt);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Airport coordinate lookup failed");
        }
        finally
        {
            Interlocked.Exchange(ref _resolving, 0);
        }
    }

    private static string Describe(AirportLocation? location)
        => location is null
            ? "unknown"
            : FormattableString.Invariant(
                $"{location.Position.LatitudeDeg:F4},{location.Position.LongitudeDeg:F4} ({location.Source})");

    private static object Payload(string icao, AirportLocation? location) => new
    {
        icao,
        found = location is not null,
        lat = location is null ? (double?)null : Math.Round(location.Position.LatitudeDeg, 4),
        lon = location is null ? (double?)null : Math.Round(location.Position.LongitudeDeg, 4),
        elevationFt = location?.ElevationFt is { } elevation ? Math.Round(elevation) : (double?)null,
        source = location?.Source,
    };

    public void Dispose()
    {
        _shutdown.Cancel();
        _timer.Dispose();
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _vnavDistanceRemaining.Dispose();
        _fmsTimeToDestination.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>The coordinates resolved for one OFP. The key is the plan identity plus its
    /// three idents, so a re-fetch of the same plan keeps the answers and a new plan (or an
    /// edited alternate) starts over.</summary>
    private sealed record AirportSet(string Key, AirportLocation? Origin, AirportLocation? Destination, AirportLocation? Alternate)
    {
        public static AirportSet None { get; } = new("", null, null, null);

        public static string KeyOf(OfpData? ofp)
            => ofp is null ? "" : $"{ofp.RequestId}|{ofp.OriginIcao}|{ofp.DestinationIcao}|{ofp.AlternateIcao}";

        /// <summary>The alternate is a bonus: only the origin and destination drive retries.</summary>
        public bool Complete(OfpData ofp)
            => (Origin is not null || ofp.OriginIcao.Length == 0)
                && (Destination is not null || ofp.DestinationIcao.Length == 0);
    }
}
