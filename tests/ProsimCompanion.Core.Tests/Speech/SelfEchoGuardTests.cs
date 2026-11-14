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

    // ---- issue #172: the 2026-10-11 EFHK→LKPR flight — four false positives, zero echoes ----

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private static SelfEchoGuard.SpokenLine Line(string text, DateTimeOffset at, bool aloud = true) => new(text, at, aloud);

    [Fact]
    public void ThePilotsAnswerToAClarifier_IsNotTheClarifierHeardBack()
    {
        // 23:07:30 FO "Say again — did you mean flight directors off?"; 23:07:34 pilot "Flight Directors off." → dropped.
        var fo = new DateTimeOffset(2026, 10, 10, 23, 07, 30, TimeSpan.Zero);
        var heardAt = fo.AddSeconds(4);
        var lines = new[] { Line("Say again — did you mean flight directors off?", fo) };

        // Without the exemption the words sit inside the FO line and the guard drops them …
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("Flight Directors off.", lines, heardAt, Window, []));
        // … the pending candidate is what the pilot is expected to say: never an echo.
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("Flight Directors off.", lines, heardAt, Window, ["flight directors off"]));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("flight directors off.", lines, heardAt, Window, ["flight directors off"]));
    }

    [Fact]
    public void AnOldLine_IsPrunedAtTranscriptTime_NotWhenTheListWasBuilt()
    {
        // 00:06:47 FO "Taxi checklist complete."; 00:07:58 pilot "Taxi Checklist." (71 s later) → dropped,
        // because the list had been rebuilt with the line inside the 30 s cutoff and never again.
        var fo = new DateTimeOffset(2026, 10, 11, 0, 6, 47, TimeSpan.Zero);
        var lines = new[] { Line("Taxi checklist complete.", fo) };

        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("Taxi Checklist.", lines, fo.AddSeconds(71), Window, []));
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("Taxi Checklist.", lines, fo.AddSeconds(5), Window, []));
    }

    [Fact]
    public void TheOpenChecklistItemsExpectedReply_IsNeverAnEcho()
    {
        // 02:53:08 FO "Chocks, as required" (the item); 02:53:39 pilot "as required." → dropped, item skipped.
        var fo = new DateTimeOffset(2026, 10, 11, 2, 53, 8, TimeSpan.Zero);
        var lines = new[] { Line("Chocks, as required", fo) };

        // 31 s later it is outside the window anyway …
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("as required.", lines, fo.AddSeconds(31), Window, []));
        // … and 3 s later the item's accepted phrase / expected response exempts it.
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("as required.", lines, fo.AddSeconds(3), Window, []));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("as required.", lines, fo.AddSeconds(3), Window, ["as required", "set"]));
    }

    [Fact]
    public void AShortTranscript_MatchesOnlyALineThatWasPlayedAloud()
    {
        var at = T0;
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("gear is down", [Line("Yes captain, gear is down.", at, aloud: true)], at.AddSeconds(2), Window, []));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("gear is down", [Line("Yes captain, gear is down.", at, aloud: false)], at.AddSeconds(2), Window, []));
    }

    [Theory]
    // The 2026-10-10 loop, through the new overload: still dropped inside the window.
    [InlineData("See you at the next line-up.\n Didn't catch that.", "You too. See you at the next line up.")]
    [InlineData("I'll bring the coffee. Stand by.", "Sounds like we're planning a date. I'll bring the coffee.")]
    [InlineData("I don't have that.", "I don't have that.")]
    [InlineData("Yes, run the departure briefing", "Yes, run the departure briefing.")]
    [InlineData("Run the departure briefing.", "Yes, run the departure briefing.")]
    public void TheRealEchoes_AreStillDropped_InsideTheWindow(string heard, string spoken)
    {
        var lines = new[] { Line(spoken, T0) };
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech(heard, lines, T0.AddSeconds(3), Window, []));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech(heard, lines, T0.AddSeconds(16), Window, []));
    }

    // ---- issue #172 again: EGLL→LOWW 2026-11-14 — a fresh command dropped twice as echo ----

    /// <summary>speech.spoken {tag: checklist.cancelled} "Descent checklist cancelled." — the playback end.</summary>
    private static readonly DateTimeOffset CancelledSpokenAt = new(2026, 11, 14, 6, 17, 58, 828, TimeSpan.Zero);

    /// <summary>asr.utterance 06:18:01.328 {durationMs: 1708} → began 0.79 s after the line; asr.self-echo 06:18:01.590.</summary>
    private static readonly DateTimeOffset FirstAttemptBegan = new DateTimeOffset(2026, 11, 14, 6, 18, 1, 328, TimeSpan.Zero).AddMilliseconds(-1708);
    private static readonly DateTimeOffset FirstAttemptHeard = new(2026, 11, 14, 6, 18, 1, 590, TimeSpan.Zero);

    /// <summary>asr.utterance 06:18:07.527 {durationMs: 2124} → began 6.6 s after the line; asr.self-echo 06:18:07.785.</summary>
    private static readonly DateTimeOffset SecondAttemptBegan = new DateTimeOffset(2026, 11, 14, 6, 18, 7, 527, TimeSpan.Zero).AddMilliseconds(-2124);
    private static readonly DateTimeOffset SecondAttemptHeard = new(2026, 11, 14, 6, 18, 7, 785, TimeSpan.Zero);

    /// <summary>The idle window the checklist left open after its cancellation, in part.</summary>
    private static readonly string[] IdleWindow = ["Descent checklist", "request Descent checklist", "cancel checklist", "gear down"];

    private static SelfEchoGuard.HeardUtterance Began(DateTimeOffset at, params string[] commands)
        => new(at, SelfEchoGuard.EchoTailFor(700), commands);

    [Fact]
    public void TheEchoTail_IsTheMicTailPlusTheMargin()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2200), SelfEchoGuard.EchoTailFor(700));
        Assert.Equal(SelfEchoGuard.EchoOnsetMargin, SelfEchoGuard.EchoTailFor(-5));
    }

    [Fact]
    public void DescentChecklist_AskedAgainAfterItsCancellation_IsACommand_NotTheCancellationHeardBack()
    {
        var lines = new[] { Line("Descent checklist cancelled.", CancelledSpokenAt) };

        // The text alone says echo, both times — what the flight's guard did.
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("Descent checklist.", lines, FirstAttemptHeard, Window, []));
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("Descent checklist.", lines, SecondAttemptHeard, Window, []));

        // Told when the utterances began and what the window was open for, it keeps both.
        Assert.Null(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, FirstAttemptHeard, Window, [], Began(FirstAttemptBegan, IdleWindow)));
        Assert.Null(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, SecondAttemptHeard, Window, [], Began(SecondAttemptBegan, IdleWindow)));
    }

    [Fact]
    public void AShortTranscript_BegunAfterTheEchoTail_IsNotAnEcho_WhateverItsWords()
    {
        var lines = new[] { Line("Descent checklist cancelled.", CancelledSpokenAt) };

        // The second attempt, 6.6 s after the playback ended: kept on timing alone (no command list).
        Assert.Null(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, SecondAttemptHeard, Window, [], Began(SecondAttemptBegan)));
        // The first, 0.79 s after: inside the tail — only its being a command of the window saves it.
        Assert.NotNull(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, FirstAttemptHeard, Window, [], Began(FirstAttemptBegan)));
        Assert.NotNull(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, FirstAttemptHeard, Window, [], Began(FirstAttemptBegan, "gear down")));
        // The boundary: at the tail still a candidate, a millisecond later not.
        var tail = SelfEchoGuard.EchoTailFor(700);
        Assert.NotNull(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, SecondAttemptHeard, Window, [], Began(CancelledSpokenAt + tail)));
        Assert.Null(SelfEchoGuard.MatchOwnSpeech("Descent checklist.", lines, SecondAttemptHeard, Window, [], Began(CancelledSpokenAt + tail + TimeSpan.FromMilliseconds(1))));
    }

    [Fact]
    public void ACommandPhrase_ThatOverlapsThePlayback_IsStillAnEcho()
    {
        // Begun 300 ms before the line finished playing: the FO's own "Descent checklist …" in the mic.
        var lines = new[] { Line("Descent checklist cancelled.", CancelledSpokenAt) };
        var matched = SelfEchoGuard.MatchOwnSpeech(
            "Descent checklist.", lines, FirstAttemptHeard, Window, [], Began(CancelledSpokenAt.AddMilliseconds(-300), IdleWindow));
        Assert.Same(lines[0], matched);

        // A line still playing at transcript time has no playback end to compare with: a candidate as before.
        var playing = new[] { new SelfEchoGuard.SpokenLine("Descent checklist cancelled.", CancelledSpokenAt, PlayedAloud: true, StillPlaying: true) };
        Assert.NotNull(SelfEchoGuard.MatchOwnSpeech(
            "Descent checklist.", playing, FirstAttemptHeard, Window, [], Began(SecondAttemptBegan, IdleWindow)));
    }

    [Theory]
    // The 2026-10-10 loop with the utterance's start known: the long ones are matched on word
    // overlap anywhere in the window; the short one trails its line at once.
    [InlineData("See you at the next line-up.\n Didn't catch that.", "You too. See you at the next line up.", 5000)]
    [InlineData("I'll bring the coffee. Stand by.", "Sounds like we're planning a date. I'll bring the coffee.", 5000)]
    [InlineData("Yes, run the departure briefing", "Yes, run the departure briefing.", 5000)]
    [InlineData("I don't have that.", "I don't have that.", 700)]
    [InlineData("gear is down", "Yes captain, gear is down.", 700)]
    public void TheRealEchoes_AreStillDropped_WithTheUtteranceStartKnown(string heard, string spoken, int beganAfterMs)
    {
        var lines = new[] { Line(spoken, T0) };
        Assert.NotNull(SelfEchoGuard.MatchOwnSpeech(
            heard, lines, T0.AddMilliseconds(beganAfterMs + 2000), Window, [], Began(T0.AddMilliseconds(beganAfterMs), IdleWindow)));
    }

    [Fact]
    public void AnExpectedReplyWithExtraWords_IsStillExempt()
    {
        // "flaps one please" against the item's "flaps one": the expected phrase sits inside the transcript.
        var lines = new[] { Line("Set flaps one please, captain.", T0) };
        Assert.True(SelfEchoGuard.LooksLikeOwnSpeech("flaps one please", lines, T0.AddSeconds(2), Window, []));
        Assert.False(SelfEchoGuard.LooksLikeOwnSpeech("flaps one please", lines, T0.AddSeconds(2), Window, ["flaps one"]));
    }
}
