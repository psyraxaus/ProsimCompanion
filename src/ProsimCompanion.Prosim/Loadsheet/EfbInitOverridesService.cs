using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// EFB INIT per-field overrides, following the predecessor's exact field mapping:
/// zfwKg → <c>aircraft.fms.init.zfw</c> (tonnes), fuelRampKg → <c>aircraft.fms.init.block</c>
/// (rounded up to 100 kg, tonnes), cargoKg → <c>efb.plannedCargoKg</c>, passengerCount → a
/// re-synthesized booked seat map + passenger statistics. Overrides live in memory for the
/// flight and reset on a new OFP (request-id change) or the turnaround cycle reset.
/// </summary>
public sealed class EfbInitOverridesService : IEfbInitOverrides, IDisposable
{
    private readonly IProsimDataRefs _prosim;
    private readonly IProsimGateway _gateway;
    private readonly OfpStore _ofpStore;
    private readonly GroundOpsSignals _signals;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<EfbInitOverridesService> _logger;
    private readonly IDataRefSubscription<int>[] _zoneCapacities;
    private readonly ConcurrentDictionary<string, double> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastOfpRequestId;

    public event EventHandler? Changed;

    public EfbInitOverridesService(
        IProsimDataRefs prosim,
        IProsimGateway gateway,
        OfpStore ofpStore,
        GroundOpsSignals signals,
        GsxDiagnosticsStore diagnostics,
        ILogger<EfbInitOverridesService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _gateway = gateway;
        _ofpStore = ofpStore;
        _signals = signals;
        _diagnostics = diagnostics;
        _logger = logger;

        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity),
        ];

        _ofpStore.Changed += OnOfpChanged;
        _signals.FlightCycleReset += OnCycleReset;
    }

    public void Dispose()
    {
        _ofpStore.Changed -= OnOfpChanged;
        _signals.FlightCycleReset -= OnCycleReset;
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }
    }

    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>(_overrides);

    public async Task<bool> SetAsync(string field, double value, CancellationToken cancellationToken = default)
    {
        if (!IsWritable(field))
        {
            return false;
        }

        var ok = await WriteFieldAsync(field, value, cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            _overrides[field] = value;
            RecordDecision($"override {field} = {value:F0} applied");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    public async Task<bool> ClearAsync(string field, CancellationToken cancellationToken = default)
    {
        if (!_overrides.TryRemove(field, out _))
        {
            return true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        var ofpValue = OfpValue(field);
        if (ofpValue is null)
        {
            RecordDecision($"override {field} cleared (no OFP value to restore)");
            return true;
        }

        var ok = await WriteFieldAsync(field, ofpValue.Value, cancellationToken).ConfigureAwait(false);
        RecordDecision($"override {field} cleared — OFP value {ofpValue.Value:F0} restored (written {ok})");
        return ok;
    }

    public async Task<bool> ClearAllAsync(CancellationToken cancellationToken = default)
    {
        var ok = true;
        foreach (var field in _overrides.Keys.ToArray())
        {
            ok &= await ClearAsync(field, cancellationToken).ConfigureAwait(false);
        }
        return ok;
    }

    private static bool IsWritable(string field) => field is IEfbInitOverrides.ZfwKg
        or IEfbInitOverrides.FuelRampKg
        or IEfbInitOverrides.CargoKg
        or IEfbInitOverrides.PassengerCount;

    private double? OfpValue(string field)
    {
        var ofp = _ofpStore.Current;
        if (ofp is null)
        {
            return null;
        }
        return field switch
        {
            IEfbInitOverrides.ZfwKg => ofp.EstZfwKg > 0 ? ofp.EstZfwKg : null,
            IEfbInitOverrides.FuelRampKg => ofp.FuelPlanRampKg > 0 ? ofp.FuelPlanRampKg : null,
            IEfbInitOverrides.CargoKg => ofp.CargoKg,
            IEfbInitOverrides.PassengerCount => ofp.PaxCount,
            _ => null,
        };
    }

    private async Task<bool> WriteFieldAsync(string field, double value, CancellationToken cancellationToken)
    {
        try
        {
            switch (field)
            {
                case IEfbInitOverrides.ZfwKg:
                    await _prosim.WriteAsync(ProsimDataRefNames.FmsInitZfw, value / 1000.0, cancellationToken).ConfigureAwait(false);
                    return true;

                case IEfbInitOverrides.FuelRampKg:
                    var rounded = LoadMath.RoundFuelUpToHundredKg(value);
                    await _prosim.WriteAsync(ProsimDataRefNames.FmsInitBlock, rounded / 1000.0, cancellationToken).ConfigureAwait(false);
                    return true;

                case IEfbInitOverrides.CargoKg:
                    return await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPlannedCargoKg.Name, value, cancellationToken).ConfigureAwait(false);

                case IEfbInitOverrides.PassengerCount:
                    return await WritePassengerCountAsync((int)value, cancellationToken).ConfigureAwait(false);

                default:
                    return false;
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("INIT override write failed for {Field}: {Message}", field, ex.Message);
            return false;
        }
    }

    /// <summary>A pax override rebuilds the booked seat map (capacity-proportional, randomized
    /// within zones — same recipe as the importer) so boarding and manifests agree with it.</summary>
    private async Task<bool> WritePassengerCountAsync(int paxCount, CancellationToken cancellationToken)
    {
        var capacities = _zoneCapacities.Select(zone => zone.Value).ToArray();
        if (capacities.Sum() <= 0)
        {
            capacities = [24, 30, 36, 42];
        }
        paxCount = Math.Clamp(paxCount, 0, capacities.Sum());

        var bookedMap = SeatMap.SynthesizeBooked(paxCount, capacities);
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

        return await _gateway.WriteDataRefAsync(ProsimDataRefNames.PaxBookedString.Name, SeatMap.Build(bookedMap), cancellationToken).ConfigureAwait(false)
            & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPassengerStatistics, statistics, cancellationToken).ConfigureAwait(false);
    }

    private void OnOfpChanged(object? sender, EventArgs e)
    {
        var requestId = _ofpStore.Current?.RequestId;
        if (requestId == _lastOfpRequestId)
        {
            return;
        }
        _lastOfpRequestId = requestId;
        if (!_overrides.IsEmpty)
        {
            _overrides.Clear();
            RecordDecision("new OFP — INIT overrides cleared");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCycleReset()
    {
        if (!_overrides.IsEmpty)
        {
            _overrides.Clear();
            RecordDecision("flight cycle reset — INIT overrides cleared");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RecordDecision(string reason)
    {
        _logger.LogInformation("EFB INIT: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "efb init", reason));
    }
}
