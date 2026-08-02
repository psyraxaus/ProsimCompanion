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

    // Jetway/stairs raw state (semantics under live observation; the OPERATE* pair is the
    // known-unreliable one with the 30 s grace rule).
    public const string Jetway = "L:FSDT_GSX_JETWAY";
    public const string Stairs = "L:FSDT_GSX_STAIRS";
    public const string OperateJetwaysState = "L:FSDT_GSX_OPERATEJETWAYS_STATE";
    public const string OperateStairsState = "L:FSDT_GSX_OPERATESTAIRS_STATE";

    // Refuel: physical hose state drives the fuel transfer start/stop.
    public const string FuelHoseConnected = "L:FSDT_GSX_FUELHOSE_CONNECTED";

    // Boarding/deboarding live counters (the API progress rollup is coarser than these).
    public const string NumPassengers = "L:FSDT_GSX_NUMPASSENGERS";
    public const string NumPassengersBoardingTotal = "L:FSDT_GSX_NUMPASSENGERS_BOARDING_TOTAL";
    public const string NumPassengersDeboardingTotal = "L:FSDT_GSX_NUMPASSENGERS_DEBOARDING_TOTAL";
    public const string BoardingCargoPercent = "L:FSDT_GSX_BOARDING_CARGO_PERCENT";
    public const string DeboardingCargoPercent = "L:FSDT_GSX_DEBOARDING_CARGO_PERCENT";

    // Crew/pilot boarding-question suppression (write 1 = "not boarding" — GSX then never asks).
    public const string CrewNotBoarding = "L:FSDT_GSX_CREW_NOT_BOARDING";
    public const string PilotsNotBoarding = "L:FSDT_GSX_PILOTS_NOT_BOARDING";
    public const string CrewNotDeboarding = "L:FSDT_GSX_CREW_NOT_DEBOARDING";
    public const string PilotsNotDeboarding = "L:FSDT_GSX_PILOTS_NOT_DEBOARDING";

    // Doors: GSX requests aircraft doors through these toggles (rising to nonzero = "operate
    // this door now"); DISABLE_DOORS_MSG=1 silences GSX's "waiting for your action" prompts
    // while we drive the doors. Both reset to 0 on a Couatl restart — re-assert.
    public const string DisableDoorsMsg = "L:FSDT_GSX_DISABLE_DOORS_MSG";
    public const string DoorToggleCargo1 = "L:FSDT_GSX_AIRCRAFT_CARGO_1_TOGGLE";
    public const string DoorToggleCargo2 = "L:FSDT_GSX_AIRCRAFT_CARGO_2_TOGGLE";
    public const string DoorToggleService1 = "L:FSDT_GSX_AIRCRAFT_SERVICE_1_TOGGLE";
    public const string DoorToggleService2 = "L:FSDT_GSX_AIRCRAFT_SERVICE_2_TOGGLE";

    // Cargo loader progress per exit (nonzero while that loader works; drops to 0 when done —
    // triggers the delayed cargo-door close, predecessor OnCargoLoadingChange).
    public const string BoardingCargoExit0 = "L:FSDT_GSX_BOARDING_CARGO_EXIT_0";
    public const string BoardingCargoExit1 = "L:FSDT_GSX_BOARDING_CARGO_EXIT_1";

    // Pushback: coarse status (3/4 = tug connected) + per-vehicle state (8 pushing,
    // 11 wait-engine-shutdown, 12 awaiting good-engine-start confirmation, 13 disconnecting,
    // 14 clear to start) + the nosewheel bypass pin.
    public const string PushbackStatus = "L:FSDT_GSX_PUSHBACK_STATUS";
    public const string VehiclePushbackState = "L:FSDT_GSX_VEHICLE_PUSHBACK_STATE";
    public const string BypassPin = "L:FSDT_GSX_BYPASS_PIN";
}
