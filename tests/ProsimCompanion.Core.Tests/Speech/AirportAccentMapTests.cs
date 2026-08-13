using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class AirportAccentMapTests
{
    [Theory]
    [InlineData("EGLL", "en-GB")]
    [InlineData("YSSY", "en-AU")]
    [InlineData("KJFK", "en-US")]
    [InlineData("LFPG", "fr-FR")]
    [InlineData("EDDF", "de-DE")]
    [InlineData("LIRF", "it-IT")]
    [InlineData("VIDP", "en-IN")]
    [InlineData("RJTT", "ja-JP")]
    [InlineData("SBGR", "pt-BR")]
    [InlineData("NZAA", "en-AU")]
    public void KnownAirports_ResolveTheirLocale(string icao, string expected)
        => Assert.Equal(expected, AirportAccentMap.LocaleFor(icao));

    [Fact]
    public void Ukraine_TwoLetterPrefix_BeatsTheRussiaContinent()
    {
        // UKBB is Kyiv; U* alone is Russia/CIS — longest prefix must win.
        Assert.Equal("uk-UA", AirportAccentMap.LocaleFor("UKBB"));
        Assert.Equal("ru-RU", AirportAccentMap.LocaleFor("UUEE"));
    }

    [Fact]
    public void Brazil_TwoLetterPrefix_BeatsTheSouthAmericaContinent()
    {
        Assert.Equal("pt-BR", AirportAccentMap.LocaleFor("SBGL"));
        Assert.Equal("es-US", AirportAccentMap.LocaleFor("SAEZ"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("HKJK")] // Kenya — deliberately unmapped
    public void UnmappedOrInvalid_ReturnsNull(string? icao)
        => Assert.Null(AirportAccentMap.LocaleFor(icao));

    [Fact]
    public void Overrides_WinOverTheBuiltInTable_LongestPrefixFirst()
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LSG"] = "fr-FR", // Geneva region — French-speaking Switzerland
        };

        Assert.Equal("fr-FR", AirportAccentMap.LocaleFor("LSGG", overrides));
        Assert.Equal("de-DE", AirportAccentMap.LocaleFor("LSZH", overrides));
    }

    [Fact]
    public void GroundCrewRole_ResolvesItsConfiguredVoiceAndIntercomFilter()
    {
        var voices = new VoicesOptions { Ground = "am_michael", GroundIntercomFilter = true };

        var resolved = RoleVoiceResolver.Resolve(SpeechRole.GroundCrew, voices);

        Assert.Equal("am_michael", resolved.VoiceOverride);
        Assert.True(resolved.IntercomOverride);
        Assert.False(resolved.FellBackToFoVoice);
    }
}
