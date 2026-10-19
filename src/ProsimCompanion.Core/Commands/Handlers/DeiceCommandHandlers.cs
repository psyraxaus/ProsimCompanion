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
/// <para>The de-icing instance of <see cref="CaptainsQuestionCommandHandlers"/> (C2): the
/// command names and the outcome texts are unchanged — the voice phrase catalogue and the
/// Stream Deck plugin bind them.</para>
/// </summary>
public static class DeiceCommandHandlers
{
    private static readonly CaptainsQuestionCommandTexts Texts = new(
        Unavailable: "The de-icing decision store is not running.",
        CannotDeclineOnceAccepted: "De-icing is already requested this departure — it cannot be declined from here.",
        AlreadyDeclined: reason => $"De-icing already declined: {reason}.",
        Declined: "No de-icing this departure.",
        AlreadyAccepted: "De-icing is already requested this departure.",
        Accepted: "De-icing accepted — it goes on the departure sequence.");

    public static void Register(CommandRegistry registry, DeiceRequestStore? deice)
        => CaptainsQuestionCommandHandlers.Register(registry, "gsx.acceptDeice", "gsx.declineDeice", deice, Texts);

    /// <summary>Pure so the outcomes are unit-testable; <paramref name="source"/> is what the
    /// store's Answered edge names.</summary>
    public static CommandResult Decline(DeiceRequestStore? deice, string source)
        => CaptainsQuestionCommandHandlers.Decline(deice, source, Texts);

    public static CommandResult Accept(DeiceRequestStore? deice, string source)
        => CaptainsQuestionCommandHandlers.Accept(deice, source, Texts);
}
