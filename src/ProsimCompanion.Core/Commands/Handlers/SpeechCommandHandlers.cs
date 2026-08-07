using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>speech.speakTest</c>.</summary>
public sealed record SpeechSpeakTestRequest
{
    /// <summary>Phrase to speak through the full arbiter/router/playback path.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// <c>speech.*</c> command handlers over the <see cref="ISpeechControl"/> seam. The seam is
/// nullable because the speech pillar is optional (degrade-not-fail).
/// </summary>
public static class SpeechCommandHandlers
{
    /// <summary>Cap on the test phrase length — SpeakTest routes into real TTS (possibly a
    /// paid cloud voice), so an unbounded string from a remote client is not acceptable.</summary>
    public const int MaxTextLength = 300;

    public static void Register(CommandRegistry registry, ISpeechControl? speechControl)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<SpeechSpeakTestRequest, CommandResult>(
            "speech.speakTest",
            (request, _) => Task.FromResult(SpeakTest(speechControl, request)));
    }

    private static CommandResult SpeakTest(ISpeechControl? speechControl, SpeechSpeakTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (speechControl is null)
        {
            return CommandResult.Unavailable("The speech pillar is not running.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new CommandValidationException("text is required, e.g. { \"text\": \"Radio check\" }.");
        }

        var text = request.Text.Trim();
        if (text.Length > MaxTextLength)
        {
            throw new CommandValidationException($"text must be {MaxTextLength} characters or fewer.");
        }

        speechControl.SpeakTest(text);
        return CommandResult.Ok("Test phrase queued at Normal priority.");
    }
}
