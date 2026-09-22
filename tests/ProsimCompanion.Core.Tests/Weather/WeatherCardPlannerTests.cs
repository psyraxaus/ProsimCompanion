using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class WeatherCardPlannerTests
{
    private static readonly OfpData Ofp = new()
    {
        OriginIcao = "EGLL",
        OriginName = "LONDON HEATHROW",
        DestinationIcao = "LIRF",
        DestinationName = "ROME FIUMICINO",
        AlternateIcao = "LIRN",
        AlternateName = "NAPLES CAPODICHINO",
    };

    [Theory]
    [InlineData(FlightPhase.ColdAndDark)]
    [InlineData(FlightPhase.Preflight)]
    [InlineData(FlightPhase.Departure)]
    [InlineData(FlightPhase.PushbackAndStart)]
    [InlineData(FlightPhase.TaxiOut)]
    [InlineData(FlightPhase.TakeoffRoll)]
    [InlineData(FlightPhase.Climb)]
    [InlineData(FlightPhase.Cruise)]
    public void BeforeDescent_LocalIsOrigin_SecondIsDestination(FlightPhase phase)
    {
        var plan = WeatherCardPlanner.Plan(Ofp, phase);

        Assert.Equal("EGLL", plan.LocalIcao);
        Assert.Equal("London Heathrow", plan.LocalName);
        Assert.Equal(WeatherCardRole.Destination, plan.SecondRole);
        Assert.Equal("LIRF", plan.SecondIcao);
        Assert.Equal("Rome Fiumicino", plan.SecondName);
    }

    [Theory]
    [InlineData(FlightPhase.Descent)]
    [InlineData(FlightPhase.Approach)]
    [InlineData(FlightPhase.LandingRollout)]
    [InlineData(FlightPhase.TaxiIn)]
    [InlineData(FlightPhase.Shutdown)]
    public void FromDescent_LocalIsDestination_SecondIsAlternate(FlightPhase phase)
    {
        var plan = WeatherCardPlanner.Plan(Ofp, phase);

        Assert.Equal("LIRF", plan.LocalIcao);
        Assert.Equal(WeatherCardRole.Alternate, plan.SecondRole);
        Assert.Equal("LIRN", plan.SecondIcao);
        Assert.Equal("Naples Capodichino", plan.SecondName);
    }

    [Fact]
    public void FromDescent_WithoutAlternate_SecondStaysDestination()
    {
        var plan = WeatherCardPlanner.Plan(Ofp with { AlternateIcao = "", AlternateName = "" }, FlightPhase.Approach);

        Assert.Equal("LIRF", plan.LocalIcao);
        Assert.Equal(WeatherCardRole.Destination, plan.SecondRole);
        Assert.Equal("LIRF", plan.SecondIcao);
    }

    [Fact]
    public void NoOfp_IsEmptyPlan()
    {
        Assert.Equal(WeatherCardPlan.None, WeatherCardPlanner.Plan(null, FlightPhase.Preflight));
        Assert.Equal(WeatherCardPlan.None, WeatherCardPlanner.Plan(new OfpData(), FlightPhase.Preflight));
    }

    [Theory]
    [InlineData("LONDON HEATHROW", "London Heathrow")]
    [InlineData("ROME FIUMICINO", "Rome Fiumicino")]
    [InlineData("RIO DE JANEIRO GALEAO", "Rio de Janeiro Galeao")]
    [InlineData("FRANKFURT AM MAIN", "Frankfurt am Main")]
    [InlineData("Already Mixed", "Already Mixed")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void AirportName_TitleCase(string? raw, string expected)
        => Assert.Equal(expected, AirportNameFormat.TitleCase(raw));

    [Fact]
    public void WeatherCard_FromProbe_CarriesSkyAndStamp()
    {
        var probe = WxProbe.Found(MetarParser.ToFacts("EGLL 221020Z 25014KT 9999 FEW020 17/09 Q1012", atisLetter: "J"));

        var card = WeatherCard.From(WeatherCardRole.Local, "EGLL", "London Heathrow", probe);

        Assert.Equal(SkyCondition.FewClouds, card.Sky);
        Assert.Equal("FEW CLOUDS", card.SkyLabel);
        Assert.Equal("10:20Z", card.ObservedAt);
        Assert.Equal("J", card.Facts.AtisLetter);
        Assert.Null(card.Failure);
    }

    [Fact]
    public void WeatherCard_FromEmptyProbe_CarriesFailureLine()
    {
        var card = WeatherCard.From(WeatherCardRole.Destination, "LIRF", "Rome Fiumicino", WxProbe.Unavailable("gateway 500"));

        Assert.Equal(SkyCondition.Unknown, card.Sky);
        Assert.Equal("Weather fetch failed (gateway 500)", card.Failure);
        Assert.Null(card.ObservedAt);
    }
}
