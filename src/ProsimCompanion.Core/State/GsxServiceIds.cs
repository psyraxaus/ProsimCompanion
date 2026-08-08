namespace ProsimCompanion.Core.State;

/// <summary>
/// Canonical GSX Remote API service ids (docs/integrations/gsx-remote-api.md §3). Every surface
/// that keys on a service — mirror lookups, the departure board, the web status rows, the
/// /api/status payload the Stream Deck plugin matches against — must use these exact strings.
/// Three independently invented vocabularies ("refuel", "Jetway", "Pushback") once left the
/// Stream Deck refuel key and the Flight Status jetway/stairs rows matching nothing (issue #31).
/// </summary>
public static class GsxServiceIds
{
    public const string Refueling = "Refueling";
    public const string Catering = "Catering";
    public const string Boarding = "Boarding";
    public const string Deboarding = "Deboarding";

    /// <summary>The pushback request — GSX names this service "Departure", not "Pushback".</summary>
    public const string Departure = "Departure";

    public const string Gpu = "GPU";
    public const string DeIce = "DeIce";
    public const string Water = "Water";
    public const string Lavatory = "Lavatory";
    public const string Cleaning = "Cleaning";
    public const string OperateJetways = "OperateJetways";
    public const string OperateStairs = "OperateStairs";

    /// <summary>True for the jetway/stairs connect-retract toggles. Their mirror state is
    /// positional (docked vs retracted), so completion latching must never apply to them —
    /// a latched "completed" would keep reading connected after a retraction.</summary>
    public static bool IsToggle(string serviceId)
        => string.Equals(serviceId, OperateJetways, StringComparison.OrdinalIgnoreCase)
            || string.Equals(serviceId, OperateStairs, StringComparison.OrdinalIgnoreCase);
}
