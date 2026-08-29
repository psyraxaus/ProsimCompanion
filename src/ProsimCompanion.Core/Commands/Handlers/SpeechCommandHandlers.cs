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

    public static void Register(
        CommandRegistry registry,
        ISpeechControl? speechControl,
        IVoiceListeningControl? listeningControl = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<SpeechSpeakTestRequest, CommandResult>(
            "speech.speakTest",
            (request, _) => Task.FromResult(SpeakTest(speechControl, request)));

        // Pilot "ear off" latch — the Stream Deck toggle key and the web button. Deliberately
        // silent (no FO acknowledgement): the pilot is about to talk to a real person.
        registry.Register<EmptyCommandRequest, CommandResult>(
            "speech.pauseListening",
            (_, _) => Task.FromResult(SetListeningPaused(listeningControl, paused: true)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "speech.resumeListening",
            (_, _) => Task.FromResult(SetListeningPaused(listeningControl, paused: false)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "speech.toggleListening",
            (_, _) => Task.FromResult(ToggleListening(listeningControl)));
    }

    /// <summary>Pure verdict for pause/resume: already in the requested state answers
    /// <c>alreadySatisfied</c> (the Stream Deck key flashes OK either way, but the reason text
    /// tells the pilot nothing changed).</summary>
    public static CommandResult SetListeningPaused(IVoiceListeningControl? control, bool paused)
    {
        if (control is null)
        {
            return CommandResult.Unavailable("The speech pillar is not running.");
        }

        if (!control.SetPaused(paused))
        {
            return CommandResult.AlreadySatisfied(
                paused ? "Voice recognition is already paused." : "Voice recognition is already listening.");
        }

        return CommandResult.Ok(paused ? "Voice recognition paused." : "Voice recognition resumed.");
    }

    /// <summary>One-key flow: flips the latch and reports the NEW state in the reason.</summary>
    public static CommandResult ToggleListening(IVoiceListeningControl? control)
    {
        if (control is null)
        {
            return CommandResult.Unavailable("The speech pillar is not running.");
        }

        return SetListeningPaused(control, paused: !control.Paused);
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
