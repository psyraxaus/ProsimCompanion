namespace ProsimCompanion.Core.Commands;

/// <summary>
/// Base type for errors originating inside the command registry or its handlers. HTTP callers
/// map subclasses to status codes (<see cref="CommandNotFoundException"/> → 404,
/// <see cref="CommandValidationException"/> → 400, anything else → 500); non-HTTP callers can
/// catch the base type to distinguish command-seam failures from programming errors.
/// </summary>
public class CommandException : Exception
{
    public CommandException(string message)
        : base(message)
    {
    }

    public CommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CommandException()
    {
    }
}

/// <summary>Thrown by <see cref="CommandRegistry"/> when no handler carries the requested name.</summary>
public sealed class CommandNotFoundException : CommandException
{
    public CommandNotFoundException(string name)
        : base($"Command '{name}' is not registered.")
    {
        CommandName = name;
    }

    public CommandNotFoundException()
    {
        CommandName = "";
    }

    public CommandNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        CommandName = "";
    }

    /// <summary>The name that was requested — kept separate from the message for diagnostics.</summary>
    public string CommandName { get; }
}

/// <summary>
/// Raised by handlers when the inbound request is malformed, references missing state (e.g. an
/// unknown checklist name), or fails a rule the client could plausibly fix and retry. Distinct
/// from a <see cref="CommandOutcome.PreconditionFailed"/> result, which reports app/aircraft
/// state the client cannot change by editing the request.
/// </summary>
public sealed class CommandValidationException : CommandException
{
    public CommandValidationException(string message)
        : base(message)
    {
    }

    public CommandValidationException()
    {
    }

    public CommandValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
