namespace ProsimCompanion.Gsx;

/// <summary>
/// GSX LVAR names used alongside the Remote API (docs/integrations/gsx-remote-api.md §8 —
/// "the API drives the answer; LVARs drive the timing"). Names are wire identifiers; extend
/// deliberately as features land. Subscribe with unit "number".
/// </summary>
public static class GsxLvarNames
{
    // Gate-assignment readback (confirmation of gate.select; letter map in GsxGateResolver).
    public const string SetGateName = "L:FSDT_GSX_SetGate_Name";
    public const string SetGateNumber = "L:FSDT_GSX_SetGate_Number";
    public const string SetGateSuffix = "L:FSDT_GSX_SetGate_Suffix";

    // Refuel: physical hose state drives the fuel transfer start/stop.
    public const string FuelHoseConnected = "L:FSDT_GSX_FUELHOSE_CONNECTED";

    // Boarding/deboarding live counters (the API progress rollup is coarser than these).
    public const string NumPassengers = "L:FSDT_GSX_NUMPASSENGERS";
    public const string NumPassengersBoardingTotal = "L:FSDT_GSX_NUMPASSENGERS_BOARDING_TOTAL";
    public const string NumPassengersDeboardingTotal = "L:FSDT_GSX_NUMPASSENGERS_DEBOARDING_TOTAL";
    public const string BoardingCargoPercent = "L:FSDT_GSX_BOARDING_CARGO_PERCENT";
    public const string DeboardingCargoPercent = "L:FSDT_GSX_DEBOARDING_CARGO_PERCENT";
}
