using ProsimCompanion.Core.Localization;
using Xunit;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>The browser language header, best first (ADR-0014).</summary>
public sealed class AcceptLanguageTests
{
    [Fact]
    public void Parse_OrdersByQuality_ThenHeaderOrder_LowerCased()
    {
        var tags = AcceptLanguage.Parse("en;q=0.5, de-DE, de;q=0.9, JA;q=0.9, *;q=0.1");

        Assert.Equal(["de-de", "de", "ja", "en"], tags);
    }

    [Fact]
    public void Parse_DropsZeroQuality_Wildcards_AndDuplicates()
    {
        var tags = AcceptLanguage.Parse("fr;q=0, en, en;q=0.8, *");

        Assert.Equal(["en"], tags);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",;,")]
    public void Parse_EmptyOrJunk_GivesNoTags(string? header)
    {
        Assert.Empty(AcceptLanguage.Parse(header));
    }
}
