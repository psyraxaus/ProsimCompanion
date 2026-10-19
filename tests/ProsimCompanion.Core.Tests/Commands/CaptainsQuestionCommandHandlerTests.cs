using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

/// <summary>The yes/no command family over a captain's-question store (C2): the outcomes per
/// state, with the store absent, and the de-icing instance's names and words unchanged
/// (<c>gsx.acceptDeice</c> / <c>gsx.declineDeice</c>, bound by the voice phrase catalogue and
/// the Stream Deck plugin).</summary>
public sealed class CaptainsQuestionCommandHandlerTests
{
    private static readonly CaptainsQuestionCommandTexts Texts = new(
        Unavailable: "not running",
        CannotDeclineOnceAccepted: "already yes",
        AlreadyDeclined: reason => $"already no: {reason}",
        Declined: "no",
        AlreadyAccepted: "yes again",
        Accepted: "yes");

    [Fact]
    public void Decline_AnswersAnOpenQuestion_ThenIsAlreadySatisfied_AndRefusesAfterAYes()
    {
        var signals = new GroundOpsSignals();
        using var store = new TurnaroundStartStore(signals);
        store.Ask("Next plan loaded — commence ground services?");

        var declined = CaptainsQuestionCommandHandlers.Decline(store, "streamdeck", Texts);
        Assert.Equal(CommandOutcome.Success, declined.Outcome);
        Assert.Equal("no", declined.Reason);
        Assert.Equal("declined by the captain (streamdeck)", store.Snapshot().Declined);

        var again = CaptainsQuestionCommandHandlers.Decline(store, "voice", Texts);
        Assert.Equal(CommandOutcome.AlreadySatisfied, again.Outcome);
        Assert.Equal("already no: declined by the captain (streamdeck)", again.Reason);

        signals.RaiseFlightCycleReset();
        store.Accept("web");
        Assert.Equal(CommandOutcome.PreconditionFailed, CaptainsQuestionCommandHandlers.Decline(store, "voice", Texts).Outcome);
        Assert.Equal(CommandOutcome.AlreadySatisfied, CaptainsQuestionCommandHandlers.Accept(store, "voice", Texts).Outcome);
    }

    [Fact]
    public void Accept_RecordsTheSource_EvenBeforeTheQuestionWasAsked()
    {
        using var store = new TurnaroundStartStore(new GroundOpsSignals());
        (bool Accepted, string Source)? answer = null;
        store.Answered += (_, a) => answer = a;

        var accepted = CaptainsQuestionCommandHandlers.Accept(store, "api", Texts);

        Assert.Equal(CommandOutcome.Success, accepted.Outcome);
        Assert.Equal("yes", accepted.Reason);
        Assert.Equal((true, "api"), answer);
        Assert.False(store.Ask("q")); // final for the cycle
    }

    [Fact]
    public void NoStore_IsUnavailable()
    {
        Assert.Equal(CommandOutcome.Unavailable, CaptainsQuestionCommandHandlers.Decline(null, "voice", Texts).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, CaptainsQuestionCommandHandlers.Accept(null, "voice", Texts).Outcome);
        Assert.Equal("not running", CaptainsQuestionCommandHandlers.Accept(null, "voice", Texts).Reason);
    }

    [Fact]
    public void Register_BindsTheTwoNames_WithVoiceAsTheSource()
    {
        var registry = new CommandRegistry();
        using var store = new DeiceRequestStore(new GroundOpsSignals());
        DeiceCommandHandlers.Register(registry, store);

        Assert.Equal(["gsx.acceptDeice", "gsx.declineDeice"], registry.Names);
    }

    [Fact]
    public void DeiceInstance_KeepsItsWords()
    {
        using var store = new DeiceRequestStore(new GroundOpsSignals());
        Assert.Equal("The de-icing decision store is not running.", DeiceCommandHandlers.Decline(null, "voice").Reason);

        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EFHK", "Request it?");
        Assert.Equal("De-icing accepted — it goes on the departure sequence.", DeiceCommandHandlers.Accept(store, "voice").Reason);
        Assert.Equal("De-icing is already requested this departure.", DeiceCommandHandlers.Accept(store, "voice").Reason);
        Assert.Equal("De-icing is already requested this departure — it cannot be declined from here.", DeiceCommandHandlers.Decline(store, "voice").Reason);
    }
}
