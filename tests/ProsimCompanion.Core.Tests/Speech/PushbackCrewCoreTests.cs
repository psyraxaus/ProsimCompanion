using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Crew;
using Xunit;
using static ProsimCompanion.Speech.Crew.PushbackCrewCore;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The pushback crew's script (owner request 2026-11-07, plan V2): one line per stage,
/// each once; the brakes question asked once and answered once; the push clearance by voice OR
/// with the push, never both; the direction filled or dropped cleanly.</summary>
public sealed class PushbackCrewCoreTests
{
    private static readonly PushbackCrewOptions Defaults = new();

    [Fact]
    public void TheWholePush_InOrder_WithTheCaptainsBrakesAnswer()
    {
        var state = CrewState.Initial;
        CrewLine? line;

        (state, line) = OnStage(state, PushbackStage.BridgeRemoved, Defaults, "tail left");
        Assert.Equal(("bridge", true), (line!.Tag, line.OpensCall));

        (state, line) = OnStage(state, PushbackStage.TugConnected, Defaults, "tail left");
        Assert.Equal("Cockpit, ground — tug connected, bypass pin inserted. Release parking brake, please.", line!.Text);
        Assert.True(state.BrakesQuestionOpen);

        (state, line) = OnBrakesReleased(state, Defaults, "tail left");
        Assert.Equal("Brakes released, copied. Cleared to push, tail left. Clear to start engines.", line!.Text);
        Assert.Equal(("cleared", false), (line.Tag, line.OpensCall));
        Assert.False(state.BrakesQuestionOpen);

        // The push begins: the clearance and the engine start were both given — silence.
        (state, line) = OnStage(state, PushbackStage.Pushing, Defaults, "tail left");
        Assert.Null(line);

        (state, line) = OnStage(state, PushbackStage.PushComplete, Defaults, "tail left");
        Assert.Equal(Defaults.PushCompleteText, line!.Text);

        (state, line) = OnStage(state, PushbackStage.EngineStartConfirmed, Defaults, "tail left");
        Assert.Equal(Defaults.EngineStartConfirmedText, line!.Text);

        (state, line) = OnStage(state, PushbackStage.Disconnecting, Defaults, "tail left");
        Assert.Null(line); // nobody speaks on 13 — the pin-out line comes at Clear

        (state, line) = OnStage(state, PushbackStage.Clear, Defaults, "tail left");
        Assert.Equal(("clear", true), (line!.Tag, line.OpensCall));
    }

    [Fact]
    public void NoBrakesAnswer_TheClearanceComesWithThePush_WithTheEngineStart()
    {
        var (state, _) = OnStage(CrewState.Initial, PushbackStage.TugConnected, Defaults, "tail right");

        var (next, line) = OnStage(state, PushbackStage.Pushing, Defaults, "tail right");

        Assert.Equal("Pushing back, tail right. Clear to start engines.", line!.Text);
        Assert.False(next.BrakesQuestionOpen);
        Assert.Null(OnBrakesReleased(next, Defaults, "tail right").Line); // too late to be an answer
    }

    [Fact]
    public void EveryStageLine_ComesOnce()
    {
        var state = CrewState.Initial;
        foreach (var stage in new[] { PushbackStage.BridgeRemoved, PushbackStage.TugConnected, PushbackStage.Pushing, PushbackStage.PushComplete, PushbackStage.EngineStartConfirmed, PushbackStage.Clear })
        {
            CrewLine? first, second;
            (state, first) = OnStage(state, stage, Defaults, null);
            (state, second) = OnStage(state, stage, Defaults, null);
            Assert.NotNull(first);
            Assert.Null(second);
        }
    }

    [Fact]
    public void BrakesReleased_WithoutAQuestion_IsNobodys()
    {
        Assert.Null(OnBrakesReleased(CrewState.Initial, Defaults, null).Line);

        var noAsk = new PushbackCrewOptions { BrakesCheck = false };
        var (state, line) = OnStage(CrewState.Initial, PushbackStage.TugConnected, noAsk, null);
        Assert.Equal(noAsk.TugConnectedText, line!.Text);
        Assert.False(state.BrakesQuestionOpen);
    }

    [Fact]
    public void EngineStartClearanceOff_NeverSaysIt()
    {
        var options = new PushbackCrewOptions { EngineStartClearance = false };
        var (state, _) = OnStage(CrewState.Initial, PushbackStage.TugConnected, options, "straight back");

        var (_, line) = OnBrakesReleased(state, options, "straight back");

        Assert.Equal("Brakes released, copied. Cleared to push, straight back.", line!.Text);
    }

    [Theory]
    [InlineData("Pushing back, {direction}.", "tail left", "Pushing back, tail left.")]
    [InlineData("Pushing back, {direction}.", null, "Pushing back.")]
    [InlineData("Cleared to push {direction}.", "", "Cleared to push.")]
    [InlineData("Cleared to push, {direction}, have a good one.", null, "Cleared to push, have a good one.")]
    [InlineData("No placeholder here.", "tail left", "No placeholder here.")]
    public void Fill_UsesTheDirection_OrDropsItCleanly(string template, string? direction, string expected)
        => Assert.Equal(expected, Fill(template, direction));

    [Fact]
    public void ReadyAck_AndTheDrawerPhrases()
    {
        Assert.Equal(Defaults.ReadyAckText, OnReady(Defaults)!.Text);
        Assert.Contains("ready for pushback", ReadyPhrases);
        Assert.Contains("brakes released", BrakesReleasedPhrases);
        Assert.Contains("released", BrakesReleasedPhrases);
    }

    [Fact]
    public void ABlankText_SilencesThatLine()
    {
        var options = new PushbackCrewOptions { BridgeRemovedText = "  " };
        var (state, line) = OnStage(CrewState.Initial, PushbackStage.BridgeRemoved, options, null);
        Assert.Null(line);
        Assert.True(state.BridgeSpoken); // and it is not retried
    }
}
