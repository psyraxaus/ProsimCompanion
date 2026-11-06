using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>A crew wording's placeholder is filled, or its clause goes (owner request
/// 2026-11-07, plan V3): never "zero tonnes", never a bare placeholder aloud.</summary>
public sealed class SpokenTemplateTests
{
    [Theory]
    [InlineData("Cockpit, ground — fuelling in progress, {fuel} tonnes requested.", "{fuel}", "6.3", "Cockpit, ground — fuelling in progress, 6.3 tonnes requested.")]
    [InlineData("Cockpit, ground — fuelling in progress, {fuel} tonnes requested.", "{fuel}", null, "Cockpit, ground — fuelling in progress.")]
    [InlineData("Cockpit, ground — de-icing complete, {fluid}. Holdover time starts now.", "{fluid}", "Type four", "Cockpit, ground — de-icing complete, Type four. Holdover time starts now.")]
    [InlineData("Cockpit, ground — de-icing complete, {fluid}. Holdover time starts now.", "{fluid}", "", "Cockpit, ground — de-icing complete. Holdover time starts now.")]
    [InlineData("Flight deck, cabin crew. Boarding complete, {pax} on board, doors closed.", "{pax}", "148", "Flight deck, cabin crew. Boarding complete, 148 on board, doors closed.")]
    [InlineData("Flight deck, cabin crew. Boarding complete, {pax} on board, doors closed.", "{pax}", null, "Flight deck, cabin crew. Boarding complete, doors closed.")]
    [InlineData("{pax} on board.", "{pax}", null, "")]
    [InlineData("No placeholder.", "{pax}", null, "No placeholder.")]
    public void FillOrDrop(string template, string placeholder, string? value, string expected)
        => Assert.Equal(expected, SpokenTemplate.FillOrDrop(template, placeholder, value));
}
