using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Gate;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gate;

/// <summary>SayIntentions' <c>assigned_gate</c> as the GSX arrival gate (2026-10-08): the
/// pure rule, then the coordinator's ATC-origin dispatch.</summary>
public sealed class AtcAssignedGateTests
{
    [Fact]
    public void Rule_DepartureStand_IsNotedNeverQueued()
    {
        var rule = new AtcAssignedGateRule();

        Assert.Equal(AtcGateDecision.DepartureGateNoted, rule.Observe("B12", FlightPhase.Preflight, null));
        Assert.Equal(AtcGateDecision.DepartureGateNoted, rule.Observe("B12", FlightPhase.TaxiOut, null));
        // Airborne, still the same text: SayIntentions has not reassigned yet.
        Assert.Equal(AtcGateDecision.SameAsDeparture, rule.Observe("B12", FlightPhase.Cruise, null));
        Assert.Equal(AtcGateDecision.None, rule.Observe("B12", FlightPhase.Cruise, null));
    }

    [Fact]
    public void Rule_ChangedGateInCruise_IsQueuedOnce()
    {
        var rule = new AtcAssignedGateRule();
        rule.Observe("B12", FlightPhase.TaxiOut, null);

        Assert.Equal(AtcGateDecision.QueueArrival, rule.Observe(" c29 ", FlightPhase.Cruise, null));
        // Same reading on the next polls (and with our own gate now pending): quiet.
        Assert.Equal(AtcGateDecision.None, rule.Observe("C29", FlightPhase.Cruise, "C29"));
        Assert.Equal(AtcGateDecision.None, rule.Observe("C29", FlightPhase.Descent, "C29"));
    }

    [Fact]
    public void Rule_PilotQueuedGate_Wins()
    {
        var rule = new AtcAssignedGateRule();
        rule.Observe("B12", FlightPhase.TaxiOut, null);

        Assert.Equal(AtcGateDecision.PilotGateWins, rule.Observe("C29", FlightPhase.Cruise, "A1"));
        // ATC changes its mind again — the pilot's still wins.
        Assert.Equal(AtcGateDecision.PilotGateWins, rule.Observe("C30", FlightPhase.Approach, "A1"));
    }

    [Fact]
    public void Rule_AtcReassignment_ReplacesItsOwnEarlierGate()
    {
        var rule = new AtcAssignedGateRule();
        rule.Observe("B12", FlightPhase.TaxiOut, null);
        Assert.Equal(AtcGateDecision.QueueArrival, rule.Observe("C29", FlightPhase.Cruise, null));

        // The pending gate is the one we queued from ATC, not the pilot's — ATC's newer
        // word replaces it.
        Assert.Equal(AtcGateDecision.QueueArrival, rule.Observe("C31", FlightPhase.TaxiIn, "C29"));
    }

    [Fact]
    public void Rule_NoDepartureStandSeen_FirstAirborneGateIsTaken()
    {
        // App started in flight: nothing noted on the ground, so the first airborne gate counts.
        var rule = new AtcAssignedGateRule();

        Assert.Equal(AtcGateDecision.QueueArrival, rule.Observe("C29", FlightPhase.Descent, null));
    }

    [Fact]
    public void Rule_Reset_ForgetsTheDepartureStand()
    {
        var rule = new AtcAssignedGateRule();
        rule.Observe("B12", FlightPhase.TaxiOut, null);
        rule.Reset();

        // A turnaround back to the same stand: ATC's identical gate is a real arrival gate now.
        Assert.Equal(AtcGateDecision.QueueArrival, rule.Observe("B12", FlightPhase.Approach, null));
    }

    [Fact]
    public void Rule_Blank_IsNothing()
    {
        var rule = new AtcAssignedGateRule();

        Assert.Equal(AtcGateDecision.None, rule.Observe(null, FlightPhase.Cruise, null));
        Assert.Equal(AtcGateDecision.None, rule.Observe("  ", FlightPhase.Cruise, null));
    }

    [Fact]
    public async Task Coordinator_ConfirmFromAtc_InClimb_QueuesForCruise_AndSkipsTheAtcPush()
    {
        var (coordinator, phase, gsx, atc, raise) = Build(FlightPhase.Climb);

        coordinator.ConfirmFromAtc("c29");
        var queued = coordinator.Snapshot();
        Assert.Equal("C29", queued.PendingGate);
        Assert.False(queued.Sent);
        Assert.Contains("SayIntentions ATC", queued.AtcStatus, StringComparison.Ordinal);

        phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Cruise);
        raise(new FlightPhaseChangedEventArgs(FlightPhase.Climb, FlightPhase.Cruise));
        await coordinator.LastDispatch;

        Assert.True(coordinator.Snapshot().Sent);
        gsx.Verify(g => g.RequestGate("C29"), Times.Once);
        atc.Verify(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Coordinator_ConfirmFromAtc_AfterLanding_SendsToGsxAtOnce()
    {
        var (coordinator, _, gsx, atc, _) = Build(FlightPhase.TaxiIn);

        coordinator.ConfirmFromAtc("C29");
        await coordinator.LastDispatch;

        Assert.True(coordinator.Snapshot().Sent);
        gsx.Verify(g => g.RequestGate("C29"), Times.Once);
        atc.Verify(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Coordinator_PilotConfirmAfterAtc_SendsToBothAgain()
    {
        var (coordinator, _, gsx, atc, _) = Build(FlightPhase.Cruise);

        coordinator.ConfirmFromAtc("C29");
        await coordinator.LastDispatch;
        // The pilot overrides on the OFP page: theirs goes everywhere, including ATC.
        coordinator.Confirm("A1");
        await coordinator.LastDispatch;

        gsx.Verify(g => g.RequestGate("A1"), Times.Once);
        atc.Verify(a => a.AssignGateAsync("EGLL", "A1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (ArrivalGateCoordinator Coordinator, Mock<IFlightPhaseSource> Phase, Mock<IGsxGateControl> Gsx,
        Mock<ISayIntentionsGateAssign> Atc, Action<FlightPhaseChangedEventArgs> Raise) Build(FlightPhase current)
    {
        var phase = new Mock<IFlightPhaseSource>();
        phase.SetupGet(p => p.CurrentPhase).Returns(current);
        var gsx = new Mock<IGsxGateControl>();
        var atc = new Mock<ISayIntentionsGateAssign>();
        atc.SetupGet(a => a.IsActive).Returns(true);
        atc.Setup(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SayIntentionsGateAssignResult(true, false, "ok"));
        var ofp = new OfpStore();
        ofp.Set(new OfpData { DestinationIcao = "EGLL", FetchedAtUtc = DateTimeOffset.UtcNow });
        var coordinator = new ArrivalGateCoordinator(phase.Object, ofp, gsx.Object, atc.Object,
            NullLogger<ArrivalGateCoordinator>.Instance);
        return (coordinator, phase, gsx, atc, args => phase.Raise(p => p.PhaseChanged += null, phase.Object, args));
    }
}
