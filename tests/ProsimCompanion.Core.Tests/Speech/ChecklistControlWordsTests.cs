using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner flight 2026-10-25: "… 80 degrees Celsius, skip," lost the pilot's "skip"
/// because a control word had to be the whole utterance.</summary>
public sealed class ChecklistControlWordsTests
{
    [Theory]
    [InlineData("at the temperature of the gap 80 degrees celsius skip", "skip")]
    [InlineData("skip that one please", "skip")]
    [InlineData("skip item we will do it later", "skip")]
    [InlineData("okay say again", "say again")]
    [InlineData("hold the checklist for a moment", "hold the checklist")]
    [InlineData("right cancel the checklist", "cancel checklist")]
    [InlineData("cancel checklist now", "cancel checklist")]
    [InlineData("resume checklist thanks", "resume checklist")]
    public void AtEdge_FindsTheWordAtTheStartOrEnd(string normalized, string expected)
        => Assert.Equal(expected, ChecklistControlWords.AtEdge(normalized));

    [Theory]
    [InlineData("skip")]
    [InlineData("say again")]
    [InlineData("we might skip the next one")]
    [InlineData("and repeat go to the next one")]
    [InlineData("please continue")]
    [InlineData("flaps three")]
    [InlineData("")]
    public void AtEdge_IgnoresExactWords_MiddleWords_AndEverydayPaWords(string normalized)
        => Assert.Null(ChecklistControlWords.AtEdge(normalized));
}
