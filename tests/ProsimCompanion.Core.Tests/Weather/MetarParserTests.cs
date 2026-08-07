using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class MetarParserTests
{
    // ---- visibility ----

    [Theory]
    [InlineData("KLAX 070753Z 25008KT P6SM FEW015 18/12 A2992", 10000)]      // "more than 6" → cap
    [InlineData("KBOS 070754Z 04012KT M1/4SM FG VV002 08/07 A2990", 300)]    // "less than 1/4"
    [InlineData("KJFK 070751Z 31015KT 2 1/2SM BR OVC008 12/11 A2988", 4023)] // mixed fraction: 2.5 mi
    [InlineData("KSEA 070753Z 18006KT 3/4SM RA BKN005 10/09 A2985", 1207)]   // bare fraction
    [InlineData("KDEN 070753Z 27010KT 10SM SKC 20/05 A3010", 10000)]         // ≥7 mi → cap
    [InlineData("KORD 070751Z 09008KT 3SM -RA OVC012 14/12 A2979", 4828)]    // whole miles
    [InlineData("YSSY 070800Z 27012KT 9999 FEW030 22/10 Q1013", 10000)]      // metric unlimited
    [InlineData("EDDF 070820Z 24010KT 3000 BR SCT004 08/07 Q1021", 3000)]    // metric literal
    public void Visibility_AllForms(string metar, int expectedMeters)
        => Assert.Equal(expectedMeters, MetarParser.Parse(metar).VisibilityMeters);

    [Fact]
    public void Visibility_Cavok_IsNull()
        => Assert.Null(MetarParser.Parse("YSSY 070800Z 27012KT CAVOK 22/10 Q1013").VisibilityMeters);

    // ---- ceiling ----

    [Fact]
    public void Ceiling_LowestOfBknOvcVv()
    {
        var v = MetarParser.Parse("KJFK 070751Z 31015KT 9999 FEW010 BKN030 OVC020 12/11 Q1013");
        Assert.Equal(2000, v.CeilingFt); // OVC020 beats BKN030; FEW010 is not a ceiling
    }

    [Fact]
    public void Ceiling_VerticalVisibilityCounts()
        => Assert.Equal(200, MetarParser.Parse("KBOS 070754Z 04012KT 0400 FG VV002 08/07 Q1010").CeilingFt);

    [Fact]
    public void Ceiling_FewSctOnly_IsNull()
        => Assert.Null(MetarParser.Parse("YSSY 070800Z 27012KT 9999 FEW030 SCT045 22/10 Q1013").CeilingFt);

    // ---- precipitation priority: TS > freezing > snow > rain > other frozen ----

    [Theory]
    [InlineData("KMCO 070753Z 18010KT 4SM +TSRA BKN015CB 26/23 A2995", PrecipKind.Thunderstorm)]
    [InlineData("KBUF 070753Z 06012KT 2SM FZRA SN OVC008 M01/M02 A2970", PrecipKind.Freezing)]
    [InlineData("ESSA 070750Z 36008KT 3000 -SHSN OVC012 M03/M05 Q1005", PrecipKind.Snow)]
    [InlineData("EGLL 070750Z 21010KT 8000 RA BR BKN010 12/11 Q0998", PrecipKind.Rain)]
    [InlineData("LFPG 070800Z 25015KT 9999 GR BKN025 15/09 Q1008", PrecipKind.Other)]
    [InlineData("EDDF 070820Z 24004KT 0800 BR FG OVC002 08/07 Q1021", PrecipKind.None)] // obscuration only
    public void Precip_PriorityAndObscuration(string metar, PrecipKind expected)
        => Assert.Equal(expected, MetarParser.Parse(metar).Precip);

    // ---- QNH ----

    [Fact]
    public void Qnh_QForm_IsHpaDirect()
        => Assert.Equal(1013, MetarParser.Parse("YSSY 070800Z 27012KT CAVOK 22/10 Q1013").QnhHpa);

    [Fact]
    public void Qnh_AForm_ConvertsInHgToHpa()
        // 29.92 inHg × 33.8639 = 1013.07 → 1013 (predecessor rounding rule).
        => Assert.Equal(1013, MetarParser.Parse("KLAX 070753Z 25008KT 10SM FEW015 18/12 A2992").QnhHpa);

    // ---- wind ----

    [Fact]
    public void Wind_VariableDirection_IsNullDir()
    {
        var v = MetarParser.Parse("YSSY 070800Z VRB03KT CAVOK 22/10 Q1013");
        Assert.Null(v.WindDirDeg);
        Assert.Equal(3, v.WindSpeedKt);
    }

    [Fact]
    public void Wind_Gust_IsParsed()
    {
        var v = MetarParser.Parse("KLAX 070753Z 25015G25KT 10SM FEW015 18/12 A2992");
        Assert.Equal(250, v.WindDirDeg);
        Assert.Equal(15, v.WindSpeedKt);
        Assert.Equal(25, v.WindGustKt);
    }

    // ---- temperature ----

    [Theory]
    [InlineData("YSSY 070800Z 27012KT CAVOK 22/10 Q1013", 22)]
    [InlineData("ESSA 070750Z 36008KT 9999 OVC012 M05/M12 Q1005", -5)]
    public void Temperature_IncludingNegative(string metar, int expected)
        => Assert.Equal(expected, MetarParser.Parse(metar).TemperatureC);

    // ---- RMK confinement ----

    [Fact]
    public void Remarks_NeverParsed()
    {
        // QNH, precip and gust tokens live only in the remarks — none may leak into the body parse.
        var v = MetarParser.Parse("YSSY 070800Z 27012KT CAVOK 22/10 RMK Q1020 TSRA 25015G25KT");
        Assert.Null(v.QnhHpa);
        Assert.Equal(PrecipKind.None, v.Precip);
        Assert.Null(v.WindGustKt);
        Assert.Equal(270, v.WindDirDeg);
    }

    // ---- never throws / ToFacts ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage ???")]
    public void Parse_DegenerateInput_ReturnsEmpty(string? metar)
        => Assert.Equal(MetarValues.Empty, MetarParser.Parse(metar));

    [Fact]
    public void ToFacts_CarriesRawAndDerivedFields()
    {
        var facts = MetarParser.ToFacts("  YSSY 070800Z 27012KT CAVOK 22/10 Q1013  ", "J", "16R");
        Assert.Equal("YSSY 070800Z 27012KT CAVOK 22/10 Q1013", facts.RawMetar);
        Assert.Equal(270, facts.WindDirDeg);
        Assert.Equal(1013, facts.QnhHpa);
        Assert.Equal("J", facts.AtisLetter);
        Assert.Equal("16R", facts.ActiveRunway);
    }

    [Fact]
    public void ToFacts_NoMetar_IsNone()
        => Assert.Equal(WxFacts.None, MetarParser.ToFacts(null));
}
