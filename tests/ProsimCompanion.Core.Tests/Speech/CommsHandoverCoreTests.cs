using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #171: who has the radios through a flight, driven by the app's own phase
/// edges and runway distance — the 2026-10-10 EGCC leg's phase timeline is the script.</summary>
public sealed class CommsHandoverCoreTests
{
    private static readonly CommsHandoverRules All = CommsHandoverRules.From(new CommsHandoverOptions { Enabled = true });

    [Fact]
    public void FullFlight_HandsOverAtEachStepExactlyOnce()
    {
        var core = new CommsHandoverCore();
        var decisions = new List<CommsHandoverDecision>();
        void Add(CommsHandoverDecision? d)
        {
            if (d is not null)
            {
                decisions.Add(d);
            }
        }

        Add(core.OnPhaseChanged(FlightPhase.Preflight, All));
        Add(core.OnFlightActive(FlightPhase.Preflight, All));              // (a) copilot at the gate
        Add(core.OnPhaseChanged(FlightPhase.Departure, All));
        Add(core.OnPhaseChanged(FlightPhase.PushbackAndStart, All));
        Add(core.OnPhaseChanged(FlightPhase.TaxiOut, All));
        Add(core.OnDistanceToRunway(2.4, All));                            // still taxiing
        Add(core.OnDistanceToRunway(0.28, All));                           // (c) holding point → captain
        Add(core.OnDistanceToRunway(0.1, All));                            // once per taxi
        Add(core.OnPhaseChanged(FlightPhase.TakeoffRoll, All));
        Add(core.OnPhaseChanged(FlightPhase.InitialClimb, All));
        Add(core.OnPhaseChanged(FlightPhase.Climb, All));                  // (d) copilot regains
        Add(core.OnPhaseChanged(FlightPhase.Cruise, All));
        Add(core.OnPhaseChanged(FlightPhase.Descent, All));
        Add(core.OnPhaseChanged(FlightPhase.Approach, All));               // (f) captain for the landing
        Add(core.OnPhaseChanged(FlightPhase.LandingRollout, All));
        Add(core.OnPhaseChanged(FlightPhase.TaxiIn, All));                 // (g) copilot regains
        Add(core.OnPhaseChanged(FlightPhase.Shutdown, All));

        Assert.Equal(
            ["flight-active:Copilot", "holding-point:Captain", "climb:Copilot", "approach:Captain", "taxi-in:Copilot"],
            decisions.Select(d => $"{d.Trigger}:{d.Owner}"));
        Assert.Equal(CommsOwner.Unknown, core.Owner);
    }

    [Fact]
    public void FlightActive_InTheAir_DoesNotHandToTheCopilot()
    {
        // The app (or SayIntentions) started mid-descent: step (a) is a ground step only.
        var core = new CommsHandoverCore();
        core.OnPhaseChanged(FlightPhase.Descent, All);

        Assert.Null(core.OnFlightActive(FlightPhase.Descent, All));
        Assert.Equal(CommsOwner.Unknown, core.Owner);
    }

    [Fact]
    public void HoldingPoint_OnlyInTaxiOut_AndNeverWithoutADistance()
    {
        var core = new CommsHandoverCore();
        core.OnFlightActive(FlightPhase.Preflight, All);
        core.OnPhaseChanged(FlightPhase.PushbackAndStart, All);

        Assert.Null(core.OnDistanceToRunway(0.1, All));                    // pushing back past the threshold
        core.OnPhaseChanged(FlightPhase.TaxiOut, All);
        Assert.Null(core.OnDistanceToRunway(null, All));                   // runway unknown
        Assert.Null(core.OnDistanceToRunway(double.NaN, All));
        Assert.Null(core.OnDistanceToRunway(0.31, All));
        Assert.NotNull(core.OnDistanceToRunway(0.30, All));
        Assert.Equal(CommsOwner.Captain, core.Owner);
    }

    [Fact]
    public void HoldingPoint_Switch_Off_LeavesTheCopilot()
    {
        var rules = All with { HoldingPointToCaptain = false };
        var core = new CommsHandoverCore();
        core.OnFlightActive(FlightPhase.Preflight, rules);
        core.OnPhaseChanged(FlightPhase.TaxiOut, rules);

        Assert.Null(core.OnDistanceToRunway(0.1, rules));
        Assert.Equal(CommsOwner.Copilot, core.Owner);
    }

    [Fact]
    public void TowerHandoff_InDescent_TakesTheRadiosEarly_AndApproachEdgeStaysQuiet()
    {
        var core = new CommsHandoverCore();
        core.OnPhaseChanged(FlightPhase.Climb, All);
        core.OnPhaseChanged(FlightPhase.Cruise, All);
        Assert.Null(core.OnTowerHandoff(All));                             // not in the descent
        core.OnPhaseChanged(FlightPhase.Descent, All);

        var early = core.OnTowerHandoff(All);
        Assert.NotNull(early);
        Assert.Equal(("tower-handoff", CommsOwner.Captain), (early.Trigger, early.Owner));
        Assert.Null(core.OnTowerHandoff(All));                             // one per edge
        Assert.Null(core.OnPhaseChanged(FlightPhase.Approach, All));       // already the captain's
    }

    [Fact]
    public void ClimbEdgeSkipped_CruiseIsTheCopilotsSafetyNet()
    {
        var core = new CommsHandoverCore();
        core.OnFlightActive(FlightPhase.TaxiOut, All);
        core.OnPhaseChanged(FlightPhase.TaxiOut, All);
        core.OnDistanceToRunway(0.2, All);
        core.OnPhaseChanged(FlightPhase.TakeoffRoll, All);
        core.OnPhaseChanged(FlightPhase.InitialClimb, All);

        var cruise = core.OnPhaseChanged(FlightPhase.Cruise, All);
        Assert.NotNull(cruise);
        Assert.Equal("cruise", cruise.Trigger);

        // With a Climb edge seen, Cruise is silent.
        var normal = new CommsHandoverCore();
        normal.OnPhaseChanged(FlightPhase.Climb, All);
        Assert.Null(normal.OnPhaseChanged(FlightPhase.Cruise, All));
    }

    [Fact]
    public void VoiceOverride_Flips_AndIsSilentWhenAlreadyTheOwner()
    {
        var core = new CommsHandoverCore();
        core.OnFlightActive(FlightPhase.Preflight, All);

        Assert.Null(core.Override(CommsOwner.Copilot));
        var mine = core.Override(CommsOwner.Captain);
        Assert.NotNull(mine);
        Assert.Equal(("voice", CommsOwner.Captain), (mine.Trigger, mine.Owner));
        Assert.Null(core.Override(CommsOwner.Unknown));

        // The next scheduled edge still runs — the captain knows the flow.
        core.OnPhaseChanged(FlightPhase.Climb, All);
        Assert.Equal(CommsOwner.Copilot, core.Owner);
    }

    [Fact]
    public void EveryStepHasItsOwnSwitch()
    {
        var none = new CommsHandoverRules(false, false, 0.3, false, false, false);
        var core = new CommsHandoverCore();

        Assert.Null(core.OnFlightActive(FlightPhase.Preflight, none));
        core.OnPhaseChanged(FlightPhase.TaxiOut, none);
        Assert.Null(core.OnDistanceToRunway(0.1, none));
        Assert.Null(core.OnPhaseChanged(FlightPhase.Climb, none));
        Assert.Null(core.OnPhaseChanged(FlightPhase.Descent, none));
        Assert.Null(core.OnTowerHandoff(none));
        Assert.Null(core.OnPhaseChanged(FlightPhase.Approach, none));
        Assert.Null(core.OnPhaseChanged(FlightPhase.TaxiIn, none));
        Assert.Equal(CommsOwner.Unknown, core.Owner);
    }

    [Fact]
    public void Turnaround_StartsTheNextLegFresh()
    {
        var core = new CommsHandoverCore();
        core.OnFlightActive(FlightPhase.Preflight, All);
        core.OnPhaseChanged(FlightPhase.TaxiOut, All);
        core.OnDistanceToRunway(0.1, All);
        core.OnPhaseChanged(FlightPhase.Shutdown, All);
        Assert.Equal(CommsOwner.Unknown, core.Owner);

        core.OnPhaseChanged(FlightPhase.Preflight, All);
        Assert.NotNull(core.OnFlightActive(FlightPhase.Preflight, All));
        core.OnPhaseChanged(FlightPhase.TaxiOut, All);
        Assert.NotNull(core.OnDistanceToRunway(0.1, All));                 // the holding point arms again
    }

    [Fact]
    public void Rules_FromOptions_FloorsTheDistance()
    {
        var rules = CommsHandoverRules.From(new CommsHandoverOptions { HoldingPointDistanceNm = 0 });
        Assert.Equal(0.05, rules.HoldingPointDistanceNm);
    }
}
