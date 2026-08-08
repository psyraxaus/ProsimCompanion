using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;

namespace ProsimCompanion.Prosim.Passengers;

/// <summary>
/// Passenger SIMULATE tool (ported from Prosim2GSX's PassengerSimulationService): populates or
/// empties the cabin directly, for headless setups where the user wants a loaded aircraft
/// without GSX driving a boarding run.
///
/// Write path is the gateway — the same transport the boarding sync and EFB INIT overrides use
/// (several EFB-domain datarefs reject SDK writes), gated by ProsimWriteGate inside the client.
/// Generation follows the EFB pax-override recipe exactly (capacity-proportional
/// <see cref="SeatMap.SynthesizeBooked"/> map + booked string + passenger statistics, so
/// <c>PassengerManifestService</c> deals names for the simulated pax), plus the seat-occupation
/// string so the aircraft loads immediately — ProSim derives zone loads and CG from it.
/// </summary>
public sealed class PassengerSimulationService : IPassengerSimulation, IDisposable
{
    private static readonly int[] FallbackZoneCapacities = [24, 30, 36, 42];

    private readonly IProsimGateway _gateway;
    private readonly ILogger<PassengerSimulationService> _logger;
    private readonly IDataRefSubscription[] _zoneCapacities;

    public PassengerSimulationService(
        IProsimDataRefs prosim,
        IProsimGateway gateway,
        ILogger<PassengerSimulationService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _logger = logger;
        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity, DataRefTier.Infrequent),
        ];
    }

    public void Dispose()
    {
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<bool> GenerateAsync(int count, CancellationToken cancellationToken = default)
    {
        try
        {
            var capacities = ResolveCapacities();
            count = Math.Clamp(count, 0, capacities.Sum());

            var map = SeatMap.SynthesizeBooked(count, capacities);
            var seatString = SeatMap.Build(map);
            var statistics = BuildStatisticsJson(SeatMap.CountPerZone(map, capacities), count);

            // Booked + statistics first (the plan the manifest reads), then the occupation
            // string — the actual cabin load. Same statistics shape as EfbInitOverridesService.
            var ok = await _gateway.WriteDataRefAsync(ProsimDataRefNames.PaxBookedString, seatString, cancellationToken).ConfigureAwait(false)
                & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPassengerStatistics, statistics, cancellationToken).ConfigureAwait(false)
                & await _gateway.WriteDataRefAsync(ProsimDataRefNames.PaxSeatOccupationString, seatString, cancellationToken).ConfigureAwait(false);

            if (ok)
            {
                _logger.LogInformation("Passenger simulation: generated {Count} pax and loaded the cabin", count);
            }
            else
            {
                _logger.LogWarning("Passenger simulation: generate for {Count} pax failed (gateway rejected/unreachable)", count);
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passenger simulation: generate failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var emptyCabin = SeatMap.Build(new bool[ResolveCapacities().Sum()]);
            var ok = await _gateway.WriteDataRefAsync(ProsimDataRefNames.PaxSeatOccupationString, emptyCabin, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                _logger.LogInformation("Passenger simulation: cabin cleared");
            }
            else
            {
                _logger.LogWarning("Passenger simulation: clear failed (gateway rejected/unreachable)");
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passenger simulation: clear failed");
            return false;
        }
    }

    /// <summary>Live zone capacities, or the A320 standard 24/30/36/42 before they populate.</summary>
    private int[] ResolveCapacities()
    {
        var capacities = _zoneCapacities.Select(zone => zone.GetValue(0)).ToArray();
        return capacities.Sum() > 0 ? capacities : FallbackZoneCapacities;
    }

    /// <summary>The EFB statistics payload — field names and zone grouping must match the
    /// pax-override writer (EfbInitOverridesService) so ProSim's EFB reads either producer.</summary>
    private static string BuildStatisticsJson(int[] perZone, int total) => JsonSerializer.Serialize(new
    {
        NumOfPaxInBusiness = perZone[0],
        NumOfPaxInEconomy = perZone[1] + perZone[2] + perZone[3],
        NumOfPaxInSection1 = perZone[0],
        NumOfPaxInSection2 = perZone[1],
        NumOfPaxInSection3 = perZone[2] + perZone[3],
        Total = total,
    });
}
