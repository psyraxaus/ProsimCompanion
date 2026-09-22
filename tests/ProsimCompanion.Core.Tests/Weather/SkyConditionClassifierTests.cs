using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class SkyConditionClassifierTests
{
    private static (SkyCondition, string) Classify(string metar)
        => SkyConditionClassifier.Classify(MetarParser.ToFacts(metar));

    [Theory]
    [InlineData("EGLL 221020Z 25014KT 9999 FEW020 17/09 Q1012", SkyCondition.FewClouds, "FEW CLOUDS")]
    [InlineData("EGLL 221020Z 25014KT 9999 SCT025 17/09 Q1012", SkyCondition.FewClouds, "SCATTERED CLOUD")]
    [InlineData("LIRF 221020Z 20008KT 8000 BKN030 24/18 Q1009", SkyCondition.Overcast, "BROKEN CLOUD")]
    [InlineData("EHAM 221025Z 24012KT 6000 OVC008 12/11 Q1005", SkyCondition.Overcast, "OVERCAST")]
    [InlineData("YSSY 221000Z 27012KT CAVOK 22/10 Q1013", SkyCondition.Clear, "CLEAR")]
    [InlineData("KDEN 221053Z 27010KT 10SM SKC 20/05 A3010", SkyCondition.Clear, "CLEAR")]
    [InlineData("LIRF 221020Z 20008KT 8000 -RA BKN030 24/18 Q1009", SkyCondition.Rain, "LIGHT RAIN")]
    [InlineData("LIRF 221020Z 20008KT 3000 +SHRA BKN012 22/20 Q1004", SkyCondition.Rain, "HEAVY RAIN")]
    [InlineData("ESSA 221020Z 02008KT 2000 FZRA OVC005 M01/M02 Q1001", SkyCondition.Rain, "FREEZING RAIN")]
    [InlineData("EDDF 221020Z 24004KT 4000 -DZ BKN006 08/07 Q1021", SkyCondition.Drizzle, "LIGHT DRIZZLE")]
    [InlineData("ENGM 221020Z 36010KT 1500 SN OVC010 M03/M05 Q0998", SkyCondition.Snow, "SNOW")]
    [InlineData("KBOS 221054Z 04012KT 1/4SM FG VV002 08/07 A2990", SkyCondition.Fog, "FOG")]
    [InlineData("EDDF 221020Z 24004KT 3000 BR SCT004 08/07 Q1021", SkyCondition.Fog, "MIST")]
    [InlineData("OMDB 221000Z 12008KT 4000 HZ NSC 38/14 Q0998", SkyCondition.Fog, "HAZE")]
    [InlineData("KMIA 221053Z 14010KT 5SM TSRA BKN020CB 27/24 A2995", SkyCondition.Thunderstorm, "THUNDERSTORM")]
    [InlineData("EGLL 221020Z 25022G34KT 9999 SCT030 17/09 Q1012", SkyCondition.Windy, "GUSTY WIND")]
    [InlineData("EGLL 221020Z 25028KT 9999 FEW030 17/09 Q1012", SkyCondition.Windy, "STRONG WIND")]
    public void Classify_PicksIconAndLabel(string metar, SkyCondition expected, string label)
    {
        var (sky, text) = Classify(metar);
        Assert.Equal(expected, sky);
        Assert.Equal(label, text);
    }

    [Fact]
    public void Classify_PrecipitationBeatsWindAndCloud()
    {
        // Gusts and an overcast deck, but it is the rain the pilot cares about first.
        var (sky, _) = Classify("EGLL 221020Z 25022G34KT 4000 RA OVC008 12/11 Q1001");
        Assert.Equal(SkyCondition.Rain, sky);
    }

    [Fact]
    public void Classify_IgnoresRemarks()
    {
        // RAB24 in the remarks must not read as rain.
        var (sky, _) = Classify("KJFK 221051Z 31015KT 10SM FEW250 12/01 A3005 RMK AO2 RAB24E40 SLP178");
        Assert.Equal(SkyCondition.FewClouds, sky);
    }

    [Fact]
    public void Classify_NoObservation_IsUnknown()
    {
        Assert.Equal((SkyCondition.Unknown, "NO DATA"), SkyConditionClassifier.Classify(null));
        Assert.Equal((SkyCondition.Unknown, "NO DATA"), SkyConditionClassifier.Classify(WxFacts.None));
    }

    [Theory]
    [InlineData("EGLL 221020Z 25014KT 9999 FEW020 17/09 Q1012", "10:20Z")]
    [InlineData("KJFK 220051Z 31015KT 10SM FEW250 12/01 A3005", "00:51Z")]
    [InlineData("EGLL 25014KT 9999 FEW020 17/09 Q1012", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ObservationTime_FromDayHourMinuteGroup(string? metar, string? expected)
        => Assert.Equal(expected, SkyConditionClassifier.ObservationTime(metar));
}
