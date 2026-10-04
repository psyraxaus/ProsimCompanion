using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #148, module 1: the fuel check core and its shell's gating.</summary>
public sealed class FuelCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // A straight route east along the equator: fixes every 2° (120 nm), fuel 300 kg per leg.
    private static readonly OfpFix[] Navlog =
    [
        new("ALPHA", new GeoPoint(0, 10), 6000, TimeSpan.FromMinutes(10), 37000, true),
        new("BRAVO", new GeoPoint(0, 12), 5700, TimeSpan.FromMinutes(25), 37000, false),
        new("CHARL", new GeoPoint(0, 14), 5400, TimeSpan.FromMinutes(40), 37000, false),
        new("DELTA", new GeoPoint(0, 16), 5100, TimeSpan.FromMinutes(55), 37000, false),
        new("DEST", new GeoPoint(0, 18), 4800, TimeSpan.FromMinutes(70), 15, true),
    ];

    private static readonly FuelCheckOptions Options = new() { Enabled = true, ShortfallMarginKg = 300, OnPlanWithinKg = 100 };

    private static FuelCheckInputs Inputs(double? fob, double? lon, double flow = 2400, double plannedLanding = 4800, DateTimeOffset? eta = null, IReadOnlyList<OfpFix>? navlog = null)
        => new(fob, flow, lon is { } l ? new GeoPoint(0, l) : null, navlog ?? Navlog, plannedLanding, T0, eta);

    // ---- last fix passed ----

    [Fact]
    public void LastFixPassed_OnALeg_IsThatLegsStartFix_WithTheFractionAlongIt()
    {
        var quarter = FuelCheckCore.LastFixPassed(Navlog, new GeoPoint(0, 12.5));

        Assert.Equal(1, quarter!.Value.Index);          // past BRAVO
        Assert.Equal(0.25, quarter.Value.Fraction, 2);
    }

    [Fact]
    public void LastFixPassed_SlightlyOffTheLeg_StillCountsAsOnIt()
    {
        var passed = FuelCheckCore.LastFixPassed(Navlog, new GeoPoint(0.2, 15));   // 12 nm north of the line

        Assert.Equal(2, passed!.Value.Index);
        Assert.Equal(0.5, passed.Value.Fraction, 1);
    }

    [Fact]
    public void LastFixPassed_WellOffRoute_UsesTheNearestFix_AndWhetherItIsBehind()
    {
        // 60 nm north of CHARL, past it (closer to the destination than CHARL is).
        var passedCharl = FuelCheckCore.LastFixPassed(Navlog, new GeoPoint(1, 14.3));
        // 60 nm north, abeam a point BEFORE CHARL: BRAVO is the last fix passed.
        var beforeCharl = FuelCheckCore.LastFixPassed(Navlog, new GeoPoint(1, 13.7));

        Assert.Equal((2, 0.0), passedCharl);
        Assert.Equal((1, 0.0), beforeCharl);
    }

    [Fact]
    public void LastFixPassed_BeforeTheFirstFix_AndPastTheLast()
    {
        Assert.Equal((0, 0.0), FuelCheckCore.LastFixPassed(Navlog, new GeoPoint(0, 9)));
        Assert.Equal((3, 1.0), FuelCheckCore.LastFixPassed(Navlog, new GeoPoint(0, 18.4)));
        Assert.Null(FuelCheckCore.LastFixPassed([Navlog[0]], new GeoPoint(0, 9)));
        Assert.Null(FuelCheckCore.LastFixPassed([], new GeoPoint(0, 9)));
    }

    [Fact]
    public void PlannedFuel_InterpolatesAlongTheLeg_AndFallsBackToTheFixFigure()
    {
        Assert.Equal(5550, FuelCheckCore.PlannedFuelAt(Navlog, 1, 0.5));
        Assert.Equal(5700, FuelCheckCore.PlannedFuelAt(Navlog, 1, 0));
        Assert.Equal(4800, FuelCheckCore.PlannedFuelAt(Navlog, 4, 0.5));      // last fix: its own figure
        var noNextFuel = new[] { Navlog[0], Navlog[1] with { PlannedFuelOnBoardKg = null } };
        Assert.Equal(6000, FuelCheckCore.PlannedFuelAt(noNextFuel, 0, 0.5));
        Assert.Null(FuelCheckCore.PlannedFuelAt(noNextFuel, 1, 0));
    }

    // ---- compute ----

    [Fact]
    public void Compute_Navlog_SpeaksTheFixTheFigureTheDifferenceAndTheLandingEstimate()
    {
        // Half way BRAVO → CHARL the plan says 5550; we have 5400: 150 below plan.
        var result = FuelCheckCore.Compute(Inputs(fob: 5400, lon: 13), Options);

        Assert.NotNull(result);
        Assert.Equal("navlog", result.Method);
        Assert.Equal("BRAVO", result.Fix);
        Assert.Equal(5550, result.PlannedFuelOnBoardKg);
        Assert.Equal(-150, result.DifferenceKg);
        Assert.Equal(4650, result.EstimatedLandingKg);         // planned landing + difference
        Assert.False(result.Shortfall);
        Assert.Equal(SpeechPriority.Normal, result.Priority);
        Assert.Equal(
            "Fuel check. Past BRAVO, fuel on board 5.4 tonnes, 200 kilos below plan. Estimated landing fuel 4.7 tonnes, planned 4.8.",
            result.Text);
    }

    [Fact]
    public void Compute_RightOverAFix_SaysPassing_AndOnPlanWithinTheMargin()
    {
        var result = FuelCheckCore.Compute(Inputs(fob: 5450, lon: 14.05), Options);   // 3 nm past CHARL: plan ≈ 5392

        Assert.StartsWith("Fuel check. Passing CHARL, fuel on board 5.5 tonnes, on plan.", result!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_AShortfallBeyondTheMargin_IsHigh()
    {
        var result = FuelCheckCore.Compute(Inputs(fob: 5000, lon: 13), Options);   // 550 below

        Assert.True(result!.Shortfall);
        Assert.Equal(SpeechPriority.High, result.Priority);
        Assert.Contains("600 kilos below plan", result.Text, StringComparison.Ordinal);
        Assert.Contains("Estimated landing fuel 4.3 tonnes, planned 4.8.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_ABigSurplus_IsSpokenInTonnes()
    {
        var result = FuelCheckCore.Compute(Inputs(fob: 6800, lon: 13), Options);   // 1250 above

        Assert.Contains("1.3 tonnes above plan", result!.Text, StringComparison.Ordinal);
        Assert.Equal(SpeechPriority.Normal, result.Priority);
    }

    [Fact]
    public void Compute_WithoutAPosition_FallsBackToFlowTimesTimeToTheEta()
    {
        // 5400 kg, 2400 kg/h, 30 minutes to go: 4200 at landing against 4800 planned.
        var result = FuelCheckCore.Compute(Inputs(fob: 5400, lon: null, eta: T0.AddMinutes(30)), Options);

        Assert.Equal("fallback", result!.Method);
        Assert.Null(result.Fix);
        Assert.Equal(4200, result.EstimatedLandingKg!.Value, 0);
        Assert.Equal(-600, result.DifferenceKg!.Value, 0);
        Assert.True(result.Shortfall);
        Assert.Equal("Fuel check. Fuel on board 5.4 tonnes. Estimated landing fuel 4.2 tonnes, planned 4.8.", result.Text);
    }

    [Fact]
    public void Compute_WithoutANavlog_AlsoFallsBack()
    {
        var result = FuelCheckCore.Compute(Inputs(fob: 5400, lon: 13, eta: T0.AddMinutes(60), navlog: []), Options);

        Assert.Equal("fallback", result!.Method);
    }

    [Fact]
    public void Compute_FallbackWithoutEtaOrFlow_StillGivesTheFigureAndThePlan()
    {
        var noEta = FuelCheckCore.Compute(Inputs(fob: 5400, lon: null), Options);
        var noFlow = FuelCheckCore.Compute(Inputs(fob: 5400, lon: null, flow: 0, eta: T0.AddMinutes(30)), Options);

        Assert.Equal("Fuel check. Fuel on board 5.4 tonnes. Planned landing fuel 4.8 tonnes.", noEta!.Text);
        Assert.Null(noEta.EstimatedLandingKg);
        Assert.False(noEta.Shortfall);
        Assert.Equal(noEta.Text, noFlow!.Text);
    }

    [Fact]
    public void Compute_NothingToSay_IsNull()
    {
        Assert.Null(FuelCheckCore.Compute(Inputs(fob: null, lon: 13), Options));                                   // no fuel figure
        Assert.Null(FuelCheckCore.Compute(Inputs(fob: 0, lon: 13), Options));
        Assert.Null(FuelCheckCore.Compute(Inputs(fob: 5400, lon: null, plannedLanding: 0), Options));             // no plan, no ETA
        Assert.Null(FuelCheckCore.Compute(Inputs(fob: 5400, lon: 13, plannedLanding: 0, navlog: []), Options));
    }

    [Theory]
    [InlineData(-50, "on plan")]
    [InlineData(-149, "100 kilos below plan")]
    [InlineData(-150, "200 kilos below plan")]
    [InlineData(260, "300 kilos above plan")]
    [InlineData(-1250, "1.3 tonnes below plan")]
    public void Difference_Wording(double delta, string expected)
        => Assert.Equal(expected, FuelCheckCore.Difference(delta, Options));

    // ---- the shell's gating ----

    private sealed class Harness
    {
        public FakePhaseSource Phases { get; } = new();
        public FakeArbiter Arbiter { get; } = new();
        public FakeDataRefs Refs { get; } = new();
        public OfpStore Ofp { get; } = new();
        public FlightProgressStore Progress { get; } = new();
        public SopOptions Sop { get; } = new();
        public FuelCheckLogStore CheckLog { get; } = new();
        public FuelCheckMonitor Monitor { get; }

        public Harness(ProsimCompanion.Core.Aircraft.ISimClock? simClock = null)
        {
            Sop.Monitoring.FuelCheck.Enabled = true;
            Sop.Monitoring.FuelCheck.IntervalMinutes = 30;
            Refs.Values["aircraft.fuel.total.amount.kg"] = 5400.0;
            Refs.Values["aircraft.engines.1.ff.kg"] = 1200.0;
            Refs.Values["aircraft.engines.2.ff.kg"] = 1200.0;
            Ofp.Set(new OfpData { FuelPlanLandingKg = 4800, Navlog = Navlog });
            Progress.Update(_ => new FlightProgressSnapshot { Position = new GeoPoint(0, 13) });
            Phases.Data = new FlightDataSnapshot { IsValid = true, AltitudeFt = 37000 };
            Monitor = new FuelCheckMonitor(
                SpeechTestSupport.SopMonitor(Sop), SpeechTestSupport.SpeechMonitor(new SpeechOptions()),
                Phases, Progress, Ofp, Refs, Arbiter, SpeechTestSupport.TempEventLog(), NullLogger<FuelCheckMonitor>.Instance,
                CheckLog, simClock);
        }
    }

    // 2026-10-05 (sim 03:54Z flown at real 19:44Z): the ETA the fallback burns to is on the
    // simulated clock, so the check must read the same clock — against the PC clock a sim ETA
    // in the "past" gave no estimate at all, and the page showed the check at the real time.
    [Fact]
    public void ACheck_RunsOnTheSimClock_LikeTheEtaAndTheFuelLog()
    {
        var simNow = new DateTimeOffset(2020, 1, 1, 3, 54, 0, TimeSpan.Zero);
        var clock = new Moq.Mock<ProsimCompanion.Core.Aircraft.ISimClock>();
        clock.SetupGet(c => c.UtcNowOrReal).Returns(simNow);
        var h = new Harness(clock.Object);
        h.Progress.Update(_ => new FlightProgressSnapshot { Position = null, EtaUtc = simNow.AddMinutes(30) });

        Assert.Null(h.Monitor.RequestNow("web"));

        // 5400 kg, 2400 kg/h, 30 sim-minutes to go: 4200 at landing.
        var spoken = Assert.Single(h.Arbiter.Requests);
        Assert.Contains("Estimated landing fuel 4.2 tonnes", spoken.Text, StringComparison.Ordinal);
        Assert.Equal(simNow, h.CheckLog.Snapshot().Latest!.AtUtc);
    }

    [Fact]
    public void ThePeriodicInterval_StaysOnTheSuppliedClock_WhateverTheSimClockSays()
    {
        var clock = new Moq.Mock<ProsimCompanion.Core.Aircraft.ISimClock>();
        clock.SetupGet(c => c.UtcNowOrReal).Returns(new DateTimeOffset(2020, 1, 1, 3, 54, 0, TimeSpan.Zero));
        var h = new Harness(clock.Object);
        h.Phases.SetPhase(FlightPhase.Cruise);

        Assert.Null(h.Monitor.ProcessTick(T0));
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(29)));
        Assert.NotNull(h.Monitor.ProcessTick(T0.AddMinutes(30)));
    }

    [Fact]
    public void Periodic_FirstCheckOneIntervalAfterCruiseEntry_ThenEveryInterval()
    {
        var h = new Harness();
        h.Phases.SetPhase(FlightPhase.Climb);
        Assert.Null(h.Monitor.ProcessTick(T0));
        h.Phases.SetPhase(FlightPhase.Cruise);

        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(1)));            // cruise entry: the clock starts
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(30)));
        Assert.NotNull(h.Monitor.ProcessTick(T0.AddMinutes(31)));        // first check
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(45)));
        Assert.NotNull(h.Monitor.ProcessTick(T0.AddMinutes(61)));        // second
        Assert.Equal(2, h.Arbiter.Requests.Count);
        Assert.All(h.Arbiter.Requests, r => Assert.Equal("fuel.check", r.Tag));
    }

    [Fact]
    public void Periodic_StaysSilent_WhenDisabled_NotLive_OutsideTheCruise_OrIntervalZero()
    {
        var h = new Harness();
        h.Phases.SetPhase(FlightPhase.Cruise);
        h.Monitor.ProcessTick(T0);

        h.Sop.Monitoring.FuelCheck.Enabled = false;
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(31)));
        h.Sop.Monitoring.FuelCheck.Enabled = true;

        h.Phases.SetLive(false);
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(32)));
        h.Phases.SetLive(true);

        h.Sop.Monitoring.FuelCheck.IntervalMinutes = 0;
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(33)));
        h.Sop.Monitoring.FuelCheck.IntervalMinutes = 30;

        h.Phases.SetPhase(FlightPhase.Descent);
        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(34)));
        Assert.Empty(h.Arbiter.Requests);
    }

    [Fact]
    public void Periodic_WithNoPlanAndNoFuel_RecordsButDoesNotSpeak()
    {
        var h = new Harness();
        h.Refs.Values.Remove("aircraft.fuel.total.amount.kg");
        h.Phases.SetPhase(FlightPhase.Cruise);
        h.Monitor.ProcessTick(T0);

        Assert.Null(h.Monitor.ProcessTick(T0.AddMinutes(31)));
        Assert.Empty(h.Arbiter.Requests);
    }

    [Fact]
    public void Voice_FuelCheck_AnswersAtOnce_OrSaysWhyNot()
    {
        var h = new Harness();
        h.Phases.SetPhase(FlightPhase.Climb);

        Assert.True(h.Monitor.TryHandle("fuel check"));
        Assert.StartsWith("Fuel check. Past BRAVO", h.Arbiter.Requests[^1].Text, StringComparison.Ordinal);

        h.Phases.SetLive(false);
        Assert.True(h.Monitor.TryHandle("fuel check please"));
        Assert.Equal("No flight data for a fuel check.", h.Arbiter.Requests[^1].Text);

        h.Phases.SetLive(true);
        h.Ofp.Clear();
        h.Progress.Update(_ => FlightProgressSnapshot.Empty);
        Assert.True(h.Monitor.TryHandle("check the fuel"));
        Assert.Equal("I don't have the fuel plan for a fuel check.", h.Arbiter.Requests[^1].Text);

        Assert.False(h.Monitor.TryHandle("fuel pump check"));
    }
}
