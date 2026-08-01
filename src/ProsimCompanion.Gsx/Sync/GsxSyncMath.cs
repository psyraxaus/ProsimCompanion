namespace ProsimCompanion.Gsx.Sync;

/// <summary>Pure arithmetic for the ProSim sync modules — fully unit-tested.</summary>
public static class GsxSyncMath
{
    /// <summary>
    /// Distributes boarded passengers across zones proportionally to zone capacity using the
    /// largest-remainder method, so the zone sum always equals <paramref name="totalPax"/> and
    /// no zone exceeds its capacity.
    /// </summary>
    public static int[] DistributePax(int totalPax, IReadOnlyList<int> zoneCapacities)
    {
        ArgumentNullException.ThrowIfNull(zoneCapacities);

        var zones = zoneCapacities.Count;
        var result = new int[zones];
        var totalCapacity = zoneCapacities.Sum();
        if (totalPax <= 0 || totalCapacity <= 0)
        {
            return result;
        }

        totalPax = Math.Min(totalPax, totalCapacity);

        var remainders = new (int Zone, double Remainder)[zones];
        var assigned = 0;
        for (var i = 0; i < zones; i++)
        {
            var exact = (double)totalPax * zoneCapacities[i] / totalCapacity;
            result[i] = (int)exact;
            assigned += result[i];
            remainders[i] = (i, exact - result[i]);
        }

        foreach (var (zone, _) in remainders.OrderByDescending(r => r.Remainder))
        {
            if (assigned >= totalPax)
            {
                break;
            }

            if (result[zone] < zoneCapacities[zone])
            {
                result[zone]++;
                assigned++;
            }
        }

        return result;
    }

    /// <summary>Splits a cargo weight across forward/aft holds proportional to capacity.
    /// Bulk is not settable in ProSim, so its share folds into aft (predecessor rule).</summary>
    public static (double Forward, double Aft) SplitCargo(double totalKg, double forwardCapacity, double aftCapacity)
    {
        if (totalKg <= 0)
        {
            return (0, 0);
        }

        var totalCapacity = forwardCapacity + aftCapacity;
        if (totalCapacity <= 0)
        {
            // No capacity data yet — put everything aft rather than losing it.
            return (0, totalKg);
        }

        var forward = totalKg * forwardCapacity / totalCapacity;
        return (forward, totalKg - forward);
    }

    /// <summary>Next fuel quantity after one transfer tick, clamped so the target is reached
    /// exactly and never overshot (works in both directions — GSX can also defuel).</summary>
    public static double NextFuelStep(double currentKg, double targetKg, double stepKg)
    {
        if (stepKg <= 0)
        {
            return currentKg;
        }

        var remaining = targetKg - currentKg;
        if (Math.Abs(remaining) <= stepKg)
        {
            return targetKg;
        }

        return currentKg + Math.Sign(remaining) * stepKg;
    }
}
