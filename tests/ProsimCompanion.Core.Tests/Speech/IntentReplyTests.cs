using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The intent tier's pure half (owner request 2026-11-07): the model may only ever
/// name a phrase that is in the list — quotes, dashes and case forgiven, anything else is none.</summary>
public sealed class IntentReplyTests
{
    private static readonly string[] Vocabulary = ["request refueling", "start boarding", "gear up", "cross-check", NumberGrammar.Sentinel];

    [Theory]
    [InlineData("request refueling", "request refueling")]
    [InlineData("\"Request refueling\"", "request refueling")]
    [InlineData("- start boarding.", "start boarding")]
    [InlineData("Cross-check", "cross-check")]
    [InlineData("gear up\nbecause the captain asked", "gear up")]
    public void Pick_AcceptsOnlyAListedPhrase(string reply, string expected)
        => Assert.Equal(expected, IntentReply.Pick(reply, Vocabulary));

    [Theory]
    [InlineData("NONE")]
    [InlineData("none.")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("request fuel")]                 // close, but not in the list
    [InlineData("request refueling now please")]  // added words
    [InlineData("The captain wants the fuel truck")]
    public void Pick_RefusesEverythingElse(string? reply)
        => Assert.Null(IntentReply.Pick(reply, Vocabulary));

    [Fact]
    public void User_ListsEveryPhraseButTheNumberSentinel_AndTheHeardText()
    {
        var user = IntentReply.User("can we get the fuel truck over here", Vocabulary);
        Assert.Contains("- request refueling", user, StringComparison.Ordinal);
        Assert.Contains("- gear up", user, StringComparison.Ordinal);
        Assert.DoesNotContain(NumberGrammar.Sentinel, user, StringComparison.Ordinal);
        Assert.EndsWith("HEARD: can we get the fuel truck over here\n\nANSWER:", user, StringComparison.Ordinal);
        Assert.Contains("NONE", IntentReply.System, StringComparison.Ordinal);
    }
}
