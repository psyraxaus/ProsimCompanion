namespace ProsimCompanion.Core.Commands;

/// <summary>
/// Discrete result classification for an executed command, mirrored from the predecessor's
/// Stream Deck outcome model. Every command response carries an outcome plus a human-readable
/// reason — never a bare ack — so remote surfaces (web page, Stream Deck key) can show
/// <em>why</em> nothing visibly happened, not just that the request was received.
/// </summary>
public enum CommandOutcome
{
    /// <summary>Resolved, written or actuated successfully.</summary>
    Success,

    /// <summary>Idempotent no-op: the requested state already holds, so nothing was re-fired.</summary>
    AlreadySatisfied,

    /// <summary>The action is not valid in the current flight/automation phase.</summary>
    PhaseMismatch,

    /// <summary>App or aircraft state does not permit the action right now (e.g. no checklist
    /// selected, sequence not started). Unlike a validation error, the client cannot fix this
    /// by editing the request.</summary>
    PreconditionFailed,

    /// <summary>Execution was attempted but failed (exception, downstream refusal) — see log.</summary>
    Failed,

    /// <summary>The owning subsystem is not running (degraded mode) — the command has nowhere
    /// to route to.</summary>
    Unavailable,
}

/// <summary>
/// Outcome + reason for one executed command. Not sealed on purpose: commands with a richer
/// payload (e.g. <c>fms.syncInit</c>) derive a record that adds fields, so the HTTP layer can
/// map <see cref="Outcome"/> to a status code uniformly while still serialising the extras.
/// </summary>
public record CommandResult(CommandOutcome Outcome, string Reason)
{
    public static CommandResult Ok(string reason) => new(CommandOutcome.Success, reason);

    public static CommandResult AlreadySatisfied(string reason) => new(CommandOutcome.AlreadySatisfied, reason);

    public static CommandResult PhaseMismatch(string reason) => new(CommandOutcome.PhaseMismatch, reason);

    public static CommandResult PreconditionFailed(string reason) => new(CommandOutcome.PreconditionFailed, reason);

    public static CommandResult Failed(string reason) => new(CommandOutcome.Failed, reason);

    public static CommandResult Unavailable(string reason) => new(CommandOutcome.Unavailable, reason);
}

/// <summary>
/// Request DTO for commands that take no input. A concrete (deserialisable) type rather than
/// <c>object</c> so the generic HTTP endpoint can treat every command identically: empty POST
/// body → <c>{}</c> → an instance of the registered request type.
/// </summary>
public sealed record EmptyCommandRequest;
