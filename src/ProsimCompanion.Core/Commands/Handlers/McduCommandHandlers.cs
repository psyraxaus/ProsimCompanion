using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// <c>mcdu.checkBox</c>: the FO's MCDU check (D-I-F-S-R-I-P-P), the same run as the voice
/// command "check the box". The Speech pillar implements the seam; absent, the command answers
/// <see cref="CommandOutcome.Unavailable"/>.
/// </summary>
public static class McduCommandHandlers
{
    public static void Register(CommandRegistry registry, IBoxCheckControl? boxCheck)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<EmptyCommandRequest, CommandResult>(
            "mcdu.checkBox",
            (_, _) => Task.FromResult(CheckBox(boxCheck, "command")));
    }

    /// <summary>Pure mapping of the seam's answer to a command result — public for tests.</summary>
    public static CommandResult CheckBox(IBoxCheckControl? boxCheck, string source)
    {
        if (boxCheck is null)
        {
            return CommandResult.Unavailable("The voice First Officer is not running.");
        }

        return boxCheck.Start(source) switch
        {
            BoxCheckStart.Started => CommandResult.Ok("MCDU check started — the FO reports when it is done."),
            BoxCheckStart.AlreadyRunning => CommandResult.AlreadySatisfied("The MCDU check is already running."),
            _ => CommandResult.Unavailable("The MCDU is off in Settings, or its display is not arriving from ProSim."),
        };
    }
}
