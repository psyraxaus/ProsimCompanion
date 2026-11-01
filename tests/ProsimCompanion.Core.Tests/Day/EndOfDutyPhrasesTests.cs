using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

/// <summary>The FO's "end of our duty day?" offer table (ADR-0019 §8, issue #217).</summary>
public sealed class EndOfDutyPhrasesTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("affirm", true)]
    [InlineData("that's us", true)]
    [InlineData("end duty day", true)]
    [InlineData("we're done for today", true)]
    [InlineData("no", false)]
    [InlineData("negative", false)]
    [InlineData("not yet", false)]
    [InlineData("one more sector", false)]
    [InlineData("keep it open", false)]
    [InlineData("say again", null)]
    [InlineData("what's the weather", null)]
    public void Interpret_ClassifiesYesNoAndNotAnAnswer(string heard, bool? expected)
        => Assert.Equal(expected, EndOfDutyPhrases.Interpret(heard));

    [Fact]
    public void Table_VoicesTheEndOfDutyStore_WithItsOwnEvent()
    {
        Assert.Equal(EndOfDutyOfferStore.QuestionDefinition.Topic, EndOfDutyPhrases.Table.Topic);
        Assert.Equal("day.end-offer", EndOfDutyPhrases.Table.EventName);
        Assert.Equal(1, EndOfDutyOfferStore.QuestionDefinition.MaxAsks); // once, no re-ask
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, EndOfDutyOfferStore.QuestionDefinition.Expiry);
    }
}
