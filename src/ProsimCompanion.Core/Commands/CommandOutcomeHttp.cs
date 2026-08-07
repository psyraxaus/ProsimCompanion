namespace ProsimCompanion.Core.Commands;

/// <summary>
/// The one place the outcome → HTTP status contract lives. Kept in Core (plain ints, no
/// ASP.NET dependency) so it is unit-testable from the Core test project and so any future
/// HTTP surface reuses the exact mapping instead of re-deriving it.
/// </summary>
public static class CommandOutcomeHttp
{
    /// <summary>
    /// Maps a command outcome to the HTTP status the command API returns. 409 for both phase
    /// and precondition failures: the request was well-formed but conflicts with current state;
    /// the client should re-read state, not retry blindly.
    /// </summary>
    public static int StatusCodeFor(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Success or CommandOutcome.AlreadySatisfied => 200,
        CommandOutcome.PhaseMismatch or CommandOutcome.PreconditionFailed => 409,
        CommandOutcome.Unavailable => 503,
        _ => 500, // Failed, plus any future outcome nobody mapped — a server-side problem either way.
    };
}
