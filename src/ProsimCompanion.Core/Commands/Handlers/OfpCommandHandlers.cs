using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// <c>ofp.*</c> command handlers. <c>ofp.fetch</c> is the manual "fetch OFP now" button as a
/// command: it forces a re-fetch/re-import even when ProSim already reports the plan imported,
/// so an OFP regenerated on SimBrief is picked up.
/// </summary>
public static class OfpCommandHandlers
{
    public static void Register(CommandRegistry registry, ISimbriefImporter? importer)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<EmptyCommandRequest, CommandResult>(
            "ofp.fetch",
            async (_, cancellationToken) =>
            {
                if (importer is null)
                {
                    return CommandResult.Unavailable("The SimBrief importer is not running.");
                }

                var outcome = await importer.TryImportAsync(force: true, cancellationToken).ConfigureAwait(false);
                return FromImportOutcome(outcome);
            });
    }

    /// <summary>
    /// Pure mapping from the importer's outcome to the command outcome model. NoPilotId is a
    /// precondition (fix it in ProSim's EFB settings, not in the request) rather than a
    /// validation error; AlreadyImported cannot normally occur with <c>force: true</c> but is
    /// mapped anyway so the contract survives importer changes.
    /// </summary>
    public static CommandResult FromImportOutcome(SimbriefImportOutcome outcome) => outcome switch
    {
        SimbriefImportOutcome.Imported =>
            CommandResult.Ok("OFP fetched from SimBrief and imported into the ProSim EFB."),
        SimbriefImportOutcome.AlreadyImported =>
            CommandResult.AlreadySatisfied("ProSim already reports this plan imported."),
        SimbriefImportOutcome.NoPilotId =>
            CommandResult.PreconditionFailed("No SimBrief pilot id in ProSim (efb.simbrief.id is empty)."),
        SimbriefImportOutcome.FetchFailed =>
            CommandResult.Failed("SimBrief fetch failed — see log."),
        SimbriefImportOutcome.ImportFailed =>
            CommandResult.Failed("Importing the OFP into ProSim failed — see log."),
        _ => CommandResult.Failed($"Unexpected import outcome '{outcome}'."),
    };
}
