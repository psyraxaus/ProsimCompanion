using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>When a GSX "Select Position" prompt may count as a parking conflict (2026-09-20
/// EGLL: the FO spoke the guidance 31 s after touchdown, on the landing roll).</summary>
public sealed class ParkingConflictGateTests
{
    private static FlightDataSnapshot Sample(bool onGround = true, double gs = 0, bool engines = false, bool starting = false)
        => new()
        {
            IsValid = true,
            IsReady = true,
            OnGround = onGround,
            GroundSpeedKt = gs,
            AnyEngineRunning = engines,
            EngineStarting = starting,
        };

    [Fact]
    public void ParkedEnginesOff_IsAConflictCandidate()
        => Assert.True(ParkingConflictGate.IsParkedEnginesOff(Sample()));

    [Fact]
    public void LandingRoll_IsNot()
        => Assert.False(ParkingConflictGate.IsParkedEnginesOff(Sample(gs: 60, engines: true)));

    [Fact]
    public void StoppedWithEnginesRunning_IsNot()
        => Assert.False(ParkingConflictGate.IsParkedEnginesOff(Sample(engines: true)));

    [Fact]
    public void EngineStart_IsNot()
        => Assert.False(ParkingConflictGate.IsParkedEnginesOff(Sample(starting: true)));

    [Fact]
    public void Airborne_IsNot()
        => Assert.False(ParkingConflictGate.IsParkedEnginesOff(Sample(onGround: false, gs: 250, engines: true)));

    [Fact]
    public void NoData_ReadsAsParked()
    {
        Assert.True(ParkingConflictGate.IsParkedEnginesOff(null));
        Assert.True(ParkingConflictGate.IsParkedEnginesOff(new FlightDataSnapshot { IsValid = false, GroundSpeedKt = 90 }));
    }
}
