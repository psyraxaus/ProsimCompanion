using System.Collections.Frozen;

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
        "aircraft.refuel.fuelTarget",
        "aircraft.refuel.fuelTarget.kg",
        "aircraft.refuel.refuelingActive",
        "aircraft.refuel.refuelingPower",
        "aircraft.refuel.refuelingRate",
        "aircraft.fuel.total.amount.kg",

        // Ground equipment
        "groundservice.groundpower",
        "groundservice.preconditionedAir",
        "efb.chocks",
        "efb.fwdStairs",
        "efb.aftStairs",

        // ProSim native-integration flags we own while running
        "efb.autoJetway",
        "efb.autoDoor",

        // Boarding state (EFB UI tracks it; double "efb." is the real path)
        "efb.efb.boardingStatus",

        // SimBrief import flag (written by the OFP importer)
        "efb.simbriefPlanImported",

        // Planning / pax
        "efb.plannedfuel",
        "efb.plannedCargoKg",
        "efb.passengers.booked.string",
        "efb.passengerStatistics",
        "aircraft.passengers.seatOccupation.string",

        // Loadsheet pipeline (Phase 3): the EFB display slots + the ACARS uplink envelope
        "efb.prelimLoadsheet",
        "efb.finalLoadsheet",
        "efb.aoc.message.uplink",

        // MCDU INIT B sync (values in TONNES for zfw/block; zfwcg is %MAC)
        "aircraft.fms.init.block",
        "aircraft.fms.init.zfw",
        "aircraft.fms.init.zfwcg",

        // FMS PERF TO uplink (Phase 3 performance page)
        "aircraft.fms.perf.takeOff.flaps",
        "aircraft.fms.perf.takeOff.flexTemp",
        "aircraft.fms.perf.takeOff.v1",
        "aircraft.fms.perf.takeOff.vr",
        "aircraft.fms.perf.takeOff.v2",
        "aircraft.fms.perf.takeOff.ths",
        "aircraft.fms.perf.takeOff.shift",
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
