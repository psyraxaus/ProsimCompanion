using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The pure startup-resync verdict (issue #30): dataref/LVAR evidence proves services done,
/// and the never-re-call policy assumes the rest ONLY once this leg's departure flow has
/// demonstrably progressed — a restart on a fresh leg must sequence normally.
/// </summary>
public sealed class GsxStartupResyncTests
{
    private static readonly string[] ConfiguredServices =
        ["Refueling", "Catering", "Water", "Boarding"];

    private static ResyncEvidence Evidence(
        bool turnaround = false,
        bool prepDone = false,
        string[]? doneLvars = null,
        double fuelTarget = 0,
        double fob = 0,
        int booked = 0,
        int occupied = 0,
        string? boardingStatus = null,
        int prelimEdition = 0,
        bool finalSent = false,
        bool? planLoaded = true)
        => new(
            turnaround, prepDone,
            new HashSet<string>(doneLvars ?? [], StringComparer.OrdinalIgnoreCase),
            fuelTarget, fob, booked, occupied, boardingStatus, prelimEdition, finalSent,
            ConfiguredServices, planLoaded);

    [Fact]
    public void BetweenLegs_NeedsTurnaround_DeboardingDone_AndNoDepartureProgress()
    {
        // ADR-0019 §9 (issue #220): the leg-boundary fallback with no snapshot — the EHAM
        // 2026-11-01 restart 2 (turnaround LVAR, deboarding done, nothing of leg 2 run yet).
        var between = Evidence(turnaround: true, doneLvars: ["Deboarding"]);
        Assert.True(GsxStartupResync.BetweenLegs(GsxStartupResync.Assess(between), between));

        // The GPU seed is exempt from "progress" too.
        var withGpu = Evidence(turnaround: true, doneLvars: ["Deboarding", "Gpu"]);
        Assert.True(GsxStartupResync.BetweenLegs(GsxStartupResync.Assess(withGpu), withGpu));

        // Catering of the next leg already done: a leg is under way, not between legs.
        var legUnderWay = Evidence(turnaround: true, doneLvars: ["Deboarding", "Catering"]);
        Assert.False(GsxStartupResync.BetweenLegs(GsxStartupResync.Assess(legUnderWay), legUnderWay));

        // No arrival this session, or no deboarding proof: nothing is known.
        var noTurnaround = Evidence(turnaround: false, doneLvars: ["Deboarding"]);
        Assert.False(GsxStartupResync.BetweenLegs(GsxStartupResync.Assess(noTurnaround), noTurnaround));
        var noDeboarding = Evidence(turnaround: true);
        Assert.False(GsxStartupResync.BetweenLegs(GsxStartupResync.Assess(noDeboarding), noDeboarding));
    }

    [Fact]
    public void UnpushedPlanRefs_NeverCondemnValidLvars()
    {
        // Issue #30 regression (2026-08-23 09:29 restart): the assessment ran 5 s after app
        // start, before efb.simbriefPlanImported / FMS origin had pushed — FlightPlanLoaded
        // read as false-because-unknown, the LVARs were condemned as "ProSim reset?", and
        // refuelling + catering re-ran mid-boarding. Unknown (null) must not condemn.
        var verdict = GsxStartupResync.Assess(
            Evidence(doneLvars: ["Refueling", "Catering"], planLoaded: null));

        Assert.False(verdict.StaleTrackingDetected);
        Assert.Contains(verdict.SeedCompleted, seed => seed.ServiceId == "Refueling");
        Assert.Contains(verdict.SeedCompleted, seed => seed.ServiceId == "Catering");
    }

    [Fact]
    public void UnpushedFuelRef_DoesNotContradictACompletedRefuel()
    {
        var evidence = Evidence(doneLvars: ["Refueling"], fuelTarget: 8000) with
        {
            FuelOnBoardKg = null,
        };

        var verdict = GsxStartupResync.Assess(evidence);

        Assert.False(verdict.StaleTrackingDetected);
        Assert.Contains(verdict.SeedCompleted, seed => seed.ServiceId == "Refueling");
    }

    [Fact]
    public void UnpushedPaxRef_DoesNotContradictACompletedBoarding()
    {
        var evidence = Evidence(doneLvars: ["Boarding"]) with { PaxOccupied = null };

        var verdict = GsxStartupResync.Assess(evidence);

        Assert.False(verdict.StaleTrackingDetected);
        Assert.Contains(verdict.SeedCompleted, seed => seed.ServiceId == "Boarding");
    }

    [Fact]
    public void FreshSession_SeedsNothing()
    {
        var verdict = GsxStartupResync.Assess(Evidence());

        Assert.Empty(verdict.SeedCompleted);
        Assert.False(verdict.Turnaround);
        Assert.False(verdict.SeedPrepComplete);
    }

    [Fact]
    public void TurnaroundWithoutLegProgress_SeedsNothing()
    {
        // Restart right after arrival, before any leg-2 service: the turnaround flag alone
        // must NOT assume services done — leg 2 has to sequence normally.
        var verdict = GsxStartupResync.Assess(Evidence(turnaround: true));

        Assert.Empty(verdict.SeedCompleted);
        Assert.True(verdict.Turnaround);
    }

    [Fact]
    public void FuelAtTarget_ProvesRefuel_AndNothingElse()
    {
        // EHAM 2026-11-01: the fuel was loaded for leg two, catering and boarding still to come —
        // a proven refuel alone must not assume the rest done.
        var verdict = GsxStartupResync.Assess(Evidence(fuelTarget: 8000, fob: 7950));

        var ids = verdict.SeedCompleted.Select(s => s.ServiceId).ToArray();
        Assert.Equal(["Refueling"], ids);
    }

    [Fact]
    public void BoardingProven_NeverRecallAssumesTheRest()
    {
        var verdict = GsxStartupResync.Assess(Evidence(booked: 174, occupied: 174));

        var ids = verdict.SeedCompleted.Select(s => s.ServiceId).ToArray();
        Assert.Contains("Boarding", ids);
        // Never re-call: boarding is the last one-shot, so everything before it ran.
        Assert.Contains("Catering", ids);
        Assert.Contains("Water", ids);
        Assert.Contains("Refueling", ids);
        Assert.Contains("unverified", verdict.SeedCompleted.First(s => s.ServiceId == "Catering").Reason);
    }

    [Fact]
    public void FuelWellBelowTarget_DoesNotProveRefuel()
    {
        var verdict = GsxStartupResync.Assess(Evidence(fuelTarget: 8000, fob: 7000));

        Assert.Empty(verdict.SeedCompleted);
    }

    [Fact]
    public void PaxCountsProveBoarding()
    {
        var verdict = GsxStartupResync.Assess(Evidence(booked: 174, occupied: 174));

        Assert.True(verdict.BoardingProven);
        Assert.Equal(
            "174 of 174 booked pax aboard",
            verdict.SeedCompleted.First(s => s.ServiceId == "Boarding").Reason);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("ended")]
    public void EfbBoardingStatus_ProvesBoarding_WhileTheCabinIsNotEmpty(string status)
    {
        Assert.True(GsxStartupResync.Assess(Evidence(boardingStatus: status, occupied: 120)).BoardingProven);
        // No pax figure pushed yet: the status still counts (absent data never condemns).
        var noFigure = Evidence(boardingStatus: status) with { PaxOccupied = null };
        Assert.True(GsxStartupResync.Assess(noFigure).BoardingProven);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("ended")]
    public void EfbBoardingStatus_WithAnEmptyCabin_IsThePreviousLeg(string status)
    {
        // EHAM 2026-11-01: restart at the gate after the arrival, before the next plan — the
        // EFB still read the arrival leg's "completed", the cabin was empty, and leg two's
        // boarding was never called. Nothing is proven, nothing is assumed.
        var verdict = GsxStartupResync.Assess(Evidence(boardingStatus: status, booked: 129, occupied: 0));

        Assert.False(verdict.BoardingProven);
        Assert.Empty(verdict.SeedCompleted);
    }

    [Fact]
    public void AssumedServices_AreNotProven()
    {
        // The never-re-call assumption rides a PROVEN boarding; the assumed ones are marked
        // as such and must never re-arm anything on their own.
        var verdict = GsxStartupResync.Assess(Evidence(booked: 174, occupied: 174));

        Assert.True(verdict.BoardingProven);
        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Catering" && s.Reason.Contains("unverified"));
    }

    [Fact]
    public void TrackingLvars_ProveServices_AndCarryFlags()
    {
        var verdict = GsxStartupResync.Assess(Evidence(
            turnaround: true,
            prepDone: true,
            doneLvars: ["Catering", "Water"],
            prelimEdition: 2,
            finalSent: true));

        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Catering" && s.Reason.Contains("LVAR"));
        // Prelim EDNO also proves the refuel cycle ran.
        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Refueling");
        Assert.True(verdict.SeedPrepComplete);
        Assert.Equal(2, verdict.LoadsheetPrelimEdition);
        Assert.True(verdict.LoadsheetFinalSent);
    }

    [Fact]
    public void DeboardingProof_DoesNotAssumeLegTwoDepartureServices()
    {
        // Restart during leg-2 preflight, right after deboarding finished: deboarding is the
        // ARRIVAL flow — leg 2's departure services must still sequence normally.
        var verdict = GsxStartupResync.Assess(Evidence(turnaround: true, doneLvars: ["Deboarding"]));

        Assert.Single(verdict.SeedCompleted);
        Assert.Equal("Deboarding", verdict.SeedCompleted[0].ServiceId);
    }

    [Fact]
    public void GpuProof_DoesNotAssumeDepartureServices()
    {
        // The GPU connects during ground prep, before any departure service — its completion
        // alone must not skip the whole departure sequence.
        var verdict = GsxStartupResync.Assess(Evidence(doneLvars: ["GPU"], prepDone: true));

        Assert.Single(verdict.SeedCompleted);
        Assert.Equal("GPU", verdict.SeedCompleted[0].ServiceId);
        Assert.True(verdict.SeedPrepComplete);
    }

    // ── Stale-tracking hardening (issue #33): LVARs survive a ProSim reset that
    //    invalidates them — dataref corroboration decides whether to believe them. ──

    [Fact]
    public void DepartureClaimsWithoutFlightPlan_AreStale()
    {
        // ProSim reset: LVARs say catering/refuel done, but no OFP/MCDU plan exists —
        // departure services are plan-gated, so those claims are impossible for THIS flight.
        var verdict = GsxStartupResync.Assess(Evidence(
            turnaround: true, prepDone: true,
            doneLvars: ["Refueling", "Catering"], prelimEdition: 2, planLoaded: false));

        Assert.True(verdict.StaleTrackingDetected);
        Assert.Empty(verdict.SeedCompleted);
        Assert.False(verdict.Turnaround);
        Assert.False(verdict.SeedPrepComplete);
        Assert.Equal(0, verdict.LoadsheetPrelimEdition);
        Assert.False(verdict.LoadsheetFinalSent);
    }

    [Fact]
    public void RefuelClaimContradictedByLiveFuel_IsStale()
    {
        // Same route re-flown after a reset: plan loaded, LVAR says refuel done, but the
        // tanks are far below the new target — the live dataref wins.
        var verdict = GsxStartupResync.Assess(Evidence(
            doneLvars: ["Refueling", "Catering"], fuelTarget: 8000, fob: 4700));

        Assert.True(verdict.StaleTrackingDetected);
        Assert.Empty(verdict.SeedCompleted);
    }

    [Fact]
    public void BoardingClaimWithEmptyCabin_IsStale()
    {
        var verdict = GsxStartupResync.Assess(Evidence(doneLvars: ["Boarding", "Water"]));

        Assert.True(verdict.StaleTrackingDetected);
        Assert.Empty(verdict.SeedCompleted);
    }

    [Fact]
    public void CorroboratedClaims_AreNotStale()
    {
        // Legit restart mid-turnaround: plan loaded, fuel at target, cabin occupied.
        var verdict = GsxStartupResync.Assess(Evidence(
            doneLvars: ["Refueling", "Boarding"],
            fuelTarget: 8000, fob: 7980, booked: 174, occupied: 174));

        Assert.False(verdict.StaleTrackingDetected);
        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Refueling");
        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Boarding");
    }

    [Fact]
    public void DeboardingOnlyClaimWithoutPlan_StaysTrusted()
    {
        // The legit restart-right-after-arrival window: no leg-2 plan yet, only the
        // arrival-flow deboarding LVAR set — must NOT be treated as a ProSim reset, or the
        // recovered turnaround flag would be lost.
        var verdict = GsxStartupResync.Assess(Evidence(
            turnaround: true, doneLvars: ["Deboarding"], planLoaded: false));

        Assert.False(verdict.StaleTrackingDetected);
        Assert.True(verdict.Turnaround);
        Assert.Single(verdict.SeedCompleted);
        Assert.Equal("Deboarding", verdict.SeedCompleted[0].ServiceId);
    }

    [Fact]
    public void WithoutProsimData_LvarsStayTrusted()
    {
        // ProSim disconnected: every dataref reads "nothing" — that absence must not condemn
        // valid LVARs (degrade, not fail).
        var evidence = Evidence(doneLvars: ["Refueling", "Catering"], planLoaded: false)
            with { ProsimDataAvailable = false };

        var verdict = GsxStartupResync.Assess(evidence);

        Assert.False(verdict.StaleTrackingDetected);
        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Refueling");
    }

    [Fact]
    public void Toggles_AreNeverAssumed()
    {
        var evidence = Evidence(fuelTarget: 8000, fob: 8000) with
        {
            ConfiguredOneShotServices = ["Refueling", GsxServiceIds.OperateJetways],
        };

        var verdict = GsxStartupResync.Assess(evidence);

        Assert.DoesNotContain(verdict.SeedCompleted, s => s.ServiceId == GsxServiceIds.OperateJetways);
    }
}
