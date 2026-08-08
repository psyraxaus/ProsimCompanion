using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Gate;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gate;

public sealed class ArrivalGatePlanTests
{
    [Fact]
    public void Queue_NormalizesAndArms()
    {
        var queue = new ArrivalGatePlan();

        Assert.Equal("B12", queue.Queue("  b12 "));
        Assert.Equal("B12", queue.PendingGate);
        Assert.False(queue.Fired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Queue_BlankInput_QueuesNothing(string? gate)
    {
        var queue = new ArrivalGatePlan();

        Assert.Null(queue.Queue(gate));
        Assert.Null(queue.PendingGate);
    }

    [Fact]
    public void TakeAuto_AtCruise_FiresExactlyOnce()
    {
        var queue = new ArrivalGatePlan();
        queue.Queue("B12");

        Assert.Equal("B12", queue.TakeAuto(FlightPhase.Cruise));
        // PhaseChanged can commit Cruise again (e.g. step climb re-entry) — never refire.
        Assert.Null(queue.TakeAuto(FlightPhase.Cruise));
        Assert.True(queue.Fired);
        Assert.Equal("B12", queue.PendingGate);
    }

    [Theory]
    [InlineData(FlightPhase.Climb)]
    [InlineData(FlightPhase.Descent)]
    [InlineData(FlightPhase.Preflight)]
    public void TakeAuto_OutsideCruise_DoesNotFire(FlightPhase phase)
    {
        var queue = new ArrivalGatePlan();
        queue.Queue("B12");

        Assert.Null(queue.TakeAuto(phase));
        Assert.False(queue.Fired);
    }

    [Fact]
    public void TakeAuto_NothingQueued_DoesNotFire()
    {
        var queue = new ArrivalGatePlan();

        Assert.Null(queue.TakeAuto(FlightPhase.Cruise));
    }

    [Fact]
    public void Requeue_AfterAutoFire_RearmsAutoFire()
    {
        var queue = new ArrivalGatePlan();
        queue.Queue("B12");
        queue.TakeAuto(FlightPhase.Cruise);

        queue.Queue("C3");

        Assert.False(queue.Fired);
        Assert.Equal("C3", queue.TakeAuto(FlightPhase.Cruise));
    }

    [Fact]
    public void Clear_ThenRequeue_AllowsAutoFireAgain()
    {
        var queue = new ArrivalGatePlan();
        queue.Queue("B12");
        queue.TakeAuto(FlightPhase.Cruise);

        queue.Clear();
        Assert.Null(queue.PendingGate);
        Assert.False(queue.Fired);

        queue.Queue("B12");
        Assert.Equal("B12", queue.TakeAuto(FlightPhase.Cruise));
    }

    [Fact]
    public void TakeManual_ExplicitGate_WinsOverPendingAndMarksFired()
    {
        var queue = new ArrivalGatePlan();
        queue.Queue("B12");

        Assert.Equal("C3", queue.TakeManual(" c3 "));
        Assert.Equal("C3", queue.PendingGate);
        Assert.True(queue.Fired);
        // Cruise entry after a manual send must not resend.
        Assert.Null(queue.TakeAuto(FlightPhase.Cruise));
    }

    [Fact]
    public void TakeManual_NoExplicitGate_SendsPending()
    {
        var queue = new ArrivalGatePlan();
        queue.Queue("B12");

        Assert.Equal("B12", queue.TakeManual());
        Assert.True(queue.Fired);
    }

    [Fact]
    public void TakeManual_NothingPendingOrExplicit_SendsNothing()
    {
        var queue = new ArrivalGatePlan();

        Assert.Null(queue.TakeManual());
        Assert.Null(queue.TakeManual("  "));
        Assert.False(queue.Fired);
    }
}
