using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

public sealed class PassengerManifestTests
{
    private static readonly int[] Capacities = [24, 30, 36, 42];

    [Fact]
    public void Generate_OneEntryPerBookedSeat_WithCorrectZones()
    {
        var map = new bool[132];
        map[0] = true;    // seat 1 → zone 1
        map[23] = true;   // seat 24 → zone 1 (last)
        map[24] = true;   // seat 25 → zone 2 (first)
        map[89] = true;   // seat 90 → zone 3 (last)
        map[131] = true;  // seat 132 → zone 4 (last)

        var manifest = PassengerManifestService.Generate(map, Capacities);

        Assert.Equal(5, manifest.Count);
        Assert.Equal((1, 1), (manifest[0].SeatNumber, manifest[0].Zone));
        Assert.Equal((24, 1), (manifest[1].SeatNumber, manifest[1].Zone));
        Assert.Equal((25, 2), (manifest[2].SeatNumber, manifest[2].Zone));
        Assert.Equal((90, 3), (manifest[3].SeatNumber, manifest[3].Zone));
        Assert.Equal((132, 4), (manifest[4].SeatNumber, manifest[4].Zone));
        Assert.All(manifest, entry =>
        {
            Assert.NotEmpty(entry.FirstName);
            Assert.NotEmpty(entry.LastName);
        });
    }

    [Fact]
    public void Generate_SameMap_DealsTheSameNames()
    {
        var map = new bool[132];
        for (var i = 0; i < 99; i++)
        {
            map[i] = true;
        }

        var first = PassengerManifestService.Generate(map, Capacities);
        var second = PassengerManifestService.Generate(map, Capacities);

        Assert.Equal(
            first.Select(entry => (entry.SeatNumber, entry.FirstName, entry.LastName)),
            second.Select(entry => (entry.SeatNumber, entry.FirstName, entry.LastName)));
    }

    [Fact]
    public void Generate_DifferentMap_DealsDifferentNames()
    {
        var mapA = new bool[132];
        var mapB = new bool[132];
        for (var i = 0; i < 60; i++)
        {
            mapA[i] = true;
            mapB[i + 10] = true;
        }

        var manifestA = PassengerManifestService.Generate(mapA, Capacities);
        var manifestB = PassengerManifestService.Generate(mapB, Capacities);

        // Different seeds → overwhelmingly likely at least one differing name pair.
        Assert.NotEqual(
            manifestA.Select(entry => entry.FirstName + entry.LastName).ToList(),
            manifestB.Select(entry => entry.FirstName + entry.LastName).ToList());
    }

    [Fact]
    public void Generate_EmptyMap_IsEmpty()
        => Assert.Empty(PassengerManifestService.Generate([], Capacities));
}
