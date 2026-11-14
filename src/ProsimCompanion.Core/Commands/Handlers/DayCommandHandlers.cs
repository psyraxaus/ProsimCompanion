using ProsimCompanion.Core.Day;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// <c>day.discard</c> (issue #239): the Duty Day page's "Discard this day" as a command, so a
/// Stream Deck or a script can drop the open day of a flight that was given up. Distinct from
/// the end of duty (voice "end duty day", the page's End button): no debrief, the unfinished
/// leg is not recorded, the state file is removed. The seam is nullable because the Speech
/// pillar, which owns the duty day, is optional (degrade-not-fail).
/// </summary>
public static class DayCommandHandlers
{
    public static void Register(CommandRegistry registry, IDayControl? day)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<EmptyCommandRequest, CommandResult>(
            "day.discard",
            (_, _) => Task.FromResult(Discard(day)));
    }

    /// <summary>Pure so the outcomes are unit-testable.</summary>
    public static CommandResult Discard(IDayControl? day)
    {
        if (day is null)
        {
            return CommandResult.Unavailable("Company day mode is not running.");
        }

        return day.DiscardDay()
            ? CommandResult.Ok("Duty day discarded as abandoned — the unfinished leg is not recorded and nothing is debriefed.")
            : CommandResult.AlreadySatisfied("No duty day is open.");
    }
}
