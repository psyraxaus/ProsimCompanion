using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>The saved tile order of the Flight Status page (issue #160): a hand-edited or
/// stale list must never lose a tile, and the drag / button moves must land where the pilot
/// put them.</summary>
public sealed class FlightStatusTilesTests
{
    [Fact]
    public void Normalize_NullOrEmpty_IsDefaultOrder()
    {
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Normalize(null));
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Normalize([]));
    }

    [Fact]
    public void Normalize_PartialList_AppendsMissingTilesInDefaultOrder()
    {
        var order = FlightStatusTiles.Normalize(["services", "hero"]);

        Assert.Equal(["services", "hero", "sequence", "sim", "app", "gsx"], order);
    }

    [Fact]
    public void Normalize_DropsUnknownIdsAndDuplicates_CaseInsensitive()
    {
        var order = FlightStatusTiles.Normalize(["GSX", "weather", "gsx", "Sim"]);

        Assert.Equal(["gsx", "sim", "hero", "sequence", "app", "services"], order);
    }

    [Fact]
    public void IsDefault_TrueForEmptyAndForTheDefaultSpelledOut()
    {
        Assert.True(FlightStatusTiles.IsDefault([]));
        Assert.True(FlightStatusTiles.IsDefault(["hero", "sequence", "sim", "app", "gsx", "services"]));
        Assert.False(FlightStatusTiles.IsDefault(["sequence", "hero"]));
    }

    [Fact]
    public void Swap_TradesTheTwoTiles_NothingElseMoves()
    {
        Assert.Equal(["hero", "gsx", "sim", "app", "sequence", "services"],
            FlightStatusTiles.Swap(FlightStatusTiles.DefaultOrder, "gsx", "sequence"));
        // The same drop the other way round gives the same result.
        Assert.Equal(["hero", "gsx", "sim", "app", "sequence", "services"],
            FlightStatusTiles.Swap(FlightStatusTiles.DefaultOrder, "sequence", "GSX"));
    }

    [Fact]
    public void Swap_OntoItselfOrUnknownId_ChangesNothing()
    {
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Swap(FlightStatusTiles.DefaultOrder, "sim", "sim"));
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Swap(FlightStatusTiles.DefaultOrder, "weather", "sim"));
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Swap(FlightStatusTiles.DefaultOrder, "sim", "weather"));
    }

    [Fact]
    public void Shift_SwapsWithTheNeighbour_AndStopsAtTheEnds()
    {
        Assert.Equal(["hero", "sim", "sequence", "app", "gsx", "services"],
            FlightStatusTiles.Shift(FlightStatusTiles.DefaultOrder, "sim", -1));
        Assert.Equal(["hero", "sequence", "app", "sim", "gsx", "services"],
            FlightStatusTiles.Shift(FlightStatusTiles.DefaultOrder, "sim", +1));
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Shift(FlightStatusTiles.DefaultOrder, "hero", -1));
        Assert.Equal(FlightStatusTiles.DefaultOrder, FlightStatusTiles.Shift(FlightStatusTiles.DefaultOrder, "services", +1));
    }
}
