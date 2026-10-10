using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Sim;

/// <summary>
/// Code-level allow-list for SimVar/LVAR writes — the sim-side twin of the ProSim write gate.
/// Doubly important here because MSFS silently auto-creates unknown LVAR names as 0: an
/// unlisted (possibly misspelled) name must fail loudly at the gate, not vanish into the sim.
/// Seeded with the GSX control surface Phase 2 needs; extend deliberately per feature.
/// </summary>
public static class SimWriteGate
{
    private static readonly string[] AllowedPrefixes =
    [
        // GSX menu interaction and external-control flags (docs/integrations/gsx.md,
        // gsx-remote-api.md §8 — the write LVARs deliberately kept alongside the Remote API).
        "L:FSDT_GSX_",
        // The companion's own turnaround-progress tracking LVARs (issue #30).
        CompanionLvarNames.Prefix,
    ];

    /// <summary>Exact names (no wildcard, owner rule 2026-10-10): the SayIntentions PTT control
    /// LVARs the SI client lists under Controls → "Map PTT inside the sim" — 1 to talk, 0 to
    /// stop. Keyed from a ProSim PTT switch by the SayIntentions PTT relay.</summary>
    private static readonly string[] AllowedExact =
    [
        "L:SIAI_CONTROL_PTT_COM",
        "L:SIAI_CONTROL_PTT_COM1",
        "L:SIAI_CONTROL_PTT_COM2",
        "L:SIAI_CONTROL_PTT_INTERCOM1",
        "L:SIAI_CONTROL_PTT_INTERCOM2",
        "L:SIAI_CONTROL_PTT_INTERCOM3",
        "L:SIAI_CONTROL_PTT_GROUP",
    ];

    /// <summary>True when the name may be written by this application.</summary>
    public static bool IsAllowed(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var exact in AllowedExact)
        {
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in AllowedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws when the name is not allow-listed for writing.</summary>
    public static void EnsureAllowed(string name)
    {
        if (!IsAllowed(name))
        {
            throw new InvalidOperationException(
                $"SimVar '{name}' is not on the sim write allow-list. Writes are gated by design — " +
                "add the name/prefix to SimWriteGate deliberately if this write is intended.");
        }
    }
}
