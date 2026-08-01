using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxGateResolverTests
{
    [Theory]
    [InlineData("Gate B12", "GATEB12")]
    [InlineData("b-12", "B12")]
    [InlineData("  117L ", "117L")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsNonAlphanumericsAndUppercases(string? input, string expected)
        => Assert.Equal(expected, GsxGateResolver.Normalize(input));

    [Fact]
    public void PickUniqueCandidate_ExactUiNameMatch_Wins()
    {
        var candidates = new[]
        {
            new GsxGateRef("B12", "Gate B12", 12, "BGL_B12"),
            new GsxGateRef("B12A", "Gate B12A", 12, "BGL_B12A"),
        };

        var picked = GsxGateResolver.PickUniqueCandidate(candidates, "B12");

        Assert.Equal("BGL_B12", picked?.ResendToken);
    }

    [Fact]
    public void PickUniqueCandidate_UniqueSuffixMatch_Wins()
    {
        var candidates = new[]
        {
            new GsxGateRef(null, "Terminal 1 Gate 34A", 34, "T1_34A"),
            new GsxGateRef(null, "Terminal 1 Gate 35", 35, "T1_35"),
        };

        var picked = GsxGateResolver.PickUniqueCandidate(candidates, "34A");

        Assert.Equal("T1_34A", picked?.ResendToken);
    }

    [Fact]
    public void PickUniqueCandidate_AmbiguousSuffix_ReturnsNull()
    {
        var candidates = new[]
        {
            new GsxGateRef(null, "West 34A", 34, "W34A"),
            new GsxGateRef(null, "East 34A", 34, "E34A"),
        };

        Assert.Null(GsxGateResolver.PickUniqueCandidate(candidates, "34A"));
    }

    [Fact]
    public void PickUniqueCandidate_ResendTokenPrefersBglName()
    {
        var candidate = new GsxGateRef("UI", "G", 1, "BGL");
        Assert.Equal("BGL", candidate.ResendToken);
        Assert.Equal("UI", (candidate with { BglName = null }).ResendToken);
        Assert.Equal("G", (candidate with { BglName = null, UiName = null }).ResendToken);
    }

    [Theory]
    [InlineData(0, 12, 0, null)]            // NONE
    [InlineData(14, 12, -1, null)]          // suffix sentinel: unassigned
    [InlineData(10, 7, 0, "Gate 7")]        // GATE, no letter
    [InlineData(12, 12, 0, "A12")]          // 12 = A
    [InlineData(37, 3, 0, "Z3")]            // 37 = Z
    [InlineData(38, 3, 0, null)]            // out of letter map
    [InlineData(11, 3, 0, null)]            // undefined value
    public void FormatReadback_AppliesLetterMap(int name, int number, int suffix, string? expected)
        => Assert.Equal(expected, GsxGateResolver.FormatReadback(name, number, suffix));

    [Fact]
    public void NearestNames_RanksExactThenSuffixThenContains_MaxThree()
    {
        var parkings = new List<GsxParking>
        {
            new("Stand 112", null, null, null, null, null),
            new(null, "12", null, 12, null, null),
            new("B12", null, null, null, null, null),
            new("Gate 12B", null, null, null, null, null),
            new("112", null, null, null, null, null),
        };

        var nearest = GsxGateResolver.NearestNames(parkings, "12");

        Assert.Equal(3, nearest.Count);
        Assert.Equal("12", nearest[0]);                       // exact
        Assert.Contains(nearest.Skip(1), n => n is "B12" or "112" or "Stand 112"); // suffix matches next
    }

    [Fact]
    public void NearestNames_NoMatches_ReturnsEmpty()
    {
        var parkings = new List<GsxParking> { new("A1", null, null, 1, null, null) };

        Assert.Empty(GsxGateResolver.NearestNames(parkings, "Z99"));
    }
}
