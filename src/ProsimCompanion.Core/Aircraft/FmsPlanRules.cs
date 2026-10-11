namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The one reading of the MCDU origin/destination datarefs (<c>aircraft.fms.origin</c> /
/// <c>aircraft.fms.destination</c>). Written for the GSX flight-plan gate and moved to Core
/// for the flight-state snapshot (issue #167), whose startup check and in-flight plan fetch
/// must apply the exact same "is a plan loaded" rule as the ground importer — never a
/// re-derived copy.
/// </summary>
public static class FmsPlanRules
{
    /// <summary>The FMS origin/destination datarefs carry a valid 4-char ICAO once the pilot
    /// loads a plan — but read "----" before that, and have been observed returning the
    /// literal string "Null" (predecessor archaeology, TakeoffPerfService).</summary>
    public static bool IsValidIcao(string? value)
        => value is { Length: 4 }
            && value != "----"
            && !value.Equals("Null", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pilot loaded a plan in the MCDU: both ends are valid ICAO codes.</summary>
    public static bool IsPlanPresent(string? origin, string? destination)
        => IsValidIcao(origin) && IsValidIcao(destination);
}
