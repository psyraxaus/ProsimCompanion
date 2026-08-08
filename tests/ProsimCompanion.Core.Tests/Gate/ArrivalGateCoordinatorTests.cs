using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Gate;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gate;

public sealed class ArrivalGateCoordinatorTests
{
    private readonly Mock<IFlightPhaseSource> _phase = new();
    private readonly Mock<IGsxGateControl> _gsx = new();
    private readonly Mock<ISayIntentionsGateAssign> _atc = new();
    private readonly OfpStore _ofp = new();

    public ArrivalGateCoordinatorTests()
    {
        _phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Climb);
        _atc.SetupGet(a => a.IsActive).Returns(true);
        _atc.Setup(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SayIntentionsGateAssignResult(true, false, "Gate B12"));
        _ofp.Set(new OfpData { DestinationIcao = "YSSY", FetchedAtUtc = DateTimeOffset.UtcNow });
    }

    private ArrivalGateCoordinator CreateCoordinator(
        IGsxGateControl? gsx = null, ISayIntentionsGateAssign? atc = null)
        => new(
            _phase.Object,
            _ofp,
            gsx ?? _gsx.Object,
            atc ?? _atc.Object,
            NullLogger<ArrivalGateCoordinator>.Instance);

    private void RaiseCruise(FlightPhase previous = FlightPhase.Climb)
        => _phase.Raise(p => p.PhaseChanged += null, _phase.Object,
            new FlightPhaseChangedEventArgs(previous, FlightPhase.Cruise));

    [Fact]
    public void Confirm_QueuesWithoutDispatching()
    {
        using var coordinator = CreateCoordinator();

        coordinator.Confirm("b12");

        var view = coordinator.Snapshot();
        Assert.Equal("B12", view.PendingGate);
        Assert.False(view.Sent);
        _gsx.Verify(g => g.RequestGate(It.IsAny<string>()), Times.Never);
        _atc.Verify(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CruiseTransition_FiresBothTargetsOnce()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Confirm("B12");

        RaiseCruise();
        await coordinator.LastDispatch;
        // The engine can commit Cruise again (step climb) — must not refire.
        RaiseCruise(FlightPhase.Descent);
        await coordinator.LastDispatch;

        _gsx.Verify(g => g.RequestGate("B12"), Times.Once);
        _atc.Verify(a => a.AssignGateAsync("YSSY", "B12", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(coordinator.Snapshot().Sent);
        Assert.Contains("Gate B12", coordinator.Snapshot().AtcStatus);
    }

    [Fact]
    public async Task Confirm_WhileAlreadyAtCruise_FiresImmediately()
    {
        _phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Cruise);
        using var coordinator = CreateCoordinator();

        coordinator.Confirm("B12");
        await coordinator.LastDispatch;

        _gsx.Verify(g => g.RequestGate("B12"), Times.Once);
        _atc.Verify(a => a.AssignGateAsync("YSSY", "B12", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendNow_FiresImmediately_AndCruiseDoesNotResend()
    {
        using var coordinator = CreateCoordinator();

        await coordinator.SendNow("B12");
        RaiseCruise();
        await coordinator.LastDispatch;

        _gsx.Verify(g => g.RequestGate("B12"), Times.Once);
        _atc.Verify(a => a.AssignGateAsync("YSSY", "B12", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendNow_WithNothingPending_DoesNothing()
    {
        using var coordinator = CreateCoordinator();

        await coordinator.SendNow();

        _gsx.Verify(g => g.RequestGate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_ClearsQueue_CancelsGsx_AndRearmsAfterRequeue()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Confirm("B12");

        coordinator.Cancel();
        RaiseCruise();
        await coordinator.LastDispatch;
        _gsx.Verify(g => g.Cancel(), Times.Once);
        _gsx.Verify(g => g.RequestGate(It.IsAny<string>()), Times.Never);
        Assert.Null(coordinator.Snapshot().PendingGate);

        // Re-queue after cancel is a fresh request and auto-fires again.
        coordinator.Confirm("C3");
        RaiseCruise(FlightPhase.Descent);
        await coordinator.LastDispatch;
        _gsx.Verify(g => g.RequestGate("C3"), Times.Once);
    }

    [Fact]
    public async Task SkippedAtcResult_ShowsDetail_AndGsxStillFires()
    {
        _atc.Setup(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SayIntentionsGateAssignResult(false, true, "SayIntentions disabled — ATC assignment skipped."));
        using var coordinator = CreateCoordinator();

        await coordinator.SendNow("B12");

        _gsx.Verify(g => g.RequestGate("B12"), Times.Once);
        Assert.Equal("SayIntentions disabled — ATC assignment skipped.", coordinator.Snapshot().AtcStatus);
    }

    [Fact]
    public async Task NoDestinationIcao_SkipsAtc_ButFiresGsx()
    {
        _ofp.Clear();
        using var coordinator = CreateCoordinator();

        await coordinator.SendNow("B12");

        _gsx.Verify(g => g.RequestGate("B12"), Times.Once);
        _atc.Verify(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("No destination ICAO", coordinator.Snapshot().AtcStatus);
    }

    [Fact]
    public async Task AtcFailure_ReportsOnStatusLine_WithoutThrowing()
    {
        _atc.Setup(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        using var coordinator = CreateCoordinator();

        await coordinator.SendNow("B12");

        Assert.Contains("failed", coordinator.Snapshot().AtcStatus, StringComparison.OrdinalIgnoreCase);
        _gsx.Verify(g => g.RequestGate("B12"), Times.Once);
    }

    [Fact]
    public async Task AbsentTargets_DegradeToStatusLines()
    {
        using var coordinator = new ArrivalGateCoordinator(
            _phase.Object, _ofp, gsxGate: null, sayIntentions: null,
            NullLogger<ArrivalGateCoordinator>.Instance);

        await coordinator.SendNow("B12");

        var view = coordinator.Snapshot();
        Assert.Contains("unavailable", view.GsxStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not available", view.AtcStatus, StringComparison.OrdinalIgnoreCase);
    }
}
