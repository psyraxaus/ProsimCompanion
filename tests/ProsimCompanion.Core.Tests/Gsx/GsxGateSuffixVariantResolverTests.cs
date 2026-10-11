using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The pure rules behind issue #156 (EFHK 2026-10-11): <c>gate.select {"gate":36}</c>
/// for W36 answered <c>ambiguous</c> with " Gate 36A" / " Gate 36B" (bglName "Gate W 36A" /
/// "Gate W 36B"); the four name rungs were then refused; the not_found diagnostics listed
/// nearest []; GSX anchored on "Gate 36B with Safedock©" and the FO called it a conflict.</summary>
public sealed class GsxGateSuffixVariantResolverTests
{
    private static readonly GsxGateRef ThirtySixA = new("Apron 1W (Gates W34-W48) | Gate 36A", " Gate 36A", 36, "Gate W 36A");
    private static readonly GsxGateRef ThirtySixB = new("Apron 1W (Gates W34-W48) | Gate 36B", " Gate 36B", 36, "Gate W 36B");

    [Fact]
    public void TheEfhkCandidates_AreSuffixVariantsOfW36()
    {
        var variants = GsxGateResolver.SuffixVariants([ThirtySixA, ThirtySixB], "W36");

        Assert.NotNull(variants);
        Assert.Equal(["36A", "36B"], variants.Select(GsxGateResolver.CandidateLabel));
    }

    [Fact]
    public void BareNumber_AcceptsTheVariantsToo()
        => Assert.NotNull(GsxGateResolver.SuffixVariants([ThirtySixA, ThirtySixB], "36"));

    [Fact]
    public void AnotherNumber_OrAnUnsuffixedCandidate_OrAnotherLetter_IsNotAVariantSet()
    {
        Assert.Null(GsxGateResolver.SuffixVariants([ThirtySixA, ThirtySixB], "W40"));
        Assert.Null(GsxGateResolver.SuffixVariants([ThirtySixA, new("Gate 36", " Gate 36", 36, "Gate W 36")], "W36"));
        Assert.Null(GsxGateResolver.SuffixVariants([ThirtySixA, new("Gate 36B", " Gate 36B", 36, "Gate S 36B")], "W36"));
        Assert.Null(GsxGateResolver.SuffixVariants([], "W36"));
        Assert.Null(GsxGateResolver.SuffixVariants([ThirtySixA], "Terminal 1"));
    }

    [Theory]
    [InlineData("W36B", "Gate W 36B")]
    [InlineData("36a", "Gate W 36A")]
    [InlineData("Gate W36A", "Gate W 36A")]
    [InlineData("W36", null)]
    [InlineData("W36C", null)]
    public void PickBySuffix_TakesThePilotsSuffix(string requested, string? expectedToken)
        => Assert.Equal(expectedToken, GsxGateResolver.PickBySuffix([ThirtySixA, ThirtySixB], requested)?.ResendToken);

    [Fact]
    public void CandidateParts_ReadTheBglName_ThenTheNumber()
    {
        var parts = GsxGateResolver.CandidateParts(ThirtySixB)!;
        Assert.Equal(36, parts.Number);
        Assert.Equal("B", parts.Suffix);
        Assert.Equal('W', parts.GateLetter);

        var numberOnly = GsxGateResolver.CandidateParts(new GsxGateRef(null, null, 12, null))!;
        Assert.Equal(12, numberOnly.Number);
        Assert.Equal("", numberOnly.Suffix);
    }

    [Theory]
    [InlineData("Apron 1W (Gates W34-W48)   Gate 36B with Safedock©", "W36", "Gate 36B")]
    [InlineData("Gate W 36B", "W36", "Gate 36B")]
    [InlineData("Stand 36A", "36", "Stand 36A")]
    [InlineData("Gate 36B", "36A", "Gate 36B")]
    [InlineData("Gate 36B", "W36B", null)]          // the same stand — FacilityNamesGate's business
    [InlineData("Gate 40", "W36", null)]            // another stand
    [InlineData("Apron 1W (Gates W34-W48)", "W36", null)] // the range, not a stand
    [InlineData("Gate S 36B", "W36", null)]         // another GATE letter
    [InlineData("Terminal 36B", "W36", null)]       // no facility word before it
    [InlineData("", "W36", null)]
    [InlineData("Gate 36B", null, null)]
    public void SuffixVariantIn_FindsTheSameNumberWithAnotherSuffix(string facility, string? requested, string? expected)
        => Assert.Equal(expected, GsxGateResolver.SuffixVariantIn(facility, requested));

    [Fact]
    public void NearestNames_SurfaceTheSuffixVariants()
    {
        var parkings = new List<GsxParking>
        {
            new(null, " Stand 203", null, 203, null, null),
            new(null, " Gate 36A", "Gate W 36A", 36, null, null),
            new(null, " Gate 36B", "Gate W 36B", 36, null, null),
            new(null, " Stand 136", null, 136, null, null),
        };

        var nearest = GsxGateResolver.NearestNames(parkings, "W36");

        Assert.Equal([" Gate 36A", " Gate 36B"], nearest);
    }
}
