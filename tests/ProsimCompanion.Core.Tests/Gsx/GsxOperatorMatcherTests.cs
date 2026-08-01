using ProsimCompanion.Gsx.Menu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxOperatorMatcherTests
{
    private static readonly string[] Entries =
    [
        "[GSX choice] Menzies Aviation",
        "Swissport",
        "Dnata Ground Handling",
    ];

    [Fact]
    public void FirstPreferenceInOrder_Wins()
        => Assert.Equal(2, GsxOperatorMatcher.PickOperator(Entries, ["Dnata", "Swissport"]));

    [Fact]
    public void PreferenceMatch_IsCaseInsensitiveSubstring()
        => Assert.Equal(1, GsxOperatorMatcher.PickOperator(Entries, ["swissport"]));

    [Fact]
    public void NoPreferenceMatch_FallsBackToGsxChoiceToken()
        => Assert.Equal(0, GsxOperatorMatcher.PickOperator(Entries, ["Aviapartner"]));

    [Fact]
    public void NoMatchAndNoGsxChoice_ReturnsNull()
        => Assert.Null(GsxOperatorMatcher.PickOperator(["Swissport", "Dnata"], ["Aviapartner"]));

    [Fact]
    public void EmptyPreferences_StillUseGsxChoiceFallback()
        => Assert.Equal(0, GsxOperatorMatcher.PickOperator(Entries, []));

    [Fact]
    public void BlankPreferencesAreIgnored()
        => Assert.Equal(0, GsxOperatorMatcher.PickOperator(Entries, ["", "  "]));
}
