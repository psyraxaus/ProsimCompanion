using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The arrival tick's inputs gatherer (C4): one clock of truth — ground contact, engines,
/// brake and speed from the engine's sample, the beacon from the engine's committed ground
/// facts. The arrival chain used to read a raw beacon subscription beside the snapshot's
/// fields, two clocks in one inputs record.
/// </summary>
public sealed class GsxArrivalInputsTests
{
    private static readonly GsxOptions Options = new()
    {
        AutomationEnabled = true,
        ArrivalStableSeconds = 7,
        AutoCallDeboardOnArrival = true,
        AutoGroundEquipment = false,
        ChockDelayMinSec = 3,
        ChockDelayMaxSec = 9,
    };

    private static FlightDataSnapshot Parked(bool rawBeacon = false) => new()
    {
        IsValid = true,
        IsReady = true,
        OnGround = true,
        AnyEngineRunning = false,
        ParkBrakeSet = true,
        GroundSpeedKt = 0.4,
        BeaconOn = rawBeacon,
    };

    [Fact]
    public void BeaconIsTheCommittedFact_NotTheSampleField()
    {
        // The sample carries a raw BeaconOn too; the arrival reads the committed one.
        var offFact = GroundFacts.None with { BeaconOn = false };
        var onFact = GroundFacts.None with { BeaconOn = true };

        Assert.False(GsxArrivalService.ReadInputs(Parked(rawBeacon: true), offFact, GsxAutomationPhase.Arrival, Options, false).BeaconOn);
        Assert.True(GsxArrivalService.ReadInputs(Parked(rawBeacon: false), onFact, GsxAutomationPhase.Arrival, Options, false).BeaconOn);
    }

    [Fact]
    public void SampleFieldsAndOptionsMapThrough()
    {
        var inputs = GsxArrivalService.ReadInputs(Parked(), GroundFacts.None, GsxAutomationPhase.TaxiIn, Options, deboardAlreadyCalled: true);

        Assert.True(inputs.Enabled);
        Assert.Equal(GsxAutomationPhase.TaxiIn, inputs.Phase);
        Assert.True(inputs.SnapshotValid);
        Assert.True(inputs.OnGround);
        Assert.False(inputs.AnyEngineRunning);
        Assert.True(inputs.ParkBrakeSet);
        Assert.Equal(0.4, inputs.GroundSpeedKt);
        Assert.Equal(7, inputs.ArrivalStableSecondsOption);
        Assert.True(inputs.AutoCallDeboard);
        Assert.True(inputs.DeboardAlreadyCalled);
        Assert.False(inputs.AutoGroundEquipment);
        Assert.Equal(3, inputs.ChockDelayMinSec);
        Assert.Equal(9, inputs.ChockDelayMaxSec);
    }

    [Fact]
    public void NoSampleYet_ReadsAsInvalidAndMoving()
    {
        // Before the engine's first sample nothing can count as stable parked: not valid,
        // not on the ground, and a ground speed that fails every "stopped" test.
        var inputs = GsxArrivalService.ReadInputs(null, GroundFacts.None, GsxAutomationPhase.Arrival, Options, false);

        Assert.False(inputs.SnapshotValid);
        Assert.False(inputs.OnGround);
        Assert.False(inputs.ParkBrakeSet);
        Assert.Equal(double.MaxValue, inputs.GroundSpeedKt);
    }

    [Fact]
    public void CommittedBeaconOff_LetsTheCoreCountStableSeconds()
    {
        // End to end through the pure core: with the committed beacon off the parked ticks
        // count; with it on they do not — the same rule as before, on the engine's clock.
        var off = GsxArrivalService.ReadInputs(Parked(), GroundFacts.None, GsxAutomationPhase.Arrival, Options, false);
        var on = GsxArrivalService.ReadInputs(Parked(), GroundFacts.None with { BeaconOn = true }, GsxAutomationPhase.Arrival, Options, false);

        var state = ArrivalCore.Tick(ArrivalCore.ArrivalState.Initial, off, (lo, _) => lo).State;
        Assert.Equal(1, state.StableSeconds);

        state = ArrivalCore.Tick(state, on, (lo, _) => lo).State;
        Assert.Equal(0, state.StableSeconds);
    }
}
