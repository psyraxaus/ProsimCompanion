using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>What a flight-progress figure was computed from.</summary>
public enum ProgressBasis
{
    /// <summary>No figure: neither a position nor the stamps/OFP the fallback needs.</summary>
    None,

    /// <summary>From the aircraft position and the airport coordinates (great-circle direct).</summary>
    Position,

    /// <summary>The time-based fallback: block/flight stamps against the OFP enroute time.</summary>
    Time,
}

/// <summary>
/// Flight progress (CONTEXT.md, issue #145): how far along the leg the aircraft is. Every
/// distance is GREAT-CIRCLE DIRECT between two points — never along the filed route — so
/// "to go" under-reads a route with a dog-leg and surfaces must label it that way. Each
/// figure carries its basis so a surface can say whether it is looking at a position-based
/// value or the time-based fallback.
/// </summary>
public sealed record FlightProgressSnapshot
{
    public static FlightProgressSnapshot Empty { get; } = new();

    /// <summary>The aircraft position the figures were computed from (4 dp); null = none.</summary>
    public GeoPoint? Position { get; init; }

    /// <summary>True track, degrees; null when unknown.</summary>
    public double? TrackTrueDeg { get; init; }

    public AirportLocation? Origin { get; init; }
    public AirportLocation? Destination { get; init; }
    public AirportLocation? Alternate { get; init; }

    /// <summary>Origin → destination, nm. Null until both airports are located.</summary>
    public double? RouteDistanceNm { get; init; }

    /// <summary>Origin → aircraft, nm.</summary>
    public double? DistanceFlownNm { get; init; }

    /// <summary>Aircraft → destination, nm.</summary>
    public double? DistanceToGoNm { get; init; }

    /// <summary>Distance along the origin → destination great circle to the point abeam the
    /// aircraft, and the distance off it (positive = right) — the route strip's two axes.</summary>
    public double? AlongTrackNm { get; init; }
    public double? CrossTrackNm { get; init; }

    /// <summary>Share of the leg done, 0–1.</summary>
    public double? Fraction { get; init; }
    public ProgressBasis FractionBasis { get; init; }

    /// <summary>Position basis: when the aircraft reaches the destination at the present
    /// ground speed. Time basis: the planned on-blocks time (or the actual one once stamped).</summary>
    public DateTimeOffset? EtaUtc { get; init; }
    public ProgressBasis EtaBasis { get; init; }

    /// <summary>Length of the descent by the 3:1 rule, nm; null outside climb/cruise.</summary>
    public double? DescentDistanceNm { get; init; }

    /// <summary>Distance and time to the estimated top of descent; 0 = at or past it. Null
    /// outside climb/cruise or without a position. Always an ESTIMATE (3:1 rule on a direct
    /// distance): it errs early, because the real route is never shorter than the direct line.</summary>
    public double? DistanceToTodNm { get; init; }
    public double? MinutesToTod { get; init; }
}

/// <summary>Live flight progress for every surface (Flight Monitor board, Flight Status, and
/// later the FO and the notification hooks). Written by <see cref="FlightProgressService"/>.</summary>
public sealed class FlightProgressStore : SnapshotStore<FlightProgressSnapshot>
{
    public FlightProgressStore()
        : base(FlightProgressSnapshot.Empty)
    {
    }
}

/// <summary>Everything one progress evaluation reads.</summary>
public sealed record FlightProgressInputs(
    FlightPhase Phase,
    bool IsLive,
    FlightDataSnapshot? Data,
    FlightTimesSnapshot Times,
    OfpData? Ofp,
    DateTimeOffset? StdUtc,
    AirportLocation? Origin,
    AirportLocation? Destination,
    AirportLocation? Alternate);

/// <summary>The once-per-flight "top of descent is close" moment.</summary>
public sealed record TodApproaching(
    double MinutesToTod,
    double DistanceToTodNm,
    double DistanceToGoNm,
    double DescentDistanceNm,
    double CruiseAltitudeFt,
    double? DestinationElevationFt,
    double GroundSpeedKt,
    int LeadMinutes);

/// <summary>
/// Pure progress rules, clock passed in. Position-based when the flight is live, the
/// aircraft has a position and the airports are located; the time-based fallback (the
/// pre-#145 behaviour) otherwise, per figure. The only state is the top-of-descent latch.
/// </summary>
public sealed class FlightProgressCore
{
    /// <summary>Below this ground speed a "distance ÷ speed" time is meaningless.</summary>
    public const double MinGroundSpeedKt = 60;

    /// <summary>An origin and destination closer than this are one airport (a circuit):
    /// distance says nothing about progress, so the time-based fraction is used.</summary>
    private const double MinRouteNm = 1;

    private bool _todFired;
    private bool _departureSeen;
    private double? _lastMinutesToTod;

    /// <summary>Forgets the top-of-descent latch (new flight cycle).</summary>
    public void Reset()
    {
        _todFired = false;
        _departureSeen = false;
        _lastMinutesToTod = null;
    }

    /// <summary>One evaluation. <paramref name="todLeadMinutes"/> ≤ 0 disables the
    /// top-of-descent moment (the figures are still computed).</summary>
    public (FlightProgressSnapshot Snapshot, TodApproaching? Tod) Evaluate(
        FlightProgressInputs inputs, DateTimeOffset nowUtc, int todLeadMinutes)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var data = inputs.IsLive && inputs.Data is { IsValid: true } valid ? valid : null;
        var position = data?.Position;
        var origin = inputs.Origin?.Position;
        var destination = inputs.Destination?.Position;

        double? route = origin is { } o1 && destination is { } d1 ? GreatCircle.DistanceNm(o1, d1) : null;
        double? flown = position is { } p1 && origin is { } o2 ? GreatCircle.DistanceNm(o2, p1) : null;
        double? toGo = position is { } p2 && destination is { } d2 ? GreatCircle.DistanceNm(p2, d2) : null;
        (double AlongNm, double CrossNm)? track = position is { } p3 && origin is { } o3 && destination is { } d3
            ? GreatCircle.AlongCrossTrackNm(o3, d3, p3)
            : null;

        var (fraction, fractionBasis) = Fraction(inputs, route, flown, toGo, nowUtc);
        var (eta, etaBasis) = Eta(inputs, data, toGo, nowUtc);
        var tod = TopOfDescent(inputs, data, toGo);
        var approaching = TodLatch(inputs.Phase, data, toGo, tod, todLeadMinutes);

        var snapshot = new FlightProgressSnapshot
        {
            Position = position is { } p ? new GeoPoint(Math.Round(p.LatitudeDeg, 4), Math.Round(p.LongitudeDeg, 4)) : null,
            TrackTrueDeg = data?.TrackTrueDeg is { } trk ? Math.Round(trk) : null,
            Origin = inputs.Origin,
            Destination = inputs.Destination,
            Alternate = inputs.Alternate,
            RouteDistanceNm = Round1(route),
            DistanceFlownNm = Round1(flown),
            DistanceToGoNm = Round1(toGo),
            AlongTrackNm = Round1(track?.AlongNm),
            CrossTrackNm = Round1(track?.CrossNm),
            Fraction = fraction is { } f ? Math.Round(f, 4) : null,
            FractionBasis = fractionBasis,
            EtaUtc = eta,
            EtaBasis = etaBasis,
            DescentDistanceNm = Round1(tod?.DescentNm),
            DistanceToTodNm = Round1(tod?.ToTodNm),
            MinutesToTod = Round1(tod?.Minutes),
        };
        return (snapshot, approaching);
    }

    // ---- time-based fallback (the pre-#145 Flight Monitor rules, moved here unchanged) ----

    /// <summary>Share of the planned block time flown, 0–1, from the off-blocks stamp and the
    /// OFP's estimated enroute time. Null until off-blocks; a leg that runs long pins at 1.</summary>
    public static double? TimeFraction(FlightTimesSnapshot times, TimeSpan? estimatedEnroute, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.OffBlocksUtc is not { } off || estimatedEnroute is not { } eet || eet <= TimeSpan.Zero)
        {
            return null;
        }

        var end = times.OnBlocksUtc ?? nowUtc;
        return Math.Clamp((end - off) / eet, 0, 1);
    }

    /// <summary>Estimated on-blocks: takeoff + enroute once airborne, else off-blocks +
    /// enroute, else STD + enroute — whichever is the best evidence. The actual on-blocks
    /// stamp wins once it exists.</summary>
    public static DateTimeOffset? TimeEta(FlightTimesSnapshot times, OfpData? ofp, DateTimeOffset? stdUtc)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.OnBlocksUtc is { } on)
        {
            return on;
        }

        if (ofp?.EstimatedEnroute is not { } eet)
        {
            return null;
        }

        var anchor = times.TakeoffUtc ?? times.OffBlocksUtc ?? stdUtc;
        return anchor?.Add(eet);
    }

    /// <summary>The 3:1 rule: three nautical miles over the ground for every thousand feet
    /// to lose. Never negative (a "cruise" below the field is no descent).</summary>
    public static double DescentDistanceNm(double cruiseAltitudeFt, double destinationElevationFt)
        => Math.Max(0, (cruiseAltitudeFt - destinationElevationFt) / 1000.0 * 3.0);

    private static (double?, ProgressBasis) Fraction(
        FlightProgressInputs inputs, double? route, double? flown, double? toGo, DateTimeOffset nowUtc)
    {
        if (route is >= MinRouteNm && flown is { } f && toGo is { } g && f + g > 0)
        {
            // flown ÷ (flown + to go), not flown ÷ route: a SID that leaves the wrong way or
            // a hold off the direct line can make "flown" exceed the route length, and this
            // form stays inside 0–1 and still reads 0 at the origin and 1 at the destination.
            return (Math.Clamp(f / (f + g), 0, 1), ProgressBasis.Position);
        }

        return TimeFraction(inputs.Times, inputs.Ofp?.EstimatedEnroute, nowUtc) is { } time
            ? (time, ProgressBasis.Time)
            : (null, ProgressBasis.None);
    }

    private static (DateTimeOffset?, ProgressBasis) Eta(
        FlightProgressInputs inputs, FlightDataSnapshot? data, double? toGo, DateTimeOffset nowUtc)
    {
        // The leg is over: the stamp is a fact, not an estimate.
        if (inputs.Times.OnBlocksUtc is null
            && inputs.Phase.IsAirborne()
            && toGo is { } g
            && data is { GroundSpeedKt: >= MinGroundSpeedKt } d)
        {
            // Whole minutes: the ground speed jitters every sample and a board that
            // re-renders its ETA each second reads as broken.
            var eta = nowUtc.AddHours(g / d.GroundSpeedKt);
            return (new DateTimeOffset(eta.Ticks - (eta.Ticks % TimeSpan.TicksPerMinute), eta.Offset), ProgressBasis.Position);
        }

        return TimeEta(inputs.Times, inputs.Ofp, inputs.StdUtc) is { } time
            ? (time, ProgressBasis.Time)
            : (null, ProgressBasis.None);
    }

    private static (double DescentNm, double ToTodNm, double Minutes, double CruiseAltFt, double? ElevationFt)? TopOfDescent(
        FlightProgressInputs inputs, FlightDataSnapshot? data, double? toGo)
    {
        if (inputs.Phase is not (FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise)
            || toGo is not { } g
            || data is not { GroundSpeedKt: >= MinGroundSpeedKt } d)
        {
            return null;
        }

        // In the cruise the aircraft IS at the level it will descend from (step climbs
        // included); before it, the planned level is the best evidence — FMS first, OFP next.
        double? cruise = inputs.Phase == FlightPhase.Cruise ? d.AltitudeFt
            : d.FmsCruiseAltFt > 0 ? d.FmsCruiseAltFt
            : inputs.Ofp is { CruiseFlightLevel: > 0 } ofp ? ofp.CruiseFlightLevel * 100.0
            : null;
        if (cruise is not { } altitude)
        {
            return null;
        }

        var descent = DescentDistanceNm(altitude, inputs.Destination?.ElevationFt ?? 0);
        var toTod = Math.Max(0, g - descent);
        return (descent, toTod, toTod / d.GroundSpeedKt * 60.0, altitude, inputs.Destination?.ElevationFt);
    }

    /// <summary>Once per flight, on the downward crossing of the lead time. A restart or a
    /// reconnect INSIDE the lead window does not fire again (no crossing was seen); a hop so
    /// short that the first airborne figure is already inside it fires at once, but only
    /// when this process watched the departure.</summary>
    private TodApproaching? TodLatch(
        FlightPhase phase,
        FlightDataSnapshot? data,
        double? toGo,
        (double DescentNm, double ToTodNm, double Minutes, double CruiseAltFt, double? ElevationFt)? tod,
        int leadMinutes)
    {
        if (phase.IsBeforeTaxiOut() || phase is FlightPhase.TaxiOut or FlightPhase.TakeoffRoll)
        {
            _todFired = false;
            _departureSeen = true;
            _lastMinutesToTod = null;
            return null;
        }

        if (phase is FlightPhase.Descent or FlightPhase.Approach or FlightPhase.LandingRollout
            or FlightPhase.TaxiIn or FlightPhase.Shutdown)
        {
            _departureSeen = false;
            _lastMinutesToTod = null;
            return null;
        }

        if (tod is not { } t || toGo is not { } g || data is null)
        {
            // Position lost: keep the last figure so a regain inside the window still
            // counts as the crossing it is.
            return null;
        }

        TodApproaching? result = null;
        if (!_todFired && leadMinutes > 0 && t.Minutes <= leadMinutes)
        {
            var crossed = _lastMinutesToTod is { } last && last > leadMinutes;
            var shortHop = _lastMinutesToTod is null && _departureSeen;
            if (crossed || shortHop)
            {
                _todFired = true;
                result = new TodApproaching(
                    t.Minutes, t.ToTodNm, g, t.DescentNm, t.CruiseAltFt,
                    t.ElevationFt, data.GroundSpeedKt, leadMinutes);
            }
        }

        _lastMinutesToTod = t.Minutes;
        return result;
    }

    private static double? Round1(double? value) => value is { } v ? Math.Round(v, 1) : null;
}
