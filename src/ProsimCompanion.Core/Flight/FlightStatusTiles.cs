namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The movable tiles of the Flight Status page and the rules for their saved order (issue
/// #160). The page renders the tiles in the order <see cref="Normalize"/> returns; the saved
/// list (<c>flightStatus.tileOrder</c>) is only a hint that may be partial, stale or hand
/// edited, so a tile that is not named still shows — nothing on the dashboard can be lost by
/// editing the config file, and a tile added in a later version appears at the end.
/// </summary>
public static class FlightStatusTiles
{
    public const string Hero = "hero";
    public const string Sequence = "sequence";
    public const string Sim = "sim";
    public const string App = "app";
    public const string Gsx = "gsx";
    public const string Services = "services";

    /// <summary>The tiles in their default order — the layout the page had before it became
    /// movable, so an empty setting changes nothing.</summary>
    public static IReadOnlyList<string> DefaultOrder { get; } = [Hero, Sequence, Sim, App, Gsx, Services];

    /// <summary>The saved order folded into a complete one: known ids in their saved order
    /// (first occurrence wins, case-insensitive), then every tile not named, in default
    /// order. Null or empty gives the default.</summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? saved)
    {
        var result = new List<string>(DefaultOrder.Count);
        foreach (var id in saved ?? [])
        {
            var known = DefaultOrder.FirstOrDefault(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
            if (known is not null && !result.Contains(known))
            {
                result.Add(known);
            }
        }

        foreach (var id in DefaultOrder)
        {
            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    /// <summary>True when the order is the default one — the page then saves an empty list,
    /// so a config that was never customised stays clean.</summary>
    public static bool IsDefault(IReadOnlyList<string> order) =>
        Normalize(order).SequenceEqual(DefaultOrder);

    /// <summary>The order after dropping <paramref name="id"/> onto <paramref name="targetId"/>:
    /// the two tiles trade places and nothing else moves (owner pick 2026-10-07 — one
    /// highlighted target, no before/after edge to judge, so a drop can never land one place
    /// off). Unknown ids and a drop on itself change nothing.</summary>
    public static IReadOnlyList<string> Swap(IReadOnlyList<string> order, string id, string targetId)
    {
        var list = Normalize(order).ToList();
        var from = list.FindIndex(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase));
        var to = list.FindIndex(t => string.Equals(t, targetId, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0 || from == to)
        {
            return list;
        }

        (list[from], list[to]) = (list[to], list[from]);
        return list;
    }

    /// <summary>The order after moving <paramref name="id"/> one place up (-1) or down (+1)
    /// — the edit-mode buttons for a tablet with no mouse and for keyboard users. At either
    /// end nothing changes.</summary>
    public static IReadOnlyList<string> Shift(IReadOnlyList<string> order, string id, int delta)
    {
        var list = Normalize(order).ToList();
        var from = list.FindIndex(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase));
        var to = from + delta;
        if (from < 0 || to < 0 || to >= list.Count)
        {
            return list;
        }

        (list[from], list[to]) = (list[to], list[from]);
        return list;
    }
}
