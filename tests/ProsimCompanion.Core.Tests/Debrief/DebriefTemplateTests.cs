using ProsimCompanion.Core.Debrief;
using Xunit;

namespace ProsimCompanion.Core.Tests.Debrief;

public sealed class DebriefTemplateTests
{
    private static DebriefFacts FullFacts() => new(
        BlockMinutes: 60,
        FlightMinutes: 41,
        LiftoffIasKt: 152.4,
        TouchdownGroundSpeedKt: 128.6,
        Gates:
        [
            new GateFact("1000", 1000, "unstable", "sink"),
            new GateFact("500", 500, "stable", null),
        ],
        CalloutsFired: 12,
        SpeechSuppressed: 1,
        ChecklistsCompleted: 4,
        ChecklistNames: ["after takeoff"],
        StartFobKg: 8200,
        FinalFobKg: 6700,
        FuelUsedKg: 1500,
        Advisories: ["landingLights", "seatbelts"],
        Degradations: 2,
        Abnormals: [new AbnormalFact("APU FAULT", Cleared: true), new AbnormalFact("PACK 1 FAULT", Cleared: false)],
        Destination: "YMML",
        DefectsRaised: 1,
        DefectsRectified: 2,
        RadioTunes: 3,
        MemoryDrills: 1);

    [Fact]
    public void Build_FullVerbosity_ExactOrderAndWording()
    {
        Assert.Equal(
            "Debrief. Block time 60 minutes. Airborne 41 minutes. Lift-off at 152 knots. "
            + "Handled APU FAULT. Handled PACK 1 FAULT, not fully cleared. "
            + "Approach unstable at the 1000 foot gate, sink. Touchdown 129 knots. "
            + "4 checklists complete. One defect entered in the tech log. 2 defects rectified. "
            + "12 callouts made. 2 advisories raised. 3 radio tunes handled. "
            + "1 memory-item drills called. 2 system notes. "
            + "Fuel on board 6.7 tonnes. About 1.5 tonnes used. Good flight.",
            DebriefTemplate.Build(FullFacts(), DebriefVerbosity.Full));
    }

    [Fact]
    public void Build_BriefVerbosity_OmitsCountsAndFuelUsed()
    {
        var text = DebriefTemplate.Build(FullFacts(), DebriefVerbosity.Brief);

        Assert.Contains("Block time 60 minutes.", text, StringComparison.Ordinal);
        Assert.Contains("Fuel on board 6.7 tonnes.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("callouts made", text, StringComparison.Ordinal);
        Assert.DoesNotContain("advisories raised", text, StringComparison.Ordinal);
        Assert.DoesNotContain("radio tunes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("system notes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tonnes used", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EmptyFacts_HeaderAndSignOffOnly()
    {
        Assert.Equal("Debrief. Good flight.", DebriefTemplate.Build(DebriefFacts.Empty, DebriefVerbosity.Full));
    }

    [Fact]
    public void Build_StableApproach_WhenNoUnstableGate()
    {
        var facts = DebriefFacts.Empty with
        {
            Gates = [new GateFact("500", 500, "stable", null)],
        };
        Assert.Contains("Approach stabilized at the 500 foot gate.",
            DebriefTemplate.Build(facts, DebriefVerbosity.Full), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IndeterminateOnlyApproach_IsSilent()
    {
        var facts = DebriefFacts.Empty with
        {
            Gates = [new GateFact("500", 500, "indeterminate", null)],
        };
        Assert.DoesNotContain("Approach",
            DebriefTemplate.Build(facts, DebriefVerbosity.Full), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnstableGateWithoutCriterion_EndsCleanly()
    {
        var facts = DebriefFacts.Empty with
        {
            Gates = [new GateFact("1000", 1000, "unstable", null)],
        };
        Assert.Contains("Approach unstable at the 1000 foot gate.",
            DebriefTemplate.Build(facts, DebriefVerbosity.Full), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SingularDefectLines()
    {
        var facts = DebriefFacts.Empty with { DefectsRaised = 1, DefectsRectified = 1 };
        var text = DebriefTemplate.Build(facts, DebriefVerbosity.Full);
        Assert.Contains("One defect entered in the tech log.", text, StringComparison.Ordinal);
        Assert.Contains("One defect rectified.", text, StringComparison.Ordinal);
    }
}
