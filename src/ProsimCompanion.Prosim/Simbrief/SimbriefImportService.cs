using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Simbrief;

/// <summary>
/// SimBrief OFP fetch + EFB import, following the predecessor's proven recipe: read the pilot id
/// from <c>efb.simbrief.id</c>, fetch the OFP (json=1), then write the booked seat map
/// (improved here to a capacity-proportional distribution instead of the old front-fill, per the
/// owner's CG-realism requirement), the passenger statistics JSON, planned fuel (into BOTH
/// <c>aircraft.refuel.fuelTarget</c> — the refuel target total — and <c>efb.plannedfuel</c>),
/// planned cargo, and finally <c>efb.simbriefPlanImported</c>. Everything decision-logged.
/// Phase 3: the parsed OFP is also published as a typed <see cref="OfpData"/> to the
/// <see cref="OfpStore"/> for the loadsheet pipeline, FMS sync and web pages.
/// </summary>
public sealed class SimbriefImportService : ISimbriefImporter, IDisposable
{
    private const double LbsPerKg = 2.20462;
    private static readonly int[] FallbackZoneCapacities = [24, 30, 36, 42];
    private static readonly TimeSpan FetchRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IProsimGateway _gateway;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly IOptionsMonitor<FlightDataOptions> _flightDataOptions;
    private readonly OfpStore _ofpStore;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<SimbriefImportService> _logger;
    private readonly IDataRefSubscription<string?> _pilotId;
    private readonly IDataRefSubscription<bool> _planImported;
    private readonly IDataRefSubscription<int>[] _zoneCapacities;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _importLock = new(1, 1);

    /// <summary>Randomized figures latched per OFP identity (issue #64) — guarded by
    /// <see cref="_importLock"/>, which serializes every import.</summary>
    private SimbriefRandomizationLatch? _randomizationLatch;

    public SimbriefImportService(
        IProsimDataRefs prosim,
        IProsimGateway gateway,
        IOptionsMonitor<GsxOptions> options,
        IOptionsMonitor<FlightDataOptions> flightDataOptions,
        OfpStore ofpStore,
        GsxDiagnosticsStore diagnostics,
        ILogger<SimbriefImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flightDataOptions);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _options = options;
        _flightDataOptions = flightDataOptions;
        _ofpStore = ofpStore;
        _diagnostics = diagnostics;
        _logger = logger;

        _pilotId = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefId);
        _planImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported);
        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity),
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

    public async Task<SimbriefImportOutcome> TryImportAsync(bool force = false, string source = "unspecified", CancellationToken cancellationToken = default)
    {
        if (!await _importLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return SimbriefImportOutcome.FetchFailed; // an import is already running
        }

        try
        {
            // Attempt-level trace (issue #64): the 6x re-import storm was undiagnosable
            // because nothing recorded WHO fired each import.
            _logger.LogDebug("SimBrief import attempt (source {Source}, force {Force})", source, force);

            if (!force && _planImported.Value)
            {
                return SimbriefImportOutcome.AlreadyImported;
            }

            var pilotId = _pilotId.Value?.Trim();
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

            return await ImportAsync(ofp, force, cancellationToken).ConfigureAwait(false)
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
        var attempts = Math.Max(1, _flightDataOptions.CurrentValue.SimbriefFetchAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (JsonNode.Parse(text) is JsonObject ofp)
                    {
                        return ofp;
                    }
                    RecordDecision("simbrief import", "fetch returned malformed JSON");
                }
                else
                {
                    RecordDecision(
                        "simbrief import",
                        $"fetch failed ({(int)response.StatusCode}, attempt {attempt}/{attempts}) — is an OFP generated on SimBrief?");
                    // A 4xx means "no OFP / bad id" — retrying won't change the answer.
                    if ((int)response.StatusCode is >= 400 and < 500)
                    {
                        return null;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                RecordDecision("simbrief import", $"fetch failed (attempt {attempt}/{attempts}): {ex.Message}");
            }

            if (attempt < attempts)
            {
                await Task.Delay(FetchRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<bool> ImportAsync(JsonObject ofp, bool force, CancellationToken cancellationToken)
    {
        var parsed = ParseOfp(ofp);
        var fuelRamp = parsed.FuelPlanRampKg;
        var cargo = parsed.CargoKg;
        var paxCount = parsed.PaxCount;

        if (fuelRamp <= 0 && paxCount <= 0)
        {
            RecordDecision("simbrief import", "OFP carries no usable fuel/pax data — aborting import");
            return false;
        }

        // Zone capacities from the live aircraft; predecessor fallback constants otherwise.
        var capacities = _zoneCapacities.Select(zone => zone.Value).ToArray();
        if (capacities.Sum() <= 0)
        {
            capacities = FallbackZoneCapacities;
        }

        bool[] bookedMap;
        var ofpKey = SimbriefRandomizationPolicy.OfpKey(
            parsed.RequestId, parsed.FlightNumber, parsed.ScheduledOutUtc);
        if (SimbriefRandomizationPolicy.ShouldReuse(_randomizationLatch, ofpKey, force))
        {
            // Idempotent re-import of the SAME OFP (issue #64): reuse the figures latched by
            // the first import — 2026-08-15 flight evidence: six imports during one boarding
            // re-rolled pax 89→91→88→85→91→89 while the prelim loadsheet had cut at 89 and
            // GSX armed a different target each time. A new OFP (different request id) or a
            // forced user re-import re-randomizes as before.
            var latch = _randomizationLatch!;
            bookedMap = [.. latch.BookedMap];
            paxCount = latch.PaxCount;
            cargo = latch.CargoKg;
            RecordDecision(
                "simbrief import",
                $"re-import of the same OFP ({ofpKey}) — reusing latched randomization ({paxCount} pax, cargo {cargo:F0} kg)");
        }
        else
        {
            var totalCapacity = capacities.Sum();

            if (paxCount > totalCapacity)
            {
                // Predecessor rule: substitute a plausible load factor rather than refusing.
                var adjusted = (int)(totalCapacity * (0.75 + (Random.Shared.NextDouble() * 0.20)));
                RecordDecision("simbrief import", $"OFP pax {paxCount} exceeds capacity {totalCapacity} — using plausible load {adjusted}");
                paxCount = adjusted;
            }

            // Booked seat map: capacity-proportional (equal load factor per zone — CG-realistic),
            // randomized within each zone so empty seats scatter naturally.
            bookedMap = SeatMap.SynthesizeBooked(paxCount, capacities);

            // Optional no-show/extra randomization (predecessor feature): seats flip with the
            // configured chance, cargo tracks the pax delta by the checked-bag weight.
            var gsxOptions = _options.CurrentValue;
            if (gsxOptions.RandomizePaxNoShows)
            {
                var delta = SeatMap.ApplyNoShowRandomization(bookedMap, gsxOptions.NoShowChancePerSeat);
                if (delta != 0)
                {
                    paxCount += delta;
                    cargo = Math.Max(0, cargo + (delta * gsxOptions.WeightPerBagKg));
                    RecordDecision(
                        "simbrief import",
                        $"pax randomization: {(delta > 0 ? "+" : "")}{delta} vs OFP ({paxCount} boarding); cargo adjusted by {delta * gsxOptions.WeightPerBagKg:F0} kg");
                }
            }

            // Latch even without no-show randomization: the seat map itself is randomized
            // within zones and must also stay stable across re-imports of the same OFP.
            _randomizationLatch = ofpKey.Length > 0
                ? new SimbriefRandomizationLatch(ofpKey, [.. bookedMap], paxCount, cargo)
                : null;
        }

        // Statistics always derive from the actual map so ProSim's manifest agrees seat-for-seat.
        var perZone = SeatMap.CountPerZone(bookedMap, capacities);
        var statistics = JsonSerializer.Serialize(new
        {
            NumOfPaxInBusiness = perZone[0],
            NumOfPaxInEconomy = perZone[1] + perZone[2] + perZone[3],
            NumOfPaxInSection1 = perZone[0],
            NumOfPaxInSection2 = perZone[1],
            NumOfPaxInSection3 = perZone[2] + perZone[3],
            Total = paxCount,
        });

        var ok = await _gateway.WriteDataRefAsync(ProsimDataRefNames.PaxBookedString.Name, SeatMap.Build(bookedMap), cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPassengerStatistics, statistics, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.RefuelFuelTarget.Name, fuelRamp, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPlannedFuel.Name, fuelRamp, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPlannedCargoKg.Name, cargo, cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbSimbriefPlanImported.Name, true, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            RecordDecision("simbrief import", "one or more EFB writes failed — see log; will retry");
            return false;
        }

        // Publish the typed OFP (post-randomization pax/cargo, so downstream consumers agree
        // with what was actually written to ProSim).
        _ofpStore.Set(parsed with { PaxCount = paxCount, CargoKg = cargo });

        RecordDecision(
            "simbrief import",
            $"imported {parsed.OriginIcao}->{parsed.DestinationIcao}: {paxCount} pax (zones {string.Join("/", perZone)}), fuel {fuelRamp:F0} kg, cargo {cargo:F0} kg");
        return true;
    }

    /// <summary>Builds the typed OFP snapshot. Weights convert lbs→kg per params.units; block
    /// fuel is rounded up to the next 100 kg (fuel-order increments, owner requirement).</summary>
    private static OfpData ParseOfp(JsonObject ofp)
    {
        var units = ReadString(ofp["params"]?["units"]) ?? "kgs";
        var isLbs = string.Equals(units, "lbs", StringComparison.OrdinalIgnoreCase);
        double Kg(JsonNode? node) => isLbs ? ReadDouble(node) / LbsPerKg : ReadDouble(node);

        var requestId = ReadString(ofp["params"]?["request_id"]) ?? "";
        var schedOut = ReadDouble(ofp["times"]?["sched_out"]);
        var enrouteSeconds = ReadDouble(ofp["times"]?["est_time_enroute"]);

        // Predecessor format: ICAO airline prefix + number ("BAW123"); empty without a number.
        var flightNumber = ReadString(ofp["general"]?["flight_number"]);
        var fltNbr = string.IsNullOrWhiteSpace(flightNumber)
            ? ""
            : $"{ReadString(ofp["general"]?["icao_airline"])}{flightNumber}";

        // initial_altitude is feet ("37000") — stored as a flight level (predecessor rule).
        var initialAltitudeFt = ReadDouble(ofp["general"]?["initial_altitude"]);

        return new OfpData
        {
            RequestId = requestId,
            Ident = requestId.Length >= 4 ? requestId[..4] : requestId,
            Callsign = ReadString(ofp["atc"]?["callsign"]) ?? "",
            FlightNumber = fltNbr,
            AirlineIcao = (ReadString(ofp["general"]?["icao_airline"]) ?? "").Trim().ToUpperInvariant(),
            PlannedRunwayOut = ReadString(ofp["origin"]?["plan_rwy"]) ?? "",
            PlannedRunwayIn = ReadString(ofp["destination"]?["plan_rwy"]) ?? "",
            CruiseFlightLevel = initialAltitudeFt > 0 ? (int)Math.Round(initialAltitudeFt / 100.0) : 0,
            CostIndex = ReadString(ofp["general"]?["costindex"]) ?? "",
            Route = ReadString(ofp["general"]?["route"]) ?? "",
            OriginIcao = ReadString(ofp["origin"]?["icao_code"]) ?? "",
            OriginIata = ReadString(ofp["origin"]?["iata_code"]) ?? "",
            OriginName = ReadString(ofp["origin"]?["name"]) ?? "",
            DestinationIcao = ReadString(ofp["destination"]?["icao_code"]) ?? "",
            DestinationIata = ReadString(ofp["destination"]?["iata_code"]) ?? "",
            DestinationName = ReadString(ofp["destination"]?["name"]) ?? "",
            AlternateIcao = ReadAlternateIcao(ofp["alternate"]),
            AlternateName = ReadAlternateField(ofp["alternate"], "name"),
            AircraftReg = ReadString(ofp["aircraft"]?["reg"]) ?? "",
            AircraftIcaoType = ReadString(ofp["aircraft"]?["icaocode"]) ?? "",
            PaxCount = (int)ReadDouble(ofp["weights"]?["pax_count"]),
            CargoKg = Kg(ofp["weights"]?["cargo"]),
            FuelPlanRampKg = LoadMath.RoundFuelUpToHundredKg(Kg(ofp["fuel"]?["plan_ramp"])),
            FuelPlanLandingKg = Kg(ofp["fuel"]?["plan_landing"]),
            FuelTaxiKg = Kg(ofp["fuel"]?["taxi"]),
            FuelMinTakeoffKg = Kg(ofp["fuel"]?["min_takeoff"]),
            FuelExtraKg = Kg(ofp["fuel"]?["extra"]),
            OewKg = Kg(ofp["weights"]?["oew"]),
            EstZfwKg = Kg(ofp["weights"]?["est_zfw"]),
            EstTowKg = Kg(ofp["weights"]?["est_tow"]),
            EstLdwKg = Kg(ofp["weights"]?["est_ldw"]),
            MaxZfwKg = Kg(ofp["weights"]?["max_zfw"]),
            MaxTowKg = Kg(ofp["weights"]?["max_tow"]),
            MaxLdwKg = Kg(ofp["weights"]?["max_ldw"]),
            ScheduledOutUtc = schedOut > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)schedOut)
                : null,
            EstimatedEnroute = enrouteSeconds > 0 ? TimeSpan.FromSeconds(enrouteSeconds) : null,
            FetchedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>The SimBrief alternate node is polymorphic: object, array of objects, or empty
    /// string. First alternate wins; anything unparseable is "no alternate".</summary>
    private static string ReadAlternateIcao(JsonNode? alternate) => ReadAlternateField(alternate, "icao_code");

    /// <summary>One string field of the first alternate (same polymorphic node rules).</summary>
    private static string ReadAlternateField(JsonNode? alternate, string field) => alternate switch
    {
        JsonObject obj => ReadString(obj[field]) ?? "",
        JsonArray { Count: > 0 } arr when arr[0] is JsonObject first => ReadString(first[field]) ?? "",
        _ => "",
    };

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
