using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Simbrief;

/// <summary>
/// SimBrief OFP fetch + EFB import, following the predecessor's proven recipe: read the pilot id
/// from <c>efb.simbrief.id</c>, fetch the OFP (json=1), then write the booked seat map
/// (improved here to a capacity-proportional distribution instead of the old front-fill, per the
/// owner's CG-realism requirement), the passenger statistics JSON, planned fuel (into BOTH
/// <c>aircraft.refuel.fuelTarget</c> — the refuel target total — and <c>efb.plannedfuel</c>),
/// planned cargo, and finally <c>efb.simbriefPlanImported</c>. Everything decision-logged.
/// </summary>
public sealed class SimbriefImportService : ISimbriefImporter, IDisposable
{
    private const double LbsPerKg = 2.20462;
    private static readonly int[] FallbackZoneCapacities = [24, 30, 36, 42];

    private readonly IProsimGateway _gateway;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<SimbriefImportService> _logger;
    private readonly IDataRefSubscription _pilotId;
    private readonly IDataRefSubscription _planImported;
    private readonly IDataRefSubscription[] _zoneCapacities;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _importLock = new(1, 1);

    public SimbriefImportService(
        IProsimDataRefs prosim,
        IProsimGateway gateway,
        GsxDiagnosticsStore diagnostics,
        ILogger<SimbriefImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _diagnostics = diagnostics;
        _logger = logger;

        _pilotId = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefId, DataRefTier.Infrequent);
        _planImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported, DataRefTier.Infrequent);
        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity, DataRefTier.Infrequent),
        ];

        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Dispose()
    {
        _pilotId.Dispose();
        _planImported.Dispose();
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }
        _http.Dispose();
        _importLock.Dispose();
    }

    public async Task<SimbriefImportOutcome> TryImportAsync(CancellationToken cancellationToken = default)
    {
        if (!await _importLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return SimbriefImportOutcome.FetchFailed; // an import is already running
        }

        try
        {
            if (_planImported.GetValue(false))
            {
                return SimbriefImportOutcome.AlreadyImported;
            }

            var pilotId = _pilotId.GetValue<string?>(null)?.Trim();
            if (string.IsNullOrEmpty(pilotId) || pilotId == "0")
            {
                RecordDecision("simbrief import", "no pilot id in ProSim (efb.simbrief.id) — set it in the ProSim EFB");
                return SimbriefImportOutcome.NoPilotId;
            }

            var ofp = await FetchOfpAsync(pilotId, cancellationToken).ConfigureAwait(false);
            if (ofp is null)
            {
                return SimbriefImportOutcome.FetchFailed;
            }

            return await ImportAsync(ofp, cancellationToken).ConfigureAwait(false)
                ? SimbriefImportOutcome.Imported
                : SimbriefImportOutcome.ImportFailed;
        }
        finally
        {
            _importLock.Release();
        }
    }

    private async Task<JsonObject?> FetchOfpAsync(string pilotId, CancellationToken cancellationToken)
    {
        var idParameter = pilotId.All(char.IsDigit) ? "userid" : "username";
        var url = $"https://www.simbrief.com/api/xml.fetcher.php?json=1&{idParameter}={Uri.EscapeDataString(pilotId)}";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RecordDecision("simbrief import", $"fetch failed ({(int)response.StatusCode}) — is an OFP generated on SimBrief?");
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            RecordDecision("simbrief import", $"fetch failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> ImportAsync(JsonObject ofp, CancellationToken cancellationToken)
    {
        // Values arrive as strings in SimBrief's JSON; parse leniently.
        var fuelRamp = ReadDouble(ofp["fuel"]?["plan_ramp"]);
        var cargo = ReadDouble(ofp["weights"]?["cargo"]);
        var paxCount = (int)ReadDouble(ofp["weights"]?["pax_count"]);
        var units = ReadString(ofp["params"]?["units"]) ?? "kgs";
        var origin = ReadString(ofp["origin"]?["icao_code"]);
        var destination = ReadString(ofp["destination"]?["icao_code"]);

        if (string.Equals(units, "lbs", StringComparison.OrdinalIgnoreCase))
        {
            fuelRamp /= LbsPerKg;
            cargo /= LbsPerKg;
        }

        if (fuelRamp <= 0 && paxCount <= 0)
        {
            RecordDecision("simbrief import", "OFP carries no usable fuel/pax data — aborting import");
            return false;
        }

        // Zone capacities from the live aircraft; predecessor fallback constants otherwise.
        var capacities = _zoneCapacities.Select(zone => zone.GetValue(0)).ToArray();
        if (capacities.Sum() <= 0)
        {
            capacities = FallbackZoneCapacities;
        }
        var totalCapacity = capacities.Sum();

        if (paxCount > totalCapacity)
        {
            // Predecessor rule: substitute a plausible load factor rather than refusing.
            var adjusted = (int)(totalCapacity * (0.75 + (Random.Shared.NextDouble() * 0.20)));
            RecordDecision("simbrief import", $"OFP pax {paxCount} exceeds capacity {totalCapacity} — using plausible load {adjusted}");
            paxCount = adjusted;
        }

        // Booked seat map: capacity-proportional (equal load factor per zone — CG-realistic),
        // deliberately NOT the predecessor's front-fill.
        var bookedMap = SeatMap.SynthesizeBooked(paxCount, capacities);
        var perZone = LoadMath.DistributePax(paxCount, capacities);
        var statistics = JsonSerializer.Serialize(new
        {
            NumOfPaxInBusiness = perZone[0],
            NumOfPaxInEconomy = perZone[1] + perZone[2] + perZone[3],
            NumOfPaxInSection1 = perZone[0],
            NumOfPaxInSection2 = perZone[1],
            NumOfPaxInSection3 = perZone[2] + perZone[3],
            Total = paxCount,
        });

        var ok = await _gateway.WriteDataRefAsync(ProsimDataRefNames.PaxBookedString, SeatMap.Build(bookedMap), cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPassengerStatistics, statistics, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.RefuelFuelTarget, fuelRamp, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPlannedFuel, fuelRamp, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPlannedCargoKg, cargo, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbSimbriefPlanImported, true, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            RecordDecision("simbrief import", "one or more EFB writes failed — see log; will retry");
            return false;
        }

        RecordDecision(
            "simbrief import",
            $"imported {origin}->{destination}: {paxCount} pax (zones {string.Join("/", perZone)}), fuel {fuelRamp:F0} kg, cargo {cargo:F0} kg");
        return true;
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("{Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
