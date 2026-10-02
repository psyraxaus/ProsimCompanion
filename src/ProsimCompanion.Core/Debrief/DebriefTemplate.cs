using System.Globalization;
using ProsimCompanion.Core.Speech;

namespace ProsimCompanion.Core.Debrief;

/// <summary>
/// Deterministic, number-safe spoken debrief built straight from the facts — the floor under
/// the optional LLM styling: every number spoken traces verbatim to a session event. Line
/// order carried from Prosim2FO's fallback template: header, times, lift-off, abnormals,
/// approach verdict, touchdown, checklists, tech log, Full-only activity counts, fuel,
/// sign-off. Since issue #147 the lines are also available one by one
/// (<see cref="Sections"/>), so a streamed LLM debrief that is cut short can be finished with
/// exactly the lines the pilot has not heard yet.
/// </summary>
public static class DebriefTemplate
{
    /// <summary>The whole template as one text: the sections joined by a space.</summary>
    public static string Build(DebriefFacts facts, DebriefVerbosity verbosity)
        => string.Join(" ", Sections(facts, verbosity).Select(section => section.Text));

    /// <summary>The template line by line, each with the figures and words that identify it
    /// in other wording (see <see cref="NarrationSection"/>).</summary>
    public static IReadOnlyList<NarrationSection> Sections(DebriefFacts facts, DebriefVerbosity verbosity)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var parts = new List<NarrationSection> { new("header", "Debrief.") { IsOpening = true } };
        if (facts.BlockMinutes is { } block)
        {
            parts.Add(new("block", $"Block time {block} minutes.") { Numbers = [block], AnyOf = ["block"] });
        }

        if (facts.FlightMinutes is { } airborne)
        {
            parts.Add(new("airborne", $"Airborne {airborne} minutes.")
            {
                Numbers = [airborne],
                AnyOf = ["airborne", "flight time", "in the air", "flying"],
            });
        }

        if (facts.LiftoffIasKt is { } liftoff)
        {
            parts.Add(new("liftoff", $"Lift-off at {liftoff.ToString("0", CultureInfo.InvariantCulture)} knots.")
            {
                Numbers = [Math.Round(liftoff)],
                AnyOf = ["lift", "rotat"],
            });
        }

        foreach (var abnormal in facts.Abnormals)
        {
            parts.Add(new("abnormal", abnormal.Cleared
                ? $"Handled {abnormal.Title}."
                : $"Handled {abnormal.Title}, not fully cleared.") { AnyOf = [abnormal.Title] });
        }

        // One approach verdict: the first unstable gate wins (that's the story of the
        // approach); otherwise the first stable gate; an indeterminate-only approach is silent.
        var unstable = facts.Gates.FirstOrDefault(
            g => string.Equals(g.Result, "unstable", StringComparison.OrdinalIgnoreCase));
        var stable = facts.Gates.FirstOrDefault(
            g => string.Equals(g.Result, "stable", StringComparison.OrdinalIgnoreCase));
        if (unstable is not null)
        {
            parts.Add(new("approach", $"Approach unstable at the {unstable.Name} foot gate"
                + (unstable.FailingCriterion is { } criterion ? $", {criterion}." : ".")) { AnyOf = ["unstable", "unstabil"] });
        }
        else if (stable is not null)
        {
            parts.Add(new("approach", $"Approach stabilized at the {stable.Name} foot gate.") { AnyOf = ["stabil", "stable"] });
        }

        if (facts.TouchdownGroundSpeedKt is { } touchdown)
        {
            parts.Add(new("touchdown", $"Touchdown {touchdown.ToString("0", CultureInfo.InvariantCulture)} knots.")
            {
                Numbers = [Math.Round(touchdown)],
                AnyOf = ["touch", "landing", "landed"],
            });
        }

        if (facts.TouchdownVerticalSpeedFpm is { } rate)
        {
            // The rate is spoken as "minus 180" (a word, not a numeral), so the number check is
            // on the rounded magnitude the LLM is allowed to quote.
            parts.Add(new("touchdown-rate", $"Touchdown at {TouchdownRate(rate)} feet per minute.")
            {
                Numbers = [Math.Abs(RoundedTouchdownRate(rate))],
                AnyOf = ["feet per minute", "fpm", "rate"],
            });
        }

        if (facts.ChecklistsCompleted > 0)
        {
            parts.Add(new("checklists", $"{facts.ChecklistsCompleted} checklists complete.")
            {
                Numbers = [facts.ChecklistsCompleted],
                AnyOf = ["checklist"],
            });
        }

        if (facts.DefectsRaised > 0)
        {
            parts.Add(new("defects-raised", facts.DefectsRaised == 1
                ? "One defect entered in the tech log."
                : $"{facts.DefectsRaised} defects entered in the tech log.")
            {
                Numbers = [facts.DefectsRaised],
                AnyOf = ["defect", "tech log"],
            });
        }

        if (facts.DefectsRectified > 0)
        {
            parts.Add(new("defects-rectified", facts.DefectsRectified == 1
                ? "One defect rectified."
                : $"{facts.DefectsRectified} defects rectified.")
            {
                Numbers = [facts.DefectsRectified],
                AnyOf = ["rectif"],
            });
        }

        if (verbosity == DebriefVerbosity.Full)
        {
            if (facts.CalloutsFired > 0)
            {
                parts.Add(new("callouts", $"{facts.CalloutsFired} callouts made.") { Numbers = [facts.CalloutsFired], AnyOf = ["callout"] });
            }

            if (facts.Advisories.Count > 0)
            {
                parts.Add(new("advisories", $"{facts.Advisories.Count} advisories raised.") { Numbers = [facts.Advisories.Count], AnyOf = ["advisor"] });
            }

            if (facts.RadioTunes > 0)
            {
                parts.Add(new("radio", $"{facts.RadioTunes} radio tunes handled.") { Numbers = [facts.RadioTunes], AnyOf = ["radio", "tune"] });
            }

            if (facts.MemoryDrills > 0)
            {
                parts.Add(new("drills", $"{facts.MemoryDrills} memory-item drills called.") { Numbers = [facts.MemoryDrills], AnyOf = ["drill", "memory"] });
            }

            if (facts.Degradations > 0)
            {
                parts.Add(new("degradations", $"{facts.Degradations} system notes.") { Numbers = [facts.Degradations], AnyOf = ["system note", "degrad"] });
            }
        }

        if (facts.FinalFobKg is { } fob)
        {
            parts.Add(new("fuel", $"Fuel on board {Tonnes(fob)} tonnes.") { Numbers = [Math.Round(fob / 1000.0, 1)], AnyOf = ["fuel"] });
            if (verbosity == DebriefVerbosity.Full && facts.FuelUsedKg is { } used)
            {
                parts.Add(new("fuel-used", $"About {Tonnes(used)} tonnes used.") { Numbers = [Math.Round(used / 1000.0, 1)], AnyOf = ["used", "burn"] });
            }
        }

        parts.Add(new("sign-off", "Good flight.") { IsClosing = true });
        return parts;
    }

    private static string Tonnes(double kg) => (kg / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The spoken touchdown rate: nearest ten feet per minute (a recorder reading of
    /// −183 is not more true than "minus 180", and it is easier on the ear), the sign as the
    /// word "minus" so no TTS engine reads a hyphen as "dash" or drops it. Shared with the
    /// LLM fact sheet (<see cref="DebriefLlm"/>) so both paths speak the same figure.</summary>
    public static string TouchdownRate(double verticalSpeedFpm)
    {
        var rounded = RoundedTouchdownRate(verticalSpeedFpm);
        var magnitude = Math.Abs(rounded).ToString("0", CultureInfo.InvariantCulture);
        return rounded < 0 ? "minus " + magnitude : magnitude;
    }

    /// <summary>The touchdown rate to the nearest ten feet per minute, sign kept.</summary>
    public static double RoundedTouchdownRate(double verticalSpeedFpm)
        => Math.Round(verticalSpeedFpm / 10.0, MidpointRounding.AwayFromZero) * 10.0;
}
