using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The FO must never answer its own voice (2026-10-10 VBAN loop): the mic gate
/// around playback plus the tail, and the transcript-overlap check behind it.</summary>
public sealed class SelfEchoGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 10, 32, 0, TimeSpan.Zero);

    [Fact]
    public void MicGate_SuppressesWhilePlaying_AndForTheTailAfter()
    {
        var guard = new SelfEchoGuard();
        Assert.False(guard.IsSuppressed(T0, 700));

        guard.Update(speaking: true, T0);
        Assert.True(guard.IsSuppressed(T0.AddSeconds(3), 700));

        guard.Update(speaking: false, T0.AddSeconds(4));
        Assert.True(guard.IsSuppressed(T0.AddSeconds(4).AddMilliseconds(699), 700));
        Assert.False(guard.IsSuppressed(T0.AddSeconds(4).AddMilliseconds(700), 700));
        Assert.False(guard.IsSuppressed(T0.AddSeconds(5), 0));
    }

    [Fact]
    public void MicGate_TailRestartsFromTheLastStop()
    {
        var guard = new SelfEchoGuard();
        guard.Update(true, T0);
        guard.Update(false, T0.AddSeconds(1));
        guard.Update(true, T0.AddSeconds(1.2));   // next segment of the same answer
        guard.Update(false, T0.AddSeconds(2));

        Assert.True(guard.IsSuppressed(T0.AddSeconds(2.5), 700));
        Assert.False(guard.IsSuppressed(T0.AddSeconds(2.8), 700));
    }

    [Theory]
    // The loop from the flight log: each "question" was the FO's previous line, sometimes with a tail word.
    [InlineData("See you at the next line-up.\n Didn't catch that.", "You too. See you at the next line up.", true)]
    [InlineData("I'll bring the coffee. Stand by.", "Sounds like we're planning a date. I'll bring the coffee.", true)]
    [InlineData("I don't have that.", "I don't have that.", true)]
    // Real pilot speech that happens to share a word or two is not echo.
    [InlineData("set flaps one", "Flaps one set, speed checked.", false)]
    [InlineData("yes", "Yes captain, gear is down.", false)]
    [InlineData("gear down", "Checklist complete. Let's stick to the plan.", false)]
    public void LooksLikeOwnSpeech_MatchesTheFoLines_NotThePilot(string heard, string spoken, bool expected)
    {
        Assert.Equal(expected, SelfEchoGuard.LooksLikeOwnSpeech(heard, [spoken]));
    }

    [Fact]
    public void LooksLikeOwnSpeech_IgnoresBlankAndSingleWordTranscripts()
    {
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("", ["anything at all"]));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("Well,", ["Well, that's a relief."]));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("gear down please", []));
    }
}
