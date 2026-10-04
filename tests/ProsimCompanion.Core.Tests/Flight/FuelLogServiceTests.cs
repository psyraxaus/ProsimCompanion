using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>The Fuel Log's clock (2026-10-05, EFHK departure): the takeoff stamp is on the
/// simulated clock, so the fixes must be too — stamped with the PC clock they read 948
/// minutes late on a flight flown at 03:54Z sim / 19:44Z real.</summary>
public sealed class FuelLogServiceTests
{
    // Simulated time, far from any real "now" the test could run at.
    private static readonly DateTimeOffset SimTakeoff = new(2020, 1, 1, 3, 54, 59, TimeSpan.Zero);

    private static OfpData Ofp() => new()
    {
        RequestId = "r1",
        OriginIcao = "AAAA",
        DestinationIcao = "DDDD",
        FuelPlanLandingKg = 5000,
        Navlog =
        [
            new OfpFix("AAAA", new GeoPoint(50, 0), 9000, TimeSpan.Zero, 0, false),
            new OfpFix("BBB", new GeoPoint(50, 1), 8000, TimeSpan.FromMinutes(10), 30000, false),
            new OfpFix("CCC", new GeoPoint(50, 2), 7000, TimeSpan.FromMinutes(20), 30000, false),
            new OfpFix("DDDD", new GeoPoint(50, 3), 6000, TimeSpan.FromMinutes(30), 0, false),
        ],
    };

    [Fact]
    public void Fixes_AreStampedOnTheSimClock_LikeTheTakeoffTheyAreComparedWith()
    {
        var simNow = SimTakeoff.AddMinutes(1);
        var clock = new Mock<ISimClock>();
        clock.SetupGet(c => c.UtcNowOrReal).Returns(() => simNow);

        var store = new FuelLogStore();
        var ofp = new OfpStore();
        ofp.Set(Ofp());
        var progress = new FlightProgressStore();
        var times = new FlightTimesStore();
        times.Update(_ => FlightTimesSnapshot.Empty with { TakeoffUtc = SimTakeoff });
        var refs = new FakeDataRefs();
        refs.Values[ProsimDataRefNames.FuelTotal.Name] = 9400.0;

        using var service = new FuelLogService(
            store, ofp, progress, times, refs, clock.Object,
            SpeechTestSupport.TempEventLog(), NullLogger<FuelLogService>.Instance);

        progress.Update(_ => new FlightProgressSnapshot { Position = new GeoPoint(50, 0.1) });
        service.ProcessTick();

        simNow = SimTakeoff.AddMinutes(14);
        refs.Values[ProsimDataRefNames.FuelTotal.Name] = 7700.0;
        progress.Update(_ => new FlightProgressSnapshot { Position = new GeoPoint(50, 1.4) });
        service.ProcessTick();

        var log = store.Snapshot();
        Assert.Equal(2, log.PassedCount);
        Assert.Equal(SimTakeoff, log.Rows[0].ActualTimeUtc);
        Assert.Equal(simNow, log.Rows[1].ActualTimeUtc);
        Assert.Equal(4, log.Rows[1].DeltaMinutes(log.TakeoffUtc));     // 14 min flown, 10 planned
    }
}
