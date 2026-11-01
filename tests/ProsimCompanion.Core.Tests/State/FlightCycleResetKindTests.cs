using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>The flight-cycle reset carries its reason (ADR-0019 §6, issue #213): a handler that
/// guards arrival-side state reads it on the raising thread.</summary>
public sealed class FlightCycleResetKindTests
{
    [Fact]
    public void TheArrivalReset_IsTheDefault_AndHandlersSeeIt()
    {
        var signals = new GroundOpsSignals();
        FlightCycleResetKind? seen = null;
        signals.FlightCycleReset += () => seen = signals.LastFlightCycleResetKind;

        signals.RaiseFlightCycleReset();

        Assert.Equal(FlightCycleResetKind.Arrival, seen);
    }

    [Fact]
    public void ANewPlanReset_IsSeenAsSuch_WhileItFires()
    {
        var signals = new GroundOpsSignals();
        FlightCycleResetKind? seen = null;
        signals.FlightCycleReset += () => seen = signals.LastFlightCycleResetKind;

        signals.RaiseFlightCycleReset(FlightCycleResetKind.NewPlan);

        Assert.Equal(FlightCycleResetKind.NewPlan, seen);
        Assert.Equal(FlightCycleResetKind.NewPlan, signals.LastFlightCycleResetKind);
    }
}
