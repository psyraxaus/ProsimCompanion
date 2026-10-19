using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>The words one question's accept/decline commands answer with.</summary>
/// <param name="Unavailable">The store is not running.</param>
/// <param name="CannotDeclineOnceAccepted">A decline after a yes: the thing is already on its way.</param>
/// <param name="AlreadyDeclined">A second decline, given the recorded reason.</param>
/// <param name="Declined">The decline succeeded.</param>
/// <param name="AlreadyAccepted">A second yes.</param>
/// <param name="Accepted">The yes succeeded.</param>
public sealed record CaptainsQuestionCommandTexts(
    string Unavailable,
    string CannotDeclineOnceAccepted,
    Func<string, string> AlreadyDeclined,
    string Declined,
    string AlreadyAccepted,
    string Accepted);

/// <summary>
/// The command-handler family of a yes/no captain's question (architecture review C2): an
/// accept and a decline command over one <see cref="ICaptainsQuestionStore"/>, at any time
/// while the question stands — the FO's listen window never decides it (issue #166). These
/// are store writes, not GSX calls: they work with GSX disconnected (the raiser applies them
/// when it next runs), which is why they do not ride <see cref="IGsxServiceControl"/>. The
/// de-icing commands are the one instance today (<see cref="DeiceCommandHandlers"/>); the
/// other questions have no commands because the command API documents no generic answer
/// pattern yet — adding one is a <c>Register</c> call with its words, not a new handler.
/// </summary>
public static class CaptainsQuestionCommandHandlers
{
    public static void Register(
        CommandRegistry registry,
        string acceptCommand,
        string declineCommand,
        ICaptainsQuestionStore? store,
        CaptainsQuestionCommandTexts texts)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(declineCommand);
        ArgumentNullException.ThrowIfNull(texts);

        registry.Register<EmptyCommandRequest, CommandResult>(
            declineCommand,
            (_, _) => Task.FromResult(Decline(store, "voice", texts)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            acceptCommand,
            (_, _) => Task.FromResult(Accept(store, "voice", texts)));
    }

    /// <summary>Pure so the outcomes are unit-testable; <paramref name="source"/> is what the
    /// store's Answered edge names.</summary>
    public static CommandResult Decline(ICaptainsQuestionStore? store, string source, CaptainsQuestionCommandTexts texts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(texts);
        if (store is null)
        {
            return CommandResult.Unavailable(texts.Unavailable);
        }

        var asking = store.Asking;
        if (asking.Accepted)
        {
            return CommandResult.PreconditionFailed(texts.CannotDeclineOnceAccepted);
        }

        if (asking.Declined is { } reason)
        {
            return CommandResult.AlreadySatisfied(texts.AlreadyDeclined(reason));
        }

        store.Decline(source, $"declined by the captain ({source})");
        return CommandResult.Ok(texts.Declined);
    }

    public static CommandResult Accept(ICaptainsQuestionStore? store, string source, CaptainsQuestionCommandTexts texts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(texts);
        if (store is null)
        {
            return CommandResult.Unavailable(texts.Unavailable);
        }

        if (store.Asking.Accepted)
        {
            return CommandResult.AlreadySatisfied(texts.AlreadyAccepted);
        }

        store.Accept(source);
        return CommandResult.Ok(texts.Accepted);
    }
}
