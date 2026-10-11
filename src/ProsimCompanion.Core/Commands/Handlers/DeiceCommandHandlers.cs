using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// <c>gsx.declineDeice</c> / <c>gsx.acceptDeice</c> over the Core <see cref="DeiceRequestStore"/>
/// (issue #166): the captain's answer to the FO's "request de-icing?" question by voice,
/// Stream Deck or API, at any time while the question stands — the FO's listen window no
/// longer decides it. These are store writes, not GSX calls: they work with GSX disconnected
/// (the sequencer applies them when it next runs), which is why they do not ride
/// <see cref="IGsxServiceControl"/>. "Request de-icing" (<c>gsx.requestDeice</c>) remains the
/// direct call and counts as "yes" when the question stands.
/// </summary>
public static class DeiceCommandHandlers
{
    public static void Register(CommandRegistry registry, DeiceRequestStore? deice)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<EmptyCommandRequest, CommandResult>(
            "gsx.declineDeice",
            (_, _) => Task.FromResult(Decline(deice, "voice")));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "gsx.acceptDeice",
            (_, _) => Task.FromResult(Accept(deice, "voice")));
    }

    /// <summary>Pure so the outcomes are unit-testable; <paramref name="source"/> is what the
    /// store's Answered edge names.</summary>
    public static CommandResult Decline(DeiceRequestStore? deice, string source)
    {
        if (deice is null)
        {
            return CommandResult.Unavailable("The de-icing decision store is not running.");
        }

        var snapshot = deice.Snapshot();
        if (snapshot.RequestThisCycle)
        {
            return CommandResult.PreconditionFailed("De-icing is already requested this departure — it cannot be declined from here.");
        }

        if (snapshot.Declined is not null)
        {
            return CommandResult.AlreadySatisfied($"De-icing already declined: {snapshot.Declined}.");
        }

        deice.Decline(source, $"declined by the captain ({source})");
        return CommandResult.Ok("No de-icing this departure.");
    }

    public static CommandResult Accept(DeiceRequestStore? deice, string source)
    {
        if (deice is null)
        {
            return CommandResult.Unavailable("The de-icing decision store is not running.");
        }

        if (deice.Snapshot().RequestThisCycle)
        {
            return CommandResult.AlreadySatisfied("De-icing is already requested this departure.");
        }

        deice.Accept(source);
        return CommandResult.Ok("De-icing accepted — it goes on the departure sequence.");
    }
}
