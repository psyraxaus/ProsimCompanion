using System.Globalization;

namespace ProsimCompanion.Core.Debrief;

/// <summary>
/// Deterministic, number-safe spoken debrief built straight from the facts. This is the ONLY
/// composer in this slice — LLM styling (with number verification) is deferred, so every
/// number spoken traces verbatim to a session event. Line order carried from Prosim2FO's
/// fallback template: header, times, lift-off, abnormals, approach verdict, touchdown,
/// checklists, tech log, Full-only activity counts, fuel, sign-off.
/// </summary>
public static class DebriefTemplate
{
    public static string Build(DebriefFacts facts, DebriefVerbosity verbosity)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var parts = new List<string> { "Debrief." };
        if (facts.BlockMinutes is { } block)
        {
            parts.Add($"Block time {block} minutes.");
        }

        if (facts.FlightMinutes is { } airborne)
        {
            parts.Add($"Airborne {airborne} minutes.");
        }

        if (facts.LiftoffIasKt is { } liftoff)
        {
            parts.Add($"Lift-off at {liftoff.ToString("0", CultureInfo.InvariantCulture)} knots.");
        }

        foreach (var abnormal in facts.Abnormals)
        {
            parts.Add(abnormal.Cleared
                ? $"Handled {abnormal.Title}."
                : $"Handled {abnormal.Title}, not fully cleared.");
        }

        // One approach verdict: the first unstable gate wins (that's the story of the
        // approach); otherwise the first stable gate; an indeterminate-only approach is silent.
        var unstable = facts.Gates.FirstOrDefault(
            g => string.Equals(g.Result, "unstable", StringComparison.OrdinalIgnoreCase));
        var stable = facts.Gates.FirstOrDefault(
            g => string.Equals(g.Result, "stable", StringComparison.OrdinalIgnoreCase));
        if (unstable is not null)
        {
            parts.Add($"Approach unstable at the {unstable.Name} foot gate"
                + (unstable.FailingCriterion is { } criterion ? $", {criterion}." : "."));
        }
        else if (stable is not null)
        {
            parts.Add($"Approach stabilized at the {stable.Name} foot gate.");
        }

        if (facts.TouchdownGroundSpeedKt is { } touchdown)
        {
            parts.Add($"Touchdown {touchdown.ToString("0", CultureInfo.InvariantCulture)} knots.");
        }

        if (facts.ChecklistsCompleted > 0)
        {
            parts.Add($"{facts.ChecklistsCompleted} checklists complete.");
        }

        if (facts.DefectsRaised > 0)
        {
            parts.Add(facts.DefectsRaised == 1
                ? "One defect entered in the tech log."
                : $"{facts.DefectsRaised} defects entered in the tech log.");
        }

        if (facts.DefectsRectified > 0)
        {
            parts.Add(facts.DefectsRectified == 1
                ? "One defect rectified."
                : $"{facts.DefectsRectified} defects rectified.");
        }

        if (verbosity == DebriefVerbosity.Full)
        {
            if (facts.CalloutsFired > 0)
            {
                parts.Add($"{facts.CalloutsFired} callouts made.");
            }

            if (facts.Advisories.Count > 0)
            {
                parts.Add($"{facts.Advisories.Count} advisories raised.");
            }

            if (facts.RadioTunes > 0)
            {
                parts.Add($"{facts.RadioTunes} radio tunes handled.");
            }

            if (facts.MemoryDrills > 0)
            {
                parts.Add($"{facts.MemoryDrills} memory-item drills called.");
            }

            if (facts.Degradations > 0)
            {
                parts.Add($"{facts.Degradations} system notes.");
            }
        }

        if (facts.FinalFobKg is { } fob)
        {
            parts.Add($"Fuel on board {Tonnes(fob)} tonnes.");
            if (verbosity == DebriefVerbosity.Full && facts.FuelUsedKg is { } used)
            {
                parts.Add($"About {Tonnes(used)} tonnes used.");
            }
        }

        parts.Add("Good flight.");
        return string.Join(" ", parts);
    }

    private static string Tonnes(double kg) => (kg / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
}
