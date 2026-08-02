using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class SeatMapTests
{
    [Fact]
    public void Parse_RoundTripsWithBuild()
    {
        const string seatString = "true,false,true,true,false";

        var map = SeatMap.Parse(seatString);

        Assert.Equal([true, false, true, true, false], map);
        Assert.Equal(seatString, SeatMap.Build(map));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyInput_ReturnsEmptyMap(string? input)
        => Assert.Empty(SeatMap.Parse(input));

    [Fact]
    public void FillBoarded_SeatsPlannedSeatsInOrder()
    {
        bool[] planned = [true, false, true, true, false, true];
        var boarded = new bool[6];

        var seated = SeatMap.FillBoarded(planned, boarded, 2);

        Assert.Equal(2, seated);
        Assert.Equal([true, false, true, false, false, false], boarded);
    }

    [Fact]
    public void FillBoarded_IsProgressive_NeverUnseats()
    {
        bool[] planned = [true, true, true, true];
        var boarded = new bool[4];

        Assert.Equal(2, SeatMap.FillBoarded(planned, boarded, 2));
        Assert.Equal(1, SeatMap.FillBoarded(planned, boarded, 3));
        Assert.Equal(0, SeatMap.FillBoarded(planned, boarded, 3));   // same count — no change
        Assert.Equal(0, SeatMap.FillBoarded(planned, boarded, 1));   // lower count — never unseat
        Assert.Equal([true, true, true, false], boarded);
    }

    [Fact]
    public void FillBoarded_ClampsToPlannedCount()
    {
        bool[] planned = [true, false, true, false];
        var boarded = new bool[4];

        var seated = SeatMap.FillBoarded(planned, boarded, 99);

        Assert.Equal(2, seated);
        Assert.Equal([true, false, true, false], boarded);
    }

    [Fact]
    public void FillBoarded_NeverSeatsUnplannedSeats()
    {
        bool[] planned = [false, true, false];
        var boarded = new bool[3];

        _ = SeatMap.FillBoarded(planned, boarded, 3);

        Assert.Equal([false, true, false], boarded);
    }

    [Fact]
    public void SynthesizeBooked_KeepsCapacityProportionalZoneCounts()
    {
        int[] capacities = [24, 30, 36, 42];

        var map = SeatMap.SynthesizeBooked(99, capacities, new Random(42));

        Assert.Equal(132, map.Length);
        Assert.Equal(99, map.Count(seat => seat));
        // Zone counts must exactly match the largest-remainder distribution (CG realism).
        var expected = LoadMath.DistributePax(99, capacities);
        var offset = 0;
        for (var zone = 0; zone < capacities.Length; zone++)
        {
            Assert.Equal(expected[zone], map.Skip(offset).Take(capacities[zone]).Count(seat => seat));
            offset += capacities[zone];
        }
    }

    [Fact]
    public void SynthesizeBooked_ScattersSeatsWithinEachZone()
    {
        int[] capacities = [24, 30, 36, 42];

        var map = SeatMap.SynthesizeBooked(99, capacities, new Random(42));

        // Owner requirement (round 4): occupancy must not be the first N seats of each zone,
        // leaving every zone's back rows empty. With 99/132 booked, at least one zone must
        // have an empty seat before its last occupied seat.
        var frontFilled = true;
        var offset = 0;
        var expected = LoadMath.DistributePax(99, capacities);
        for (var zone = 0; zone < capacities.Length; zone++)
        {
            for (var i = 0; i < expected[zone]; i++)
            {
                frontFilled &= map[offset + i];
            }
            offset += capacities[zone];
        }
        Assert.False(frontFilled);
    }

    [Fact]
    public void SynthesizeBooked_IsDeterministicForASeed()
    {
        int[] capacities = [24, 30, 36, 42];

        var first = SeatMap.SynthesizeBooked(99, capacities, new Random(7));
        var second = SeatMap.SynthesizeBooked(99, capacities, new Random(7));

        Assert.Equal(first, second);
    }

    [Fact]
    public void SynthesizeBooked_FullLoad_FillsEverySeat()
    {
        var map = SeatMap.SynthesizeBooked(10, [4, 6], new Random(1));

        Assert.All(map, Assert.True);
    }

    [Fact]
    public void DrainBoarded_UnseatsFromTheFront()
    {
        bool[] boarded = [true, false, true, true, true];

        var unseated = SeatMap.DrainBoarded(boarded, 2);

        Assert.Equal(2, unseated);
        // Front-most occupied seats empty first (front rows deboard first).
        Assert.Equal([false, false, false, true, true], boarded);
    }

    [Fact]
    public void DrainBoarded_TargetAtOrAboveCurrent_NoChange()
    {
        bool[] boarded = [true, true, false];

        Assert.Equal(0, SeatMap.DrainBoarded(boarded, 2));
        Assert.Equal(0, SeatMap.DrainBoarded(boarded, 5));
        Assert.Equal([true, true, false], boarded);
    }

    [Fact]
    public void DrainBoarded_TargetZero_EmptiesEveryone()
    {
        bool[] boarded = [true, true, true];

        Assert.Equal(3, SeatMap.DrainBoarded(boarded, 0));
        Assert.All(boarded, seat => Assert.False(seat));
    }

    [Fact]
    public void ApplyNoShowRandomization_DeltaMatchesTheMap()
    {
        var map = SeatMap.SynthesizeBooked(99, [24, 30, 36, 42], new Random(3));
        var before = map.Count(seat => seat);

        var delta = SeatMap.ApplyNoShowRandomization(map, 0.05, new Random(9));

        Assert.Equal(before + delta, map.Count(seat => seat));
    }

    [Fact]
    public void ApplyNoShowRandomization_ZeroChance_NoChange()
    {
        bool[] map = [true, false, true];

        var delta = SeatMap.ApplyNoShowRandomization(map, 0, new Random(1));

        Assert.Equal(0, delta);
        Assert.Equal([true, false, true], map);
    }

    [Fact]
    public void CountPerZone_SlicesByCapacity()
    {
        //             |-- zone 1 --|  |---- zone 2 ----|
        bool[] map = [true, false, true, true, true, false];

        Assert.Equal([2, 2], SeatMap.CountPerZone(map, [3, 3]));
    }
}

