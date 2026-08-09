using ProsimCompanion.Gsx.Menu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Issue #41: the EGLL Taxiway C menu offered both "Straight pushback (…)" and "Straight Pull
/// pushback (…)" plus compass-phrased "Nose Left/Right" direction lines — the port must match
/// the predecessor's behaviour on exactly that shape.
/// </summary>
public sealed class PushbackDirectionResolverTests
{
    private static readonly string[] EgllTaxiwayCMenu =
    [
        "Nose Right - Facing North on Taxiway C",
        "Nose Left - Facing South on Taxiway C",
        "QuickEdit Pushback",
        "QuickEdit Pushback on Map",
        "Straight pushback (manual stop, max 100 m)",
        "Straight Pull pushback (manual stop, max 100 m)",
    ];

    [Fact]
    public void Straight_PicksStraightPushback_NeverStraightPull()
    {
        var pick = PushbackDirectionResolver.Resolve(EgllTaxiwayCMenu, "straight");

        Assert.NotNull(pick);
        Assert.Equal(4, pick.Index);
        Assert.Equal("starts-with", pick.Strategy);
    }

    [Fact]
    public void TailLeft_FallsBackToFirstDirectionLine()
    {
        // Tail left = the stand's nose-right line (index 0).
        var pick = PushbackDirectionResolver.Resolve(EgllTaxiwayCMenu, "tailLeft");

        Assert.NotNull(pick);
        Assert.Equal(0, pick.Index);
        Assert.Equal("fixed-index", pick.Strategy);
    }

    [Fact]
    public void TailRight_FallsBackToSecondDirectionLine()
    {
        var pick = PushbackDirectionResolver.Resolve(EgllTaxiwayCMenu, "tailRight");

        Assert.NotNull(pick);
        Assert.Equal(1, pick.Index);
    }

    [Fact]
    public void Tail_TextMatch_WinsOverFixedIndex()
    {
        var entries = new[] { "Tail Right pushback", "Tail Left pushback", "Straight pushback" };

        var pick = PushbackDirectionResolver.Resolve(entries, "tailLeft");

        Assert.NotNull(pick);
        Assert.Equal(1, pick.Index);
        Assert.Equal("starts-with", pick.Strategy);
    }

    [Fact]
    public void Tail_FixedIndexRefusesWhenTopLinesAreMeta()
    {
        // A menu whose first lines are meta is not shaped like a direction menu — leave it.
        var entries = new[] { "QuickEdit Pushback", "Straight pushback", "Something else" };

        Assert.Null(PushbackDirectionResolver.Resolve(entries, "tailLeft"));
    }

    [Fact]
    public void Straight_NoMatch_ReturnsNull()
    {
        var entries = new[] { "Nose Right - North", "Nose Left - South" };

        Assert.Null(PushbackDirectionResolver.Resolve(entries, "straight"));
    }
}
