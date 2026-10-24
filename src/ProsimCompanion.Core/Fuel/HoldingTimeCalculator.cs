using System.Globalization;

namespace ProsimCompanion.Core.Fuel;

/// <summary>"How long can we hold?" (issue #183): the fuel left over the destination after the
/// alternate and the final reserve, in minutes at the holding burn.</summary>
/// <param name="AvailableMinutes">Minutes of holding; negative = already into the reserves.</param>
/// <param name="HoldingFuelKg">The kilos available for holding (may be negative).</param>
/// <param name="EstimatedOverDestinationKg">Fuel expected over the destination at the current burn.</param>
/// <param name="AlternateKg">The alternate fuel kept.</param>
/// <param name="FinalReserveKg">The final reserve kept.</param>
/// <param name="HoldingBurnKgPerHour">The burn used.</param>
/// <param name="AtUtc">When computed.</param>
public sealed record HoldingEstimate(
    int AvailableMinutes,
    double HoldingFuelKg,
    double EstimatedOverDestinationKg,
    double AlternateKg,
    double FinalReserveKg,
    int HoldingBurnKgPerHour,
    DateTimeOffset AtUtc)
{
    /// <summary>True when there is no holding fuel: a diversion decision is now.</summary>
    public bool DivertNow => AvailableMinutes <= 0;

    /// <summary>"We can hold for 25 minutes before we have to divert." / "No holding fuel —
    /// over the destination we would be into the alternate fuel. Divert now."</summary>
    public string Spoken => DivertNow
        ? "No holding fuel — over the destination we would be into the alternate fuel. Divert now."
        : $"We can hold for {AvailableMinutes} minutes before we have to divert.";
}

/// <summary>The pure holding-time rule (issue #183).</summary>
public static class HoldingTimeCalculator
{
    /// <summary>Holding time from the fuel expected over the destination (fuel on board minus
    /// the burn to get there at the current flow), minus the alternate fuel and the final
    /// reserve, at the holding burn. Null when the burn is unknown.</summary>
    public static HoldingEstimate? Compute(
        double fuelOnBoardKg,
        double fuelToDestinationKg,
        double alternateKg,
        double finalReserveKg,
        int holdingBurnKgPerHour,
        DateTimeOffset nowUtc)
    {
        if (holdingBurnKgPerHour <= 0 || fuelOnBoardKg <= 0)
        {
            return null;
        }

        var overDestination = fuelOnBoardKg - Math.Max(0, fuelToDestinationKg);
        var holding = overDestination - Math.Max(0, alternateKg) - Math.Max(0, finalReserveKg);
        var minutes = (int)Math.Floor(holding / holdingBurnKgPerHour * 60.0);
        return new HoldingEstimate(minutes, holding, overDestination, alternateKg, finalReserveKg, holdingBurnKgPerHour, nowUtc);
    }

    /// <summary>The page figure: "25 min" / "divert".</summary>
    public static string Figure(HoldingEstimate? estimate)
        => estimate is null ? "—" : estimate.DivertNow ? "DIVERT" : estimate.AvailableMinutes.ToString(CultureInfo.InvariantCulture) + " MIN";
}
