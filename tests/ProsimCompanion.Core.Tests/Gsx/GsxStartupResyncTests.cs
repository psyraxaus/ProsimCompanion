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
        bool planLoaded = true)
        => new(
            turnaround, prepDone,
            new HashSet<string>(doneLvars ?? [], StringComparer.OrdinalIgnoreCase),
            fuelTarget, fob, booked, occupied, boardingStatus, prelimEdition, finalSent,
            ConfiguredServices, planLoaded);

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
    public void FuelAtTarget_ProvesRefuel_AndNeverRecallAssumesTheRest()
    {
        var verdict = GsxStartupResync.Assess(Evidence(fuelTarget: 8000, fob: 7950));

        var ids = verdict.SeedCompleted.Select(s => s.ServiceId).ToArray();
        Assert.Contains("Refueling", ids);
        // Never re-call: every other configured one-shot is assumed done once progress is proven.
        Assert.Contains("Catering", ids);
        Assert.Contains("Water", ids);
        Assert.Contains("Boarding", ids);
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
    public void EfbBoardingStatus_ProvesBoarding(string status)
        => Assert.True(GsxStartupResync.Assess(Evidence(boardingStatus: status)).BoardingProven);

    [Fact]
    public void AssumedBoarding_IsNotProven()
    {
        // Refuel proof triggers the never-re-call assumption for Boarding — but an assumed
        // boarding must never re-arm the automatic final loadsheet.
        var verdict = GsxStartupResync.Assess(Evidence(fuelTarget: 8000, fob: 8000));

        Assert.Contains(verdict.SeedCompleted, s => s.ServiceId == "Boarding");
        Assert.False(verdict.BoardingProven);
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
