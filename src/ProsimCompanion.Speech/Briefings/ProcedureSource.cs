using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>The identifiers one briefing resolves, with per-field provenance ("fms",
/// "flightJson", "manual", "dfd") for the log — a wrong runway in a brief is diagnosed by
/// which tier supplied it.</summary>
public sealed record ProcedureIdentifiers(
    string? Airport,
    string? Runway,
    string? Sid,
    string? Star,
    string? Approach,
    IReadOnlyDictionary<string, string> Provenance);

/// <summary>
/// Resolves briefing procedure identifiers per <see cref="ProcedureSourceMode"/> (Prosim2FO
/// semantics). Auto walks field by field: FMS (flightPlanXml parse, then the configured
/// per-field datarefs) → SayIntentions flight.json → the manual entries; the arrival approach
/// gets a fourth tier — the top-ranked (ILS-first) published DFD approach for the resolved
/// runway, because the FMS route never carries the approach. The parsed plan is cached by
/// the raw XML string so an unchanged plan is not re-parsed at every brief.
/// </summary>
public sealed class ProcedureSource : IDisposable
{
    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly IProsimDataRefs _dataRefs;
    private readonly DfdNavDataProvider _navData;
    private readonly ILogger<ProcedureSource> _logger;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private IDataRefSubscription<string?>? _flightPlanXml;
    private string? _lastXml;
    private FmsPlan _cachedPlan = FmsPlan.Empty;

    public ProcedureSource(
        IOptionsMonitor<BriefingOptions> options,
        IProsimDataRefs dataRefs,
        DfdNavDataProvider navData,
        ILogger<ProcedureSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _dataRefs = dataRefs;
        _navData = navData;
        _logger = logger;
    }

    public ProcedureIdentifiers Resolve(bool departure)
        => Resolve(departure, FlightJsonRouteReader.Read());

    /// <summary>Testable core — the flight.json tier is injected.</summary>
    public ProcedureIdentifiers Resolve(bool departure, FlightJsonRoute flightJson)
    {
        ArgumentNullException.ThrowIfNull(flightJson);
        var options = _options.CurrentValue;
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);

        var plan = options.ProcedureSource == ProcedureSourceMode.Auto ? CurrentPlan() : FmsPlan.Empty;
        var refs = options.FmsDatarefs;

        string? Field(string name, string? fromPlan, string fmsDataref, string? fromFlightJson, string manual)
        {
            (string? Value, string Tier)[] tiers = options.ProcedureSource switch
            {
                ProcedureSourceMode.Manual =>
                    [(manual, "manual")],
                ProcedureSourceMode.FlightJson =>
                    [(fromFlightJson, "flightJson")],
                _ =>
                [
                    (fromPlan, "fms"),
                    (ReadDataref(fmsDataref), "fms"),
                    (fromFlightJson, "flightJson"),
                    (manual, "manual"),
                ],
            };

            foreach (var (value, tier) in tiers)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    provenance[name] = tier;
                    return value.Trim();
                }
            }

            return null;
        }

        string? airport, runway, sid = null, star = null, approach = null;
        if (departure)
        {
            airport = Field("airport", plan.Origin, refs.OriginIcao,
                flightJson.Origin ?? flightJson.CurrentAirport, options.DepartureAirport);
            runway = Field("runway", plan.OriginRunway, refs.DepartureRunway,
                flightJson.DepartureRunway, options.DepartureRunway);
            sid = Field("sid", plan.Sid, refs.Sid, flightJson.Sid, options.DepartureSid);
        }
        else
        {
            airport = Field("airport", plan.Destination, refs.DestinationIcao,
                flightJson.Destination, options.ArrivalAirport);
            runway = Field("runway", plan.DestinationRunway, refs.ArrivalRunway,
                flightJson.ArrivalRunway, options.ArrivalRunway);
            star = Field("star", plan.Star, refs.Star, flightJson.Star, options.ArrivalStar);
            // The FMS route doesn't carry the approach; flight.json has no field for it either.
            approach = Field("approach", null, refs.Approach, null, options.ArrivalApproach);
            if (approach is null && airport is not null && runway is not null)
            {
                // DFD tier: auto-fill the top-ranked (ILS-first) published approach.
                var candidates = _navData.ApproachesForRunway(airport, runway);
                approach = candidates.Count > 0 ? candidates[0].Identifier : null;
                if (approach is not null)
                {
                    provenance["approach"] = "dfd";
                }
            }
        }

        _logger.LogInformation("Procedure source ({Role}, {Mode}): {Airport}/{Runway} sid={Sid} star={Star} appr={Appr} [{Provenance}]",
            departure ? "departure" : "arrival", options.ProcedureSource,
            airport ?? "-", runway ?? "-", sid ?? "-", star ?? "-", approach ?? "-",
            string.Join(", ", provenance.Select(p => $"{p.Key}={p.Value}")));
        return new ProcedureIdentifiers(airport, runway, sid, star, approach, provenance);
    }

    public void Dispose()
    {
        _flightPlanXml?.Dispose();
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
    }

    private FmsPlan CurrentPlan()
    {
        _flightPlanXml ??= _dataRefs.Subscribe(ProsimDataRefNames.FmsFlightPlanXml);
        var xml = _flightPlanXml.Value;
        if (string.IsNullOrWhiteSpace(xml))
        {
            return FmsPlan.Empty;
        }

        lock (_gate)
        {
            if (xml != _lastXml)
            {
                _lastXml = xml;
                _cachedPlan = FmsPlanParser.Parse(xml);
                _logger.LogInformation("FMS plan: {Origin}/{DepRwy} {Sid} → {Dest}/{ArrRwy} {Star} alt {Alt}",
                    _cachedPlan.Origin ?? "-", _cachedPlan.OriginRunway ?? "-", _cachedPlan.Sid ?? "-",
                    _cachedPlan.Destination ?? "-", _cachedPlan.DestinationRunway ?? "-",
                    _cachedPlan.Star ?? "-", _cachedPlan.Alternate ?? "-");
            }

            return _cachedPlan;
        }
    }

    private string? ReadDataref(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (!_reads.TryGetValue(name, out var read))
        {
            // Escape hatch (#83): the per-field FMS dataref names come from the user's
            // briefing settings (BriefingOptions.FmsDatarefs) — they only exist at runtime.
            read = _dataRefs.SubscribeDynamic(name, DataRefTier.Infrequent);
            _reads[name] = read;
        }

        return read.GetValue<string?>(null);
    }
}
