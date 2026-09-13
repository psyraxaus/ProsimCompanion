using ProsimCompanion.Speech.Commands;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>{fuelQuantity} rendering (issue #129): a quantity with its unit, rounded to the
/// nearest 10, following ProSim's configured weight unit — plain digits, not aviation digit
/// words, because a fuel figure is a number, not a callsign.</summary>
public sealed class FuelQuantityTokenTests
{
    [Theory]
    [InlineData(8_537.4, null, "8540 kilograms")]
    [InlineData(8_537.4, "KG", "8540 kilograms")]
    [InlineData(8_537.4, "", "8540 kilograms")]
    [InlineData(12_000.0, "kg", "12000 kilograms")]
    [InlineData(0.0, "KG", "0 kilograms")]
    public void Kilograms_RoundToNearestTen(double kg, string? unit, string expected)
        => Assert.Equal(expected, SpokenValueFormatting.FuelQuantity(kg, unit));

    [Theory]
    [InlineData(8_537.4, "LBS", "18820 pounds")]
    [InlineData(8_537.4, "lbs", "18820 pounds")]
    [InlineData(1_000.0, "LB", "2200 pounds")]
    public void Pounds_FollowProsimWeightUnit(double kg, string unit, string expected)
        => Assert.Equal(expected, SpokenValueFormatting.FuelQuantity(kg, unit));

    [Fact]
    public void MissingOrInvalidValues_ReadUnavailable()
    {
        Assert.Equal("unavailable", SpokenValueFormatting.FuelQuantity(null, "KG"));
        Assert.Equal("unavailable", SpokenValueFormatting.FuelQuantity(-5, "KG"));
        Assert.Equal("unavailable", SpokenValueFormatting.FuelQuantity(double.NaN, "KG"));
    }

    [Fact]
    public void ApplyTokens_ExpandsFuelQuantity()
    {
        var values = CommandTokenValues.Unavailable with { FuelQuantity = "8540 kilograms" };

        Assert.Equal(
            "Fuel quantity, 8540 kilograms, checked",
            SpokenValueFormatting.ApplyTokens("Fuel quantity, {fuelQuantity}, checked", values));
    }

    [Fact]
    public void ApplyTokens_IsCaseSensitive_LowercaseSpellingPassesThrough()
    {
        var values = CommandTokenValues.Unavailable with { FuelQuantity = "8540 kilograms" };

        Assert.Equal(
            "Fuel quantity, {fuelquantity}",
            SpokenValueFormatting.ApplyTokens("Fuel quantity, {fuelquantity}", values));
    }
}
