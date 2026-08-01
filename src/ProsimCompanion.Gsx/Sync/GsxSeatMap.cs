namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Pure seat-map operations for progressive boarding (the predecessors' proven model): the
/// planned map comes from <c>efb.passengers.booked.string</c> ("true,false,…" seat-indexed
/// forward → aft); as GSX's boarded count rises, planned-but-empty seats fill in order; the
/// boarded map is written back to <c>aircraft.passengers.seatOccupation.string</c>, from which
/// ProSim itself derives zone loads and CG.
/// </summary>
public static class GsxSeatMap
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
