using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>
/// Keeps <see cref="PushbackChoiceStore"/>'s suggestion current while the aircraft is at the
/// departure stand: finds the stand (GSX's loaded parking name when it has one, else the
/// stand nearest the aircraft within <see cref="StandRadiusM"/>), the departure runway (the
/// OFP's planned runway out) and asks <see cref="PushbackAdvisor"/> which push faces it.
/// Re-evaluated on OFP changes, GSX gate-context changes and every <see cref="TickInterval"/>
/// while at the gate — the pilot may be repositioned or the runway re-planned. Startup
/// module: construction wires the events; the host calls <see cref="Start"/>.
/// </summary>
public sealed class PushbackSuggestionService : IStartupModule, IDisposable
{
    /// <summary>How far the aircraft may be from a stand's position to count as parked on it
    /// (a heavy stand's own radius is ~35 m; the GSX detection threshold is 25 m).</summary>
    public const double StandRadiusM = 80;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly PushbackChoiceStore _store;
    private readonly OfpStore _ofp;
    private readonly IFlightPhaseSource _flight;
    private readonly GsxDiagnosticsStore _gsx;
    private readonly IAirportParkingCatalog _parkings;
    private readonly RunwayLocator _runways;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PushbackSuggestionService> _logger;
    private readonly IDepartureRunwaySource? _departureRunway;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _evaluating = new(1, 1);
    private IDisposable? _gsxSubscription;
    private string? _lastKey;
    private Task? _loop;

    /// <param name="departureRunway">The live departure runway (FMS tier from the Speech
    /// pillar); null falls back to the OFP's planned runway out.</param>
    public PushbackSuggestionService(
        PushbackChoiceStore store,
        OfpStore ofp,
        IFlightPhaseSource flight,
        GsxDiagnosticsStore gsx,
        IAirportParkingCatalog parkings,
        RunwayLocator runways,
        JsonlEventLog eventLog,
        ILogger<PushbackSuggestionService> logger,
        IDepartureRunwaySource? departureRunway = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(gsx);
        ArgumentNullException.ThrowIfNull(parkings);
        ArgumentNullException.ThrowIfNull(runways);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _ofp = ofp;
        _flight = flight;
        _gsx = gsx;
        _parkings = parkings;
        _runways = runways;
        _eventLog = eventLog;
        _logger = logger;
        _departureRunway = departureRunway;
    }

    public void Start()
    {
        _ofp.Changed += OnTrigger;
        _gsxSubscription = _gsx.Observe(_ => OnTrigger(this, EventArgs.Empty));
        _loop = Task.Run(LoopAsync);
    }

    public void Dispose()
    {
        _ofp.Changed -= OnTrigger;
        _gsxSubscription?.Dispose();
        _stopping.Cancel();
        _stopping.Dispose();
        _evaluating.Dispose();
    }

    private void OnTrigger(object? sender, EventArgs e) => _ = EvaluateAsync();

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await EvaluateAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One evaluation; public so the OFP page's "Suggest again" and tests can drive it.</summary>
    public async Task EvaluateAsync()
    {
        if (!await _evaluating.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await EvaluateCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pushback suggestion evaluation failed");
        }
        finally
        {
            _evaluating.Release();
        }
    }

    private async Task EvaluateCoreAsync()
    {
        var view = _flight.Snapshot();
        if (!view.Phase.IsAtGate() || !view.IsLive)
        {
            return;
        }

        var ofp = _ofp.Current;
        var origin = ofp?.OriginIcao?.Trim().ToUpperInvariant();
        var runwayIdent = RunwayLocator.NormalizeIdent(ofp?.PlannedRunwayOut);
        var runwaySource = runwayIdent is null ? null : "ofp";

        // The FMS knows better than the OFP once the crew has set the runway ATC gave them.
        if (_departureRunway?.Current() is { } live)
        {
            if (string.IsNullOrEmpty(origin) || string.Equals(origin, live.Airport, StringComparison.OrdinalIgnoreCase))
            {
                origin = live.Airport;
                runwayIdent = live.Runway;
                runwaySource = "fms";
            }
        }

        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        var catalogue = await _parkings.GetAsync(origin, cancellationToken: _stopping.Token).ConfigureAwait(false);
        if (catalogue is null)
        {
            return;
        }

        var stand = FindStand(catalogue, _gsx.Snapshot().GateContextKey, view.Data?.Position);
        if (stand is null)
        {
            return;
        }

        var key = $"{origin}|{stand.Identity.DefaultDisplayName}|{runwayIdent}";
        if (key == _lastKey && _store.Snapshot().Suggestion is not null)
        {
            return;
        }

        var runway = await RunwayAsync(origin, runwayIdent).ConfigureAwait(false);

        var options = PushbackAdvisor.Options(stand, menuEntries: null);
        var suggestion = PushbackAdvisor.Suggest(options, stand.Pose?.Position, runway);
        var previous = _store.Snapshot();
        var changed = key != _lastKey || (suggestion is null) != (previous.Suggestion is null) || previous.Stand != stand.DisplayName;
        _lastKey = key;
        _store.SetSuggestion(suggestion, options, stand.DisplayName, runwayIdent);
        if (!changed)
        {
            return; // same stand, same runway, same (lack of) answer — nothing new to say
        }

        if (suggestion is not null)
        {
            _logger.LogInformation(
                "Pushback suggestion at {Icao} {Stand} for runway {Runway}: {Option} ({Confidence}) — {Reason}",
                origin, stand.DisplayName, runwayIdent ?? "?", suggestion.Option.Label, suggestion.Confidence, suggestion.Reason);
        }
        else
        {
            _logger.LogInformation(
                "No pushback suggestion at {Icao} {Stand}: runway {Runway} {RunwayKnown}, stand position {PosKnown}, {Options} option(s)",
                origin, stand.DisplayName, runwayIdent ?? "?", runway is null ? "unknown" : "known",
                stand.Pose is null ? "unknown" : "known", options.Count);
        }

        _eventLog.Record("pushback-suggestion", new
        {
            airport = origin,
            stand = stand.DisplayName,
            runway = runwayIdent,
            runwayFrom = runwaySource,
            runwayGeometrySource = runway?.Source,
            options = options.Select(o => new { o.Label, kind = o.Kind.ToString(), heading = o.FinalHeadingDeg, o.HeadingSource }).ToList(),
            suggestion = suggestion?.Option.Label,
            confidence = suggestion?.Confidence.ToString(),
            reason = suggestion?.Reason,
            bearingToRunway = suggestion?.BearingToRunwayDeg,
        });
    }

    private (string Key, RunwayGeometry? Geometry)? _runwayCache;

    /// <summary>Runway geometry, remembered per (airport, runway) so the 15 s tick never
    /// re-queries the gateway or the DFD for the same answer; a null answer is retried.</summary>
    private async Task<RunwayGeometry?> RunwayAsync(string icao, string? runwayIdent)
    {
        if (runwayIdent is null)
        {
            return null;
        }

        var key = $"{icao}|{runwayIdent}";
        if (_runwayCache is { } cached && cached.Key == key && cached.Geometry is not null)
        {
            return cached.Geometry;
        }

        var geometry = await _runways.FindAsync(icao, runwayIdent, _stopping.Token).ConfigureAwait(false);
        _runwayCache = (key, geometry);
        return geometry;
    }

    /// <summary>GSX's loaded parking name first (it is exact), else the nearest stand with a
    /// known position within <see cref="StandRadiusM"/> of the aircraft. Public for tests.</summary>
    public static AirportParking? FindStand(AirportParkings catalogue, string? gsxGateContextKey, GeoPoint? aircraft)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (!string.IsNullOrWhiteSpace(gsxGateContextKey)
            && ParkingTokenResolver.Resolve(catalogue, gsxGateContextKey) is { Confidence: ParkingMatchConfidence.Exact } byName)
        {
            return byName.Parking;
        }

        if (aircraft is null)
        {
            return null;
        }

        AirportParking? best = null;
        var bestM = double.MaxValue;
        foreach (var parking in catalogue.Parkings)
        {
            if (parking.Pose is not { } pose)
            {
                continue;
            }

            var distanceM = GreatCircle.DistanceNm(aircraft.Value, pose.Position) * 1852.0;
            if (distanceM < bestM)
            {
                bestM = distanceM;
                best = parking;
            }
        }

        return bestM <= StandRadiusM ? best : null;
    }
}
