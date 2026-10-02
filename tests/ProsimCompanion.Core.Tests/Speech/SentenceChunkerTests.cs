using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class SentenceChunkerTests
{
    /// <summary>Feeds the text in pieces of <paramref name="size"/> characters and returns
    /// every sentence, the flushed tail included.</summary>
    private static List<string> Chunk(string text, int size = 1)
    {
        var chunker = new SentenceChunker();
        var sentences = new List<string>();
        for (var i = 0; i < text.Length; i += size)
        {
            sentences.AddRange(chunker.Push(text.Substring(i, Math.Min(size, text.Length - i))));
        }

        if (chunker.Flush() is { } tail)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(1000)]
    public void CutsAtSentenceEnds_HoweverTheTextArrives(int size)
    {
        var sentences = Chunk("Departing Sydney, runway one six right. Wind two five zero at one four knots! Any questions? Good.", size);

        Assert.Equal(
            ["Departing Sydney, runway one six right.", "Wind two five zero at one four knots!", "Any questions?", "Good."],
            sentences);
    }

    [Fact]
    public void ASentenceIsNotReleased_UntilWhatFollowsItHasArrived()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Push("QNH one zero one three."));       // could still be "…three.5"
        Assert.Empty(chunker.Push(" "));                            // the next word decides
        Assert.Equal(["QNH one zero one three."], chunker.Push("Minimums"));
        Assert.Equal("Minimums", chunker.Flush());
    }

    [Fact]
    public void NeverCutsInsideADecimalOrAFrequency()
    {
        var sentences = Chunk("I L S frequency 110.30, glideslope 3.0 degrees. QNH 1013.2 hectopascals.");

        Assert.Equal(
            ["I L S frequency 110.30, glideslope 3.0 degrees.", "QNH 1013.2 hectopascals."],
            sentences);
    }

    [Fact]
    public void NeverCutsAfterASingleLetter_SoRunwayDesignatorsAndSpelledTermsStayWhole()
    {
        var sentences = Chunk("We land on runway 16 L. The approach is the I. L. S. Yankee. Go.");

        // "16 L." and "I. L. S." look like ends and are not; the pieces join what follows.
        Assert.Equal(["We land on runway 16 L. The approach is the I. L. S. Yankee.", "Go."], sentences);
    }

    [Fact]
    public void NeverCutsAfterACommonAbbreviation()
    {
        var sentences = Chunk("Runway length approx. 3900 metres, i.e. plenty. Elevation 21 ft. Above sea level, no. 2 is longer.");

        Assert.Equal(
            ["Runway length approx. 3900 metres, i.e. plenty.", "Elevation 21 ft. Above sea level, no. 2 is longer."],
            sentences);
    }

    [Fact]
    public void ALowerCaseContinuation_IsNotANewSentence()
        => Assert.Equal(["Expect the V.O.R. approach. then vectors."], Chunk("Expect the V.O.R. approach. then vectors."));

    [Fact]
    public void ADigitBeforeThePoint_IsASentenceEnd_WhenANewSentenceFollows()
        => Assert.Equal(["Runway 16.", "Wind 250 at 14."], Chunk("Runway 16. Wind 250 at 14."));

    [Fact]
    public void AFragment_JoinsTheNextSentence()
    {
        // A list number or a one-word interjection is not worth a synthesis call of its own.
        Assert.Equal(["1. Departure from Sydney.", "2. Runway 16 right."], Chunk("1. Departure from Sydney. 2. Runway 16 right."));
        Assert.Equal(["Right. Departure briefing for Sydney."], Chunk("Right. Departure briefing for Sydney."));
    }

    [Fact]
    public void ALineBreak_EndsASentenceWithoutATerminator_AndBlankLinesAreDropped()
    {
        var sentences = Chunk("Departure briefing\n\n- Runway one six right\n- Wind calm\n");

        Assert.Equal(["Departure briefing", "- Runway one six right", "- Wind calm"], sentences);
    }

    [Fact]
    public void RunsOfTerminatorsAndClosingQuotes_StayWithTheirSentence()
        => Assert.Equal(
            ["Hold on now...", "He said \"go around.\"", "Is that so?!", "Yes sir."],
            Chunk("Hold on now... He said \"go around.\" Is that so?! Yes sir."));

    [Fact]
    public void Flush_ReturnsTheTail_AndNothingWhenEmpty()
    {
        var chunker = new SentenceChunker();
        chunker.Push("Minimums not briefed");

        Assert.Equal("Minimums not briefed", chunker.Flush());
        Assert.Null(chunker.Flush());
        Assert.Null(new SentenceChunker().Flush());
        Assert.Empty(new SentenceChunker().Push(null));
        Assert.Empty(new SentenceChunker().Push(""));
    }

    [Fact]
    public void WhitespaceIsCollapsed()
        => Assert.Equal(["Wind calm.", "QNH 1013."], Chunk("  Wind   calm.\t QNH\n1013.  "));

    [Fact]
    public void ARunOnWithNoEnd_IsCutAtAClauseBreak_BeforeItHoldsTheSpeechBack()
    {
        var clause = "the runway is long and dry and the wind is light, ";
        var text = string.Concat(Enumerable.Repeat(clause, 12));     // 612 characters, no terminator

        var sentences = Chunk(text, size: 9);

        Assert.True(sentences.Count >= 2);
        Assert.All(sentences, s => Assert.InRange(s.Length, 1, SentenceChunker.MaxLength));
        Assert.EndsWith(",", sentences[0], StringComparison.Ordinal);
        // Nothing is lost or duplicated.
        Assert.Equal(
            text.Replace(" ", "", StringComparison.Ordinal),
            string.Concat(sentences).Replace(" ", "", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunOnWithNoBreakAtAll_IsCutAtASpace()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 120));

        var sentences = Chunk(text, size: 50);

        Assert.All(sentences, s => Assert.InRange(s.Length, 1, SentenceChunker.MaxLength));
        Assert.Equal(120, sentences.Sum(s => s.Split(' ').Length));
    }
}
