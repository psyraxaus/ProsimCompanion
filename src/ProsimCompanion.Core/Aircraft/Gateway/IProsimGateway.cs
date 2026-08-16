namespace ProsimCompanion.Core.Aircraft.Gateway;

/// <summary>
/// Client for the ProSim EFB gateway (HTTP on port 5000 of the ProSim host): GraphQL dataref
/// access plus the REST endpoints for EFB tasks, performance calculations, runways, METAR and
/// failures. Used for gateway-exclusive functionality; plain dataref traffic should prefer the
/// SDK push path (<see cref="IProsimDataRefs"/>).
///
/// Failure convention: methods log and return null/false after retries instead of throwing
/// (degrade-not-fail) — except <see cref="OperationCanceledException"/> for cancellation.
/// </summary>
public interface IProsimGateway
{
    /// <summary>
    /// Cheap single-attempt reachability probe: true when the gateway answered ANY HTTP
    /// response, false on connection refused/timeout. Never retries and never logs above
    /// Debug — built for hold-until-reachable loops (issue #76: ProSim raises its SDK
    /// connection before the port-5000 gateway starts listening, so feature writes fired at
    /// "SDK connected" burned their retry attempts on "actively refused").
    /// </summary>
    Task<bool> IsReachableAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a dataref via GraphQL mutation. The mutation type is chosen from the
    /// runtime type of <paramref name="value"/> (bool/int/float-double/string).</summary>
    Task<bool> WriteDataRefAsync(string name, object value, CancellationToken cancellationToken = default);

    /// <summary>Reads a dataref value via GraphQL query. Returns null on failure.</summary>
    Task<string?> QueryDataRefAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Cancels an in-progress EFB boarding task.</summary>
    Task<bool> CancelBoardingAsync(CancellationToken cancellationToken = default);

    /// <summary>Takeoff performance calculation (V1/VR/V2, FLEX, THS). A calculation the tables
    /// cannot satisfy is reported inside a 200 body via <see cref="CalcVSpeedResult.CalculationError"/>.</summary>
    Task<CalcVSpeedResult?> CalculateVSpeedsAsync(CalcVSpeedRequest request, CancellationToken cancellationToken = default);

    /// <summary>Landing distance required calculation.</summary>
    Task<CalcLdrResponse?> CalculateLandingDistanceAsync(CalcLdrRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runway database for an airport. Returns null on failure.</summary>
    Task<IReadOnlyList<RunwayResponse>?> GetRunwaysAsync(string icao, bool includeIntersections, CancellationToken cancellationToken = default);

    /// <summary>METAR for an airport. A gateway 204 means "no data" — a successful result with
    /// a null <see cref="MetarFetchResult.Metar"/>, deliberately not retried; transport/HTTP
    /// failures carry a <see cref="MetarFetchResult.FailureReason"/> so consumers can say WHY
    /// weather is missing (issue #62). Never returns null.</summary>
    Task<MetarFetchResult> GetMetarAsync(string icao, CancellationToken cancellationToken = default);

    /// <summary>The list of selectable failures (used by the performance calculators).</summary>
    Task<IReadOnlyList<FailuresResponse>?> GetFailuresAsync(CancellationToken cancellationToken = default);
}
