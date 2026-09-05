using System.Collections.Frozen;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Prosim.DataRefs;

/// <summary>
/// Code-level allow-list for aircraft writes — the write-safety rule from
/// docs/ARCHITECTURE.md made executable. Every dataref the application may write is listed here
/// explicitly, so widening the write surface is always a deliberate, reviewable code change.
/// Seeded with the ground-operations surface Phase 2 needs; extend alongside each feature.
/// </summary>
public static class ProsimWriteGate
{
    private static readonly FrozenSet<string> AllowedNames = new[]
    {
        // Refuel
        ProsimDataRefNames.RefuelFuelTarget.Name,
        ProsimDataRefNames.RefuelFuelTargetKg.Name,
        ProsimDataRefNames.RefuelActive,
        ProsimDataRefNames.RefuelPower,
        ProsimDataRefNames.RefuelRate,
        ProsimDataRefNames.FuelTotal.Name,

        // Ground equipment
        ProsimDataRefNames.GroundPower.Name,
        ProsimDataRefNames.GroundPreconditionedAir.Name,
        ProsimDataRefNames.Chocks.Name,
        ProsimDataRefNames.EfbFwdStairs,
        ProsimDataRefNames.EfbAftStairs,

        // ProSim native-integration flags we own while running
        ProsimDataRefNames.EfbAutoJetway,
        ProsimDataRefNames.EfbAutoDoor,

        // Boarding state (EFB UI tracks it; double "efb." is the real path)
        ProsimDataRefNames.EfbBoardingStatus.Name,

        // SimBrief import flag (written by the OFP importer)
        ProsimDataRefNames.EfbSimbriefPlanImported.Name,

        // Ground-crew upcalls (ADR-0006): momentary MECH call press so the ACP lamp flashes
        // when ground initiates an interphone call. Press-only via PressMomentaryAsync.
        ProsimDataRefNames.OhCallsMech,

        // Planning / pax
        ProsimDataRefNames.EfbPlannedFuel.Name,
        ProsimDataRefNames.EfbPlannedCargoKg.Name,
        ProsimDataRefNames.PaxBookedString.Name,
        ProsimDataRefNames.EfbPassengerStatistics,
        ProsimDataRefNames.PaxSeatOccupationString.Name,

        // Loadsheet pipeline (Phase 3): the EFB display slots + the ACARS uplink envelope
        ProsimDataRefNames.EfbPrelimLoadsheet,
        ProsimDataRefNames.EfbFinalLoadsheet.Name,
        ProsimDataRefNames.AocMessageUplink,

        // MCDU INIT B sync (values in TONNES for zfw/block; zfwcg is %MAC)
        ProsimDataRefNames.FmsInitBlock,
        ProsimDataRefNames.FmsInitZfw,
        ProsimDataRefNames.FmsInitZfwcg,

        // FMS PERF TO uplink (Phase 3 performance page)
        ProsimDataRefNames.FmsPerfTakeoffFlaps.Name,
        ProsimDataRefNames.FmsPerfTakeoffFlexTemp.Name,
        ProsimDataRefNames.FmsPerfTakeoffV1.Name,
        ProsimDataRefNames.FmsPerfTakeoffVr.Name,
        ProsimDataRefNames.FmsPerfTakeoffV2.Name,
        ProsimDataRefNames.FmsPerfTakeoffThs,
        ProsimDataRefNames.FmsPerfTakeoffShift,

        // Landing gear lever [0:Down, 1:Up] — the "gear up/down" PM voice command (issue
        // #43). The feature gates on airborne evidence before writing; dataref-first per
        // the write-safety rule (the lever IS the dataref, no cockpit switch actuation).
        ProsimDataRefNames.MipGear,

        // F/O clock chrono — momentary press on the pilot's "takeoff" call and again at
        // touchdown (issue #126). Press-only via PressMomentaryAsync, like the MECH call.
        ProsimDataRefNames.MipChronoFo,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] AllowedPrefixes =
    [
        "aircraft.passengers.zone",   // zone1-4 amounts during boarding sync
        "aircraft.cargo.",            // forward/aft amounts (bulk is not settable in ProSim)
        "doors.",                     // door automation
        "efb.gsx.",                   // disable ProSim's native GSX integration

        // Audio pillar: clear ProSim's native per-window volume bindings while this app
        // drives the Windows sessions (ProsimNativeAudioGuard). PA is deliberately never
        // written — the guard's own exclusion, not the gate's job to enforce.
        "aircraft.communication.windows.",

        // Voice FO flight-control check: the FO-side sidestick/rudder analogs ONLY
        // (0..512..1024). The captain-side A_FC_CAPT_* refs are deliberately NOT listed —
        // the FO must never move the captain's controls.
        "system.analog.A_FC_FO_",

        // File-driven voice commands (commands.json): MCDU key presses. The commands
        // engine's own VoiceCommandWriteGate narrows further to S_CDU1_KEY_/S_CDU2_KEY_.
        "system.switches.S_CDU",

        // Pilot-seat flip (speech.pilotSeat = "right"): the virtual pilot then flies the
        // LEFT side, so its control sweep writes the captain-side analogs. ControlSweepService
        // enforces the seat check — with the default left seat these are never written.
        "system.analog.A_FC_CAPT_",

        // Voice FCU actions: value analogs (A_FCU_HEADING/ALTITUDE/SPEED/VS) + push/pull and
        // engagement switches. FcuControls further narrows to its fixed 15-entry list.
        "system.analog.A_FCU_",
        "system.switches.S_FCU_",

        // Voice radio management: COM1/COM2 active+standby kHz analogs. RadioControls
        // enforces the standby-then-swap rule (active only ever written during a swap).
        "system.analog.R_COM",
    ];

    /// <summary>True when the dataref may be written by this application.</summary>
    public static bool IsAllowed(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (AllowedNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in AllowedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws when the dataref is not allow-listed for writing.</summary>
    public static void EnsureAllowed(string name)
    {
        if (!IsAllowed(name))
        {
            throw new InvalidOperationException(
                $"Dataref '{name}' is not on the write allow-list. Writes are gated by design — " +
                "add the name to ProsimWriteGate deliberately if this write is intended.");
        }
    }
}
