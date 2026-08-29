using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>The automatic prelim trigger waits for its inputs instead of failing once
/// (2026-08-29: tankering pre-skip fired seconds after a restart, CG datarefs unpopulated,
/// page showed ERROR until a manual Resend).</summary>
public sealed class PrelimTriggerPolicyTests
{
    private static PrelimReadiness Ready() => new(OfpImported: true, CgPopulated: true, GrossCgMac: 27.4, ZfwCgMac: 29.1);

    [Fact]
    public void AllInputsPresent_Generates()
    {
        var decision = PrelimTriggerPolicy.Next(Ready(), TimeSpan.Zero);

        Assert.Equal(PrelimTriggerAction.Generate, decision.Action);
    }

    [Fact]
    public void NoOfp_Waits()
    {
        var decision = PrelimTriggerPolicy.Next(Ready() with { OfpImported = false }, TimeSpan.FromMinutes(1));

        Assert.Equal(PrelimTriggerAction.Wait, decision.Action);
        Assert.Contains("OFP", decision.Reason);
    }

    [Fact]
    public void CgNotPushedYet_Waits()
    {
        var decision = PrelimTriggerPolicy.Next(Ready() with { CgPopulated = false, GrossCgMac = 0, ZfwCgMac = 0 }, TimeSpan.Zero);

        Assert.Equal(PrelimTriggerAction.Wait, decision.Action);
        Assert.Contains("CG datarefs", decision.Reason);
    }

    [Fact]
    public void ImplausibleCg_Waits_RatherThanPublishingIt()
    {
        // Populated but zero: the dataref coerced to 0.0 — the same value that used to abort
        // generation with the "implausible" error.
        var decision = PrelimTriggerPolicy.Next(Ready() with { ZfwCgMac = 0 }, TimeSpan.Zero);

        Assert.Equal(PrelimTriggerAction.Wait, decision.Action);
        Assert.Contains("plausible CG", decision.Reason);
    }

    [Fact]
    public void WaitedPastTheLimit_GivesUp_WithTheBlocker()
    {
        var decision = PrelimTriggerPolicy.Next(
            Ready() with { OfpImported = false }, PrelimTriggerPolicy.MaxWait + TimeSpan.FromSeconds(1));

        Assert.Equal(PrelimTriggerAction.GiveUp, decision.Action);
        Assert.Contains("gave up", decision.Reason);
        Assert.Contains("OFP", decision.Reason);
    }

    [Fact]
    public void ReadyAfterALongWait_StillGenerates()
    {
        // The limit only applies while blocked — a late OFP import is exactly the case.
        var decision = PrelimTriggerPolicy.Next(Ready(), PrelimTriggerPolicy.MaxWait + TimeSpan.FromMinutes(5));

        Assert.Equal(PrelimTriggerAction.Generate, decision.Action);
    }
}
