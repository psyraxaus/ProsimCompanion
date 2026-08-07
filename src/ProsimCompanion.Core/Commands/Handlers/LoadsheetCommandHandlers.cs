using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// Success payload for <c>fms.syncInit</c>: the outcome plus the values actually written to
/// INIT B, in display units, so a remote surface can show what landed in the MCDU without a
/// second state read.
/// </summary>
public sealed record FmsSyncCommandResult(
    string Source,
    double ZfwTonnes,
    double ZfwCgPercent,
    double BlockTonnes)
    : CommandResult(CommandOutcome.Success, $"INIT B synced from {Source}.");

/// <summary>
/// <c>loadsheet.*</c> and <c>fms.syncInit</c> command handlers over the Core control seams
/// implemented by the Prosim project. Seams are nullable because the ProSim pillar is optional
/// (degrade-not-fail); absent seams answer <see cref="CommandOutcome.Unavailable"/>.
/// </summary>
public static class LoadsheetCommandHandlers
{
    public static void Register(
        CommandRegistry registry,
        ILoadsheetControl? loadsheetControl,
        IFmsInitSync? fmsInitSync)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<EmptyCommandRequest, CommandResult>(
            "loadsheet.generatePreliminary",
            async (_, cancellationToken) =>
            {
                if (loadsheetControl is null)
                {
                    return CommandResult.Unavailable("The loadsheet pipeline is not running.");
                }

                // The seam is degrade-not-fail (false + logged), so false carries no cause here;
                // the log line does.
                return await loadsheetControl.GeneratePreliminaryAsync(cancellationToken).ConfigureAwait(false)
                    ? CommandResult.Ok("Preliminary loadsheet generated.")
                    : CommandResult.Failed("Preliminary loadsheet generation failed — see log.");
            });

        registry.Register<EmptyCommandRequest, CommandResult>(
            "loadsheet.generateFinal",
            async (_, cancellationToken) =>
            {
                if (loadsheetControl is null)
                {
                    return CommandResult.Unavailable("The loadsheet pipeline is not running.");
                }

                return await loadsheetControl.GenerateFinalAsync(cancellationToken).ConfigureAwait(false)
                    ? CommandResult.Ok("Final loadsheet generated.")
                    : CommandResult.Failed(
                        "Final loadsheet generation failed (a preliminary must exist first) — see log.");
            });

        registry.Register<EmptyCommandRequest, CommandResult>(
            "loadsheet.resetCycle",
            (_, _) =>
            {
                if (loadsheetControl is null)
                {
                    return Task.FromResult(CommandResult.Unavailable("The loadsheet pipeline is not running."));
                }

                loadsheetControl.ResetCycle();
                return Task.FromResult(CommandResult.Ok(
                    "Loadsheet cycle reset — cached loadsheets cleared, edition counter back to 1."));
            });

        registry.Register<EmptyCommandRequest, CommandResult>(
            "fms.syncInit",
            async (_, cancellationToken) =>
            {
                if (fmsInitSync is null)
                {
                    return CommandResult.Unavailable("FMS INIT sync is not running.");
                }

                var result = await fmsInitSync.SyncAsync(cancellationToken).ConfigureAwait(false);
                return result is null
                    ? CommandResult.Failed("Nothing to sync, or the INIT B writes failed — see log.")
                    : new FmsSyncCommandResult(
                        result.Source, result.ZfwTonnes, result.ZfwCgPercent, result.BlockTonnes);
            });
    }
}
