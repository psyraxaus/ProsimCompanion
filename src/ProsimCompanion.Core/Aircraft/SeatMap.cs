namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Pure seat-map operations for progressive boarding (the predecessors' proven model): the
/// planned map comes from <c>efb.passengers.booked.string</c> ("true,false,…" seat-indexed
/// forward → aft); as GSX's boarded count rises, planned-but-empty seats fill in order; the
/// boarded map is written back to <c>aircraft.passengers.seatOccupation.string</c>, from which
/// ProSim itself derives zone loads and CG.
/// </summary>
public static class SeatMap
{
    /// <summary>Parses a comma-separated "true,false,…" seat string; empty/null → empty map.</summary>
    public static bool[] Parse(string? seatString)
    {
        if (string.IsNullOrWhiteSpace(seatString))
        {
            return [];
        }

        var parts = seatString.Split(',');
        var map = new bool[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            map[i] = parts[i].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return map;
    }

    /// <summary>Builds the comma-separated seat string ProSim expects.</summary>
    public static string Build(IReadOnlyList<bool> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return string.Join(',', map.Select(seat => seat ? "true" : "false"));
    }

    /// <summary>
    /// Synthesizes a booked map when no OFP-derived one exists: passengers spread across the
    /// zones capacity-proportionally (equal load factor front-to-back — the CG-realistic
    /// distribution), occupying random seats within each zone so empty seats scatter naturally
    /// instead of clustering at the back of every section. Seat indices run forward → aft,
    /// sliced by zone capacity. Pass a seeded <paramref name="random"/> for reproducible maps.
    /// </summary>
    public static bool[] SynthesizeBooked(int paxCount, IReadOnlyList<int> zoneCapacities, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(zoneCapacities);
        random ??= Random.Shared;

        var perZone = LoadMath.DistributePax(paxCount, zoneCapacities);
        var map = new bool[zoneCapacities.Sum()];
        var offset = 0;
        for (var zone = 0; zone < zoneCapacities.Count; zone++)
        {
            var capacity = zoneCapacities[zone];
            // Partial Fisher–Yates: the first perZone[zone] entries end up a uniform random
            // subset of the zone's seats.
            var seats = Enumerable.Range(offset, capacity).ToArray();
            for (var i = 0; i < perZone[zone]; i++)
            {
                var pick = random.Next(i, capacity);
                (seats[i], seats[pick]) = (seats[pick], seats[i]);
                map[seats[i]] = true;
            }
            offset += capacity;
        }
        return map;
    }

    /// <summary>
    /// Unseats passengers from the FRONT of the cabin until the boarded count drops to
    /// <paramref name="targetRemainingCount"/> (front rows deboard first — the predecessor's
    /// deboarding model). Never seats anyone. Returns the number unseated.
    /// </summary>
    public static int DrainBoarded(bool[] boarded, int targetRemainingCount)
    {
        ArgumentNullException.ThrowIfNull(boarded);

        var currentlyBoarded = boarded.Count(seat => seat);
        var toUnseat = currentlyBoarded - Math.Max(0, targetRemainingCount);
        var unseated = 0;
        for (var i = 0; i < boarded.Length && unseated < toUnseat; i++)
        {
            if (boarded[i])
            {
                boarded[i] = false;
                unseated++;
            }
        }
        return unseated;
    }

    /// <summary>
    /// Flips each seat's booked state with the given per-seat chance (the predecessor's
    /// no-show/extra randomization): a booked seat flipping off is a no-show, an empty seat
    /// flipping on is a walk-up extra. Mutates the map; returns the net passenger delta
    /// (negative = fewer than planned). Pass a seeded <paramref name="random"/> for
    /// reproducible results.
    /// </summary>
    public static int ApplyNoShowRandomization(bool[] booked, double chancePerSeat, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(booked);
        random ??= Random.Shared;

        if (chancePerSeat <= 0)
        {
            return 0;
        }

        var delta = 0;
        for (var i = 0; i < booked.Length; i++)
        {
            if (random.NextDouble() < chancePerSeat)
            {
                booked[i] = !booked[i];
                delta += booked[i] ? 1 : -1;
            }
        }
        return delta;
    }

    /// <summary>Occupied-seat count per zone, slicing the map by zone capacities in order.</summary>
    public static int[] CountPerZone(IReadOnlyList<bool> map, IReadOnlyList<int> zoneCapacities)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(zoneCapacities);

        var counts = new int[zoneCapacities.Count];
        var offset = 0;
        for (var zone = 0; zone < zoneCapacities.Count; zone++)
        {
            for (var i = 0; i < zoneCapacities[zone] && offset + i < map.Count; i++)
            {
                if (map[offset + i])
                {
                    counts[zone]++;
                }
            }
            offset += zoneCapacities[zone];
        }
        return counts;
    }

    /// <summary>
    /// Seats passengers until the boarded count reaches <paramref name="targetBoardedCount"/>
    /// (clamped to the planned count): planned-but-empty seats fill in seat order. Never
    /// unseats. Returns the number of newly seated passengers.
    /// </summary>
    public static int FillBoarded(IReadOnlyList<bool> planned, bool[] boarded, int targetBoardedCount)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(boarded);

        var currentlyBoarded = boarded.Count(seat => seat);
        var target = Math.Min(targetBoardedCount, planned.Count(seat => seat));
        var toSeat = target - currentlyBoarded;
        if (toSeat <= 0)
        {
            return 0;
        }

        var seated = 0;
        for (var i = 0; i < boarded.Length && i < planned.Count && seated < toSeat; i++)
        {
            if (planned[i] && !boarded[i])
            {
                boarded[i] = true;
                seated++;
            }
        }
        return seated;
    }
}

