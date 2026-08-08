using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Prosim.Passengers;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

public sealed class PassengerSimulationServiceTests
{
    private readonly Mock<IProsimGateway> _gateway = new();
    private readonly Dictionary<string, object> _writes = new(StringComparer.Ordinal);
    private readonly List<string> _writeOrder = [];

    private PassengerSimulationService CreateService(int[]? capacities = null)
    {
        capacities ??= [4, 6, 8, 10];
        var subs = capacities.Select(capacity =>
        {
            var sub = new Mock<IDataRefSubscription>();
            sub.Setup(s => s.GetValue(It.IsAny<int>())).Returns(capacity);
            return sub.Object;
        }).ToArray();

        var prosim = new Mock<IProsimDataRefs>();
        var next = 0;
        prosim
            .Setup(p => p.Subscribe(It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns(() => subs[next++]);

        _gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) =>
            {
                _writes[name] = value;
                _writeOrder.Add(name);
            })
            .ReturnsAsync(true);

        return new PassengerSimulationService(prosim.Object, _gateway.Object, NullLogger<PassengerSimulationService>.Instance);
    }

    [Fact]
    public async Task Generate_WritesBookedStatisticsAndOccupation_WithMatchingMaps()
    {
        var service = CreateService();

        var ok = await service.GenerateAsync(15);

        Assert.True(ok);
        Assert.Equal(
            [ProsimDataRefNames.PaxBookedString, ProsimDataRefNames.EfbPassengerStatistics, ProsimDataRefNames.PaxSeatOccupationString],
            _writeOrder);

        // Booked and occupation carry the SAME map — what is planned is what is aboard.
        var booked = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxBookedString]);
        var occupation = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxSeatOccupationString]);
        Assert.Equal(booked, occupation);
        Assert.Equal(28, booked.Length);
        Assert.Equal(15, booked.Count(seat => seat));
    }

    [Fact]
    public async Task Generate_StatisticsMatchThePerZoneCounts()
    {
        var service = CreateService();

        Assert.True(await service.GenerateAsync(15));

        var map = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxBookedString]);
        var perZone = SeatMap.CountPerZone(map, [4, 6, 8, 10]);
        using var statistics = JsonDocument.Parse((string)_writes[ProsimDataRefNames.EfbPassengerStatistics]);
        var root = statistics.RootElement;

        Assert.Equal(15, root.GetProperty("Total").GetInt32());
        Assert.Equal(perZone[0], root.GetProperty("NumOfPaxInBusiness").GetInt32());
        Assert.Equal(perZone[1] + perZone[2] + perZone[3], root.GetProperty("NumOfPaxInEconomy").GetInt32());
        Assert.Equal(perZone[0], root.GetProperty("NumOfPaxInSection1").GetInt32());
        Assert.Equal(perZone[1], root.GetProperty("NumOfPaxInSection2").GetInt32());
        Assert.Equal(perZone[2] + perZone[3], root.GetProperty("NumOfPaxInSection3").GetInt32());
    }

    [Fact]
    public async Task Generate_ClampsToCabinCapacity()
    {
        var service = CreateService();

        Assert.True(await service.GenerateAsync(999));

        var map = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxSeatOccupationString]);
        Assert.Equal(28, map.Count(seat => seat));    // full cabin, never more
    }

    [Fact]
    public async Task Generate_NegativeCount_ClampsToEmpty()
    {
        var service = CreateService();

        Assert.True(await service.GenerateAsync(-5));

        var map = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxSeatOccupationString]);
        Assert.All(map, seat => Assert.False(seat));
    }

    [Fact]
    public async Task Generate_ZoneCapacitiesNotPopulated_FallsBackToA320Standard()
    {
        var service = CreateService([0, 0, 0, 0]);

        Assert.True(await service.GenerateAsync(100));

        var map = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxBookedString]);
        Assert.Equal(132, map.Length);    // 24+30+36+42
        Assert.Equal(100, map.Count(seat => seat));
    }

    [Fact]
    public async Task Generate_GatewayFailure_ReturnsFalse()
    {
        var service = CreateService();
        _gateway
            .Setup(g => g.WriteDataRefAsync(ProsimDataRefNames.PaxSeatOccupationString, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.False(await service.GenerateAsync(10));
    }

    [Fact]
    public async Task Clear_WritesAllFalseOccupation_AndLeavesTheBookedMapAlone()
    {
        var service = CreateService();

        var ok = await service.ClearAsync();

        Assert.True(ok);
        Assert.Equal([ProsimDataRefNames.PaxSeatOccupationString], _writeOrder);
        var map = SeatMap.Parse((string)_writes[ProsimDataRefNames.PaxSeatOccupationString]);
        Assert.Equal(28, map.Length);
        Assert.All(map, seat => Assert.False(seat));
    }

    [Fact]
    public async Task Clear_GatewayFailure_ReturnsFalse()
    {
        var service = CreateService();
        _gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.False(await service.ClearAsync());
    }
}
