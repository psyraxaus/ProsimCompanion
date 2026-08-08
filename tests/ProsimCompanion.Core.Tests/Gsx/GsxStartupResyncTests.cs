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
        bool finalSent = false)
        => new(
            turnaround, prepDone,
            new HashSet<string>(doneLvars ?? [], StringComparer.OrdinalIgnoreCase),
            fuelTarget, fob, booked, occupied, boardingStatus, prelimEdition, finalSent,
            ConfiguredServices);

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
