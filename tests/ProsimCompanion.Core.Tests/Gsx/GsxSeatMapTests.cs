using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxSeatMapTests
{
    [Fact]
    public void Parse_RoundTripsWithBuild()
    {
        const string seatString = "true,false,true,true,false";

        var map = GsxSeatMap.Parse(seatString);

        Assert.Equal([true, false, true, true, false], map);
        Assert.Equal(seatString, GsxSeatMap.Build(map));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyInput_ReturnsEmptyMap(string? input)
        => Assert.Empty(GsxSeatMap.Parse(input));

    [Fact]
    public void FillBoarded_SeatsPlannedSeatsInOrder()
    {
        bool[] planned = [true, false, true, true, false, true];
        var boarded = new bool[6];

        var seated = GsxSeatMap.FillBoarded(planned, boarded, 2);

        Assert.Equal(2, seated);
        Assert.Equal([true, false, true, false, false, false], boarded);
    }

    [Fact]
    public void FillBoarded_IsProgressive_NeverUnseats()
    {
        bool[] planned = [true, true, true, true];
        var boarded = new bool[4];

        Assert.Equal(2, GsxSeatMap.FillBoarded(planned, boarded, 2));
        Assert.Equal(1, GsxSeatMap.FillBoarded(planned, boarded, 3));
        Assert.Equal(0, GsxSeatMap.FillBoarded(planned, boarded, 3));   // same count — no change
        Assert.Equal(0, GsxSeatMap.FillBoarded(planned, boarded, 1));   // lower count — never unseat
        Assert.Equal([true, true, true, false], boarded);
    }

    [Fact]
    public void FillBoarded_ClampsToPlannedCount()
    {
        bool[] planned = [true, false, true, false];
        var boarded = new bool[4];

        var seated = GsxSeatMap.FillBoarded(planned, boarded, 99);

        Assert.Equal(2, seated);
        Assert.Equal([true, false, true, false], boarded);
    }

    [Fact]
    public void FillBoarded_NeverSeatsUnplannedSeats()
    {
        bool[] planned = [false, true, false];
        var boarded = new bool[3];

        _ = GsxSeatMap.FillBoarded(planned, boarded, 3);

        Assert.Equal([false, true, false], boarded);
    }
}
