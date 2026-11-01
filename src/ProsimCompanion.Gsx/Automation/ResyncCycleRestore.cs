using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// After a restart at the gate the startup resync seeds the departure one-shots from the
/// tracking LVARs, but the departure CYCLE itself is restored only once the leg had left the
/// gate (the snapshot part leaves it to the resync). When every configured departure service
/// is already completed, the cycle is over: it is marked started and complete, and the
/// "departure services completed" signal (ready for pushback) is raised, so the flight phase
/// reaches Departure and the status board reads complete (EHAM 2026-11-01: the app restarted
/// after boarding and sat in PREFLIGHT with the beacon on). Pure.
/// </summary>
public static class ResyncCycleRestore
{
    /// <summary>True when there is at least one configured step and every step's service has
    /// completed this cycle (seeded or observed). A single open step means the sequencer still
    /// has work — the cycle stays unstarted for the voice gate or the turnaround auto-start.</summary>
    public static bool AllConfiguredCompleted(IReadOnlyList<DepartureServiceStep> steps, Func<string, bool> completed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(completed);

        if (steps.Count == 0)
        {
            return false;
        }

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Service) || !completed(step.Service))
            {
                return false;
            }
        }

        return true;
    }
}
