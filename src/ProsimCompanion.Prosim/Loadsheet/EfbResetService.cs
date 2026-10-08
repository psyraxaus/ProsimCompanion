using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// The INIT page's two reset flows (ProsimInterface parity, 2026-10-09).
/// <para>
/// <b>Soft</b> (<see cref="ResetFlightAsync"/>, the predecessor's <c>ResetFlight()</c>): the
/// leg restarts on the same OFP — INIT overrides cleared (their OFP values restored to the
/// EFB) and the loadsheet cycle back to edition 1. Nothing else is written.
/// </para>
/// <para>
/// <b>Full</b> (<see cref="UnloadOfpAsync"/>, the predecessor's <c>UnloadOfp()</c>): the
/// OFP is unloaded from the ProSim EFB by clearing EXACTLY the gateway datarefs the SimBrief
/// import writes (<see cref="Simbrief.SimbriefImportService"/>: booked seat map, passenger
/// statistics, refuel target, planned fuel, planned cargo, the imported flag) plus the two
/// loadsheet slots the loadsheet pipeline writes — all already on the write allow-list, no
/// new name. The predecessor's wider sweep (cancel boarding, zero the fuel on board and the
/// cargo holds, rewrite the OOOI timestamp JSON, empty the CURRENT seat occupation) touches
/// aircraft state, not the plan, and is deliberately not reproduced: unloading the plan must
/// not move fuel or passengers that are physically aboard. App-side, the OFP store is
/// cleared FIRST (so the overrides service forgets its overrides without writing OFP values
/// back) and then the turnaround path (<see cref="GroundOpsSignals.FlightCycleReset"/>) is
/// raised — the same signal the GSX arrival raises, so every flight-cycle store (loadsheet,
/// overrides, pushback choice, gate monitor, flight times, fuel confirmation, notifications,
/// upcalls …) resets through its existing handler, never an enumerated list here. With the
/// store empty and <c>efb.simbriefPlanImported</c> false the GSX flight-plan gate
/// (<c>GsxFlightPlanMonitor</c>) closes until a new OFP arrives — unless the pilot's MCDU
/// plan is still loaded, which the gate accepts on its own (ADR-0006 rule, unchanged).
/// </para>
/// </summary>
public sealed class EfbResetService : IEfbResetControl, IDisposable
{
    private static readonly int[] FallbackZoneCapacities = [24, 30, 36, 42];

    private readonly IProsimGateway _gateway;
    private readonly OfpStore _ofpStore;
    private readonly GroundOpsSignals _signals;
    private readonly IEfbInitOverrides _overrides;
    private readonly ILoadsheetControl _loadsheets;
    private readonly JsonlEventLog _eventLog;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<EfbResetService> _logger;
    private readonly IDataRefSubscription<int>[] _zoneCapacities;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EfbResetService(
        IProsimDataRefs prosim,
        IProsimGateway gateway,
        OfpStore ofpStore,
        GroundOpsSignals signals,
        IEfbInitOverrides overrides,
        ILoadsheetControl loadsheets,
        JsonlEventLog eventLog,
        GsxDiagnosticsStore diagnostics,
        ILogger<EfbResetService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(loadsheets);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _ofpStore = ofpStore;
        _signals = signals;
        _overrides = overrides;
        _loadsheets = loadsheets;
        _eventLog = eventLog;
        _diagnostics = diagnostics;
        _logger = logger;
        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity),
        ];
    }

    public void Dispose()
    {
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }

        _lock.Dispose();
    }

    public async Task<EfbResetResult> ResetFlightAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cleared = new List<string>();
            var failed = new List<string>();
            (await _overrides.ClearAllAsync(cancellationToken).ConfigureAwait(false) ? cleared : failed).Add("init-overrides");
            _loadsheets.ResetCycle();
            cleared.Add("loadsheet-cycle");
            return Finish(EfbResetKind.Soft, cleared, failed);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<EfbResetResult> UnloadOfpAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cleared = new List<string>();
            var failed = new List<string>();

            // App side first: with no OFP the overrides service drops its overrides without
            // restoring OFP values into the datarefs we are about to blank.
            _ofpStore.Clear();
            cleared.Add("ofp-store");

            foreach (var (name, value) in ClearedWrites())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = await _gateway.WriteDataRefAsync(name, value, cancellationToken).ConfigureAwait(false);
                (ok ? cleared : failed).Add(name);
            }

            // The turnaround path: every flight-cycle store resets through its own handler.
            _signals.RaiseFlightCycleReset();
            cleared.Add("flight-cycle");
            return Finish(EfbResetKind.Full, cleared, failed);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The full reset's write set — the SimBrief import's writes with their cleared
    /// values, plus the two loadsheet slots blanked the way the loadsheet pipeline primes
    /// them at connect (an empty string; the datarefs exist only after a first write).</summary>
    internal IEnumerable<(string Name, object Value)> ClearedWrites()
    {
        var capacities = _zoneCapacities.Select(zone => zone.Value).ToArray();
        if (capacities.Sum() <= 0)
        {
            capacities = FallbackZoneCapacities;
        }

        yield return (ProsimDataRefNames.PaxBookedString.Name, SeatMap.Build(new bool[capacities.Sum()]));
        yield return (ProsimDataRefNames.EfbPassengerStatistics, EmptyPassengerStatistics);
        yield return (ProsimDataRefNames.RefuelFuelTarget.Name, 0.0);
        yield return (ProsimDataRefNames.EfbPlannedFuel.Name, 0.0);
        yield return (ProsimDataRefNames.EfbPlannedCargoKg.Name, 0.0);
        yield return (ProsimDataRefNames.EfbSimbriefPlanImported.Name, false);
        yield return (ProsimDataRefNames.EfbPrelimLoadsheet, "");
        yield return (ProsimDataRefNames.EfbFinalLoadsheet.Name, "");
    }

    /// <summary>The importer's statistics envelope with every count at zero — the same keys,
    /// so ProSim's manifest reads "nobody booked" rather than "no statistics".</summary>
    internal static readonly string EmptyPassengerStatistics = JsonSerializer.Serialize(new
    {
        NumOfPaxInBusiness = 0,
        NumOfPaxInEconomy = 0,
        NumOfPaxInSection1 = 0,
        NumOfPaxInSection2 = 0,
        NumOfPaxInSection3 = 0,
        Total = 0,
    });

    private EfbResetResult Finish(EfbResetKind kind, List<string> cleared, List<string> failed)
    {
        var result = new EfbResetResult(kind, cleared, failed);
        var reason = failed.Count == 0
            ? $"{kind.ToString().ToLowerInvariant()} reset: cleared {string.Join(", ", cleared)}"
            : $"{kind.ToString().ToLowerInvariant()} reset: cleared {string.Join(", ", cleared)}; ProSim refused {string.Join(", ", failed)}";
        _logger.LogInformation("EFB reset: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "efb reset", reason));
        _eventLog.Record("efb-reset", new
        {
            kind = kind.ToString().ToLowerInvariant(),
            cleared,
            failed,
        });
        return result;
    }
}
