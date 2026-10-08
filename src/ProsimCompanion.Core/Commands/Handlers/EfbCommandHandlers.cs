using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// <c>efb.*</c> command handlers: the INIT page's two reset buttons as commands, so a Stream
/// Deck or a script can restart a leg (soft) or unload the OFP (full) without the browser.
/// The seam is nullable because the ProSim pillar is optional (degrade-not-fail).
/// </summary>
public static class EfbCommandHandlers
{
    public static void Register(CommandRegistry registry, IEfbResetControl? reset)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<EmptyCommandRequest, CommandResult>(
            "efb.resetFlight",
            async (_, cancellationToken) =>
            {
                if (reset is null)
                {
                    return CommandResult.Unavailable("The ProSim EFB pipeline is not running.");
                }

                return FromResult(await reset.ResetFlightAsync(cancellationToken).ConfigureAwait(false));
            });

        registry.Register<EmptyCommandRequest, CommandResult>(
            "efb.unloadOfp",
            async (_, cancellationToken) =>
            {
                if (reset is null)
                {
                    return CommandResult.Unavailable("The ProSim EFB pipeline is not running.");
                }

                return FromResult(await reset.UnloadOfpAsync(cancellationToken).ConfigureAwait(false));
            });
    }

    /// <summary>Pure mapping: a reset that ProSim refused in part is a failure that names what
    /// was refused — the app-side part still ran, and the reason says so.</summary>
    public static CommandResult FromResult(EfbResetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var what = result.Kind == EfbResetKind.Soft
            ? "Flight reset (soft): INIT overrides cleared, loadsheet cycle back to edition 1."
            : "OFP unloaded (full): ProSim EFB plan cleared, flight-cycle stores reset.";
        return result.Ok
            ? CommandResult.Ok(what)
            : CommandResult.Failed($"{what} ProSim refused: {string.Join(", ", result.Failed)} — see log.");
    }
}
