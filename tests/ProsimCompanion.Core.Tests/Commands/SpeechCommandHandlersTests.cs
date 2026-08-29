using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

/// <summary>Outcome semantics of the pilot "ear off" latch commands behind the Stream Deck
/// Voice Pause key: absent pillar → unavailable, no-op → alreadySatisfied, toggle reports
/// the NEW state.</summary>
public sealed class SpeechCommandHandlersTests
{
    private sealed class FakeListeningControl : IVoiceListeningControl
    {
        public bool Paused { get; private set; }

        public bool SetPaused(bool paused)
        {
            if (Paused == paused)
            {
                return false;
            }

            Paused = paused;
            return true;
        }
    }

    [Fact]
    public void AbsentPillar_AnswersUnavailable()
    {
        Assert.Equal(CommandOutcome.Unavailable, SpeechCommandHandlers.SetListeningPaused(null, paused: true).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, SpeechCommandHandlers.ToggleListening(null).Outcome);
    }

    [Fact]
    public void Pause_ThenPauseAgain_IsAlreadySatisfied()
    {
        var control = new FakeListeningControl();

        var first = SpeechCommandHandlers.SetListeningPaused(control, paused: true);
        var second = SpeechCommandHandlers.SetListeningPaused(control, paused: true);

        Assert.Equal(CommandOutcome.Success, first.Outcome);
        Assert.Equal(CommandOutcome.AlreadySatisfied, second.Outcome);
        Assert.True(control.Paused);
    }

    [Fact]
    public void Resume_WhenNotPaused_IsAlreadySatisfied()
    {
        var control = new FakeListeningControl();

        var verdict = SpeechCommandHandlers.SetListeningPaused(control, paused: false);

        Assert.Equal(CommandOutcome.AlreadySatisfied, verdict.Outcome);
        Assert.False(control.Paused);
    }

    [Fact]
    public void Toggle_FlipsAndNamesTheNewState()
    {
        var control = new FakeListeningControl();

        var paused = SpeechCommandHandlers.ToggleListening(control);
        Assert.Equal(CommandOutcome.Success, paused.Outcome);
        Assert.True(control.Paused);
        Assert.Contains("paused", paused.Reason, StringComparison.OrdinalIgnoreCase);

        var resumed = SpeechCommandHandlers.ToggleListening(control);
        Assert.Equal(CommandOutcome.Success, resumed.Outcome);
        Assert.False(control.Paused);
        Assert.Contains("resumed", resumed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_ExposesAllThreeCommands()
    {
        var registry = new CommandRegistry();

        SpeechCommandHandlers.Register(registry, speechControl: null, listeningControl: null);

        Assert.Contains("speech.pauseListening", registry.Names);
        Assert.Contains("speech.resumeListening", registry.Names);
        Assert.Contains("speech.toggleListening", registry.Names);
    }
}
