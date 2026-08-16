using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Gsx;

/// <summary>
/// GSX LVAR catalog used alongside the Remote API (docs/integrations/gsx-remote-api.md §8 —
/// "the API drives the answer; LVARs drive the timing"). Every LVAR the app subscribes is a
/// typed <see cref="SimVarRef{T}"/> (#83); names only ever written stay string constants.
/// The sim auto-creates unknown LVARs as 0, so fallbacks are the only "GSX absent" defence —
/// non-zero ones below are deliberate.
/// </summary>
public static class GsxLvarNames
{
    // Gate-assignment readback (confirmation of gate.select; letter map in GsxGateResolver).
    // Name/Number fall back to 0 = "no readback yet"; Suffix's −1 is the asymmetric
    // no-readback sentinel (0 is a valid suffix) — deliberate, keep the asymmetry.
    public static readonly SimVarRef<int> SetGateName = new("L:FSDT_GSX_SetGate_Name", "number", DataRefTier.Normal, 0);
    public static readonly SimVarRef<int> SetGateNumber = new("L:FSDT_GSX_SetGate_Number", "number", DataRefTier.Normal, 0);
    public static readonly SimVarRef<int> SetGateSuffix = new("L:FSDT_GSX_SetGate_Suffix", "number", DataRefTier.Normal, -1);

    // Jetway/stairs raw state (semantics under live observation; the OPERATE* pair is the
    // known-unreliable one with the 30 s grace rule).
    /// <summary>Fallback 2 = "no jetway" (fail-closed, #83): with GSX or SimConnect absent
    /// the equipment logic must behave as if no jetway exists, never wait on one.</summary>
    public static readonly SimVarRef<double> Jetway = new("L:FSDT_GSX_JETWAY", "number", DataRefTier.Normal, 2.0);
    public static readonly SimVarRef<double> Stairs = new("L:FSDT_GSX_STAIRS", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> OperateJetwaysState = new("L:FSDT_GSX_OPERATEJETWAYS_STATE", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> OperateStairsState = new("L:FSDT_GSX_OPERATESTAIRS_STATE", "number", DataRefTier.Normal, 0.0);

    // Refuel: physical hose state drives the fuel transfer start/stop.
    public static readonly SimVarRef<double> FuelHoseConnected = new("L:FSDT_GSX_FUELHOSE_CONNECTED", "number", DataRefTier.Normal, 0.0);

    // Boarding/deboarding live counters (the API progress rollup is coarser than these).
    public static readonly SimVarRef<double> NumPassengers = new("L:FSDT_GSX_NUMPASSENGERS", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> NumPassengersBoardingTotal = new("L:FSDT_GSX_NUMPASSENGERS_BOARDING_TOTAL", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> NumPassengersDeboardingTotal = new("L:FSDT_GSX_NUMPASSENGERS_DEBOARDING_TOTAL", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> BoardingCargoPercent = new("L:FSDT_GSX_BOARDING_CARGO_PERCENT", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> DeboardingCargoPercent = new("L:FSDT_GSX_DEBOARDING_CARGO_PERCENT", "number", DataRefTier.Normal, 0.0);

    // Crew/pilot boarding-question suppression (write 1 = "not boarding" — GSX then never
    // asks). Write-only: no descriptors.
    public const string CrewNotBoarding = "L:FSDT_GSX_CREW_NOT_BOARDING";
    public const string PilotsNotBoarding = "L:FSDT_GSX_PILOTS_NOT_BOARDING";
    public const string CrewNotDeboarding = "L:FSDT_GSX_CREW_NOT_DEBOARDING";
    public const string PilotsNotDeboarding = "L:FSDT_GSX_PILOTS_NOT_DEBOARDING";

    // Doors: GSX requests aircraft doors through these toggles (rising to nonzero = "operate
    // this door now"); DISABLE_DOORS_MSG=1 silences GSX's "waiting for your action" prompts
    // while we drive the doors. Both reset to 0 on a Couatl restart — re-assert.
    // DisableDoorsMsg reads use GetValueOr(desired) — the re-assert loop compares against the
    // value it intends, so no static fallback is meaningful; 0 = "not asserted".
    public static readonly SimVarRef<double> DisableDoorsMsg = new("L:FSDT_GSX_DISABLE_DOORS_MSG", "number", DataRefTier.Infrequent, 0.0);
    public static readonly SimVarRef<double> DoorToggleCargo1 = new("L:FSDT_GSX_AIRCRAFT_CARGO_1_TOGGLE", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> DoorToggleCargo2 = new("L:FSDT_GSX_AIRCRAFT_CARGO_2_TOGGLE", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> DoorToggleService1 = new("L:FSDT_GSX_AIRCRAFT_SERVICE_1_TOGGLE", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> DoorToggleService2 = new("L:FSDT_GSX_AIRCRAFT_SERVICE_2_TOGGLE", "number", DataRefTier.Normal, 0.0);

    // Cargo loader progress per exit (nonzero while that loader works; drops to 0 when done —
    // triggers the delayed cargo-door close, predecessor OnCargoLoadingChange).
    public static readonly SimVarRef<double> BoardingCargoExit0 = new("L:FSDT_GSX_BOARDING_CARGO_EXIT_0", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> BoardingCargoExit1 = new("L:FSDT_GSX_BOARDING_CARGO_EXIT_1", "number", DataRefTier.Normal, 0.0);

    // Pushback: coarse status (3/4 = tug connected) + per-vehicle state (8 pushing,
    // 11 wait-engine-shutdown, 12 awaiting good-engine-start confirmation, 13 disconnecting,
    // 14 clear to start) + the nosewheel bypass pin.
    public static readonly SimVarRef<double> PushbackStatus = new("L:FSDT_GSX_PUSHBACK_STATUS", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> VehiclePushbackState = new("L:FSDT_GSX_VEHICLE_PUSHBACK_STATE", "number", DataRefTier.Normal, 0.0);
    public static readonly SimVarRef<double> BypassPin = new("L:FSDT_GSX_BYPASS_PIN", "number", DataRefTier.Normal, 0.0);

    // Deicing fluid type readout for the ground-ops signal relay.
    public static readonly SimVarRef<double> DeicingType = new("L:FSDT_GSX_DEICING_TYPE", "number", DataRefTier.Infrequent, 0.0);
}
