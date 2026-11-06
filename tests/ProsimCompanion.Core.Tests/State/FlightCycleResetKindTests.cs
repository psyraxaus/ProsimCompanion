using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>The flight-cycle reset carries its reason (ADR-0019 §6, issue #213) AS THE EVENT
/// ARGUMENT (review 2026-11-05): a handler that guards arrival-side state reads the kind of the
/// reset it is handling, never a shared "last kind" another thread may have overwritten.</summary>
public sealed class FlightCycleResetKindTests
{
    [Fact]
    public void TheArrivalReset_IsTheDefault_AndHandlersSeeIt()
    {
        var signals = new GroundOpsSignals();
        FlightCycleResetKind? seen = null;
        signals.FlightCycleReset += kind => seen = kind;

        signals.RaiseFlightCycleReset();

        Assert.Equal(FlightCycleResetKind.Arrival, seen);
    }

    [Fact]
    public void ANewPlanReset_IsSeenAsSuch_ByEveryHandler()
    {
        var signals = new GroundOpsSignals();
        var seen = new List<FlightCycleResetKind>();
        signals.FlightCycleReset += kind => seen.Add(kind);
        signals.FlightCycleReset += kind => seen.Add(kind);

        signals.RaiseFlightCycleReset(FlightCycleResetKind.NewPlan);
        signals.RaiseFlightCycleReset(FlightCycleResetKind.Arrival);

        Assert.Equal([FlightCycleResetKind.NewPlan, FlightCycleResetKind.NewPlan, FlightCycleResetKind.Arrival, FlightCycleResetKind.Arrival], seen);
    }

    [Fact]
    public void NoSharedLastKind_RemainsOnTheSignals()
        => Assert.Null(typeof(GroundOpsSignals).GetProperty("LastFlightCycleResetKind"));
}
