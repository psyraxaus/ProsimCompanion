using ProsimCompanion.Speech.Abnormals;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #123 (owner report 2026-11-01): a ProSim configuration load or a fire test
/// flips many systems at once on the ground — held, never announced if it clears.</summary>
public sealed class AbnormalBurstGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 17, 41, TimeSpan.Zero);

    [Fact]
    public void TheConfigurationLoad_IsHeldForTenSecondsFromTheFirstRise()
    {
        // The 2026-11-01 signature: seven triggers within two seconds at the gate, engines off.
        var rises = new (string, DateTimeOffset?)[]
        {
            ("eng-1-fire", T0), ("eng-2-fire", T0), ("elec-apu-gen-fault", T0.AddSeconds(1)),
            ("nav-ir-1-fault", T0.AddSeconds(1)), ("nav-ir-2-fault", T0.AddSeconds(1)), ("nav-ir-3-fault", T0.AddSeconds(1)),
            ("apu-fire", T0.AddSeconds(2)), ("hyd-g-sys-lo-pr", null),
        };

        Assert.Equal(T0.AddSeconds(10), AbnormalBurstGuard.HoldUntil(rises, onGround: true, anyEngineRunning: false));
    }

    [Fact]
    public void TwoFireLoops_AreABurst_OnTheirOwn()
    {
        var rises = new (string, DateTimeOffset?)[] { ("eng-1-fire", T0), ("eng-2-fire", T0.AddSeconds(1.5)) };
        Assert.Equal(T0.AddSeconds(10), AbnormalBurstGuard.HoldUntil(rises, true, false));
    }

    [Fact]
    public void ASingleFault_OrTwoOrdinaryOnes_IsNotABurst()
    {
        Assert.Null(AbnormalBurstGuard.HoldUntil([("elec-gen-1-fault", T0)], true, false));
        Assert.Null(AbnormalBurstGuard.HoldUntil([("elec-gen-1-fault", T0), ("hyd-g-sys-lo-pr", T0.AddSeconds(1))], true, false));
    }

    [Fact]
    public void RisesFarApart_AreNotABurst()
    {
        var rises = new (string, DateTimeOffset?)[] { ("a", T0), ("b", T0.AddSeconds(5)), ("c", T0.AddSeconds(10)) };
        Assert.Null(AbnormalBurstGuard.HoldUntil(rises, true, false));
    }

    [Fact]
    public void Airborne_OrWithAnEngineRunning_NothingIsHeld()
    {
        var rises = new (string, DateTimeOffset?)[] { ("eng-1-fire", T0), ("eng-2-fire", T0), ("apu-fire", T0) };
        Assert.Null(AbnormalBurstGuard.HoldUntil(rises, onGround: false, anyEngineRunning: false));
        Assert.Null(AbnormalBurstGuard.HoldUntil(rises, onGround: true, anyEngineRunning: true));
    }
}
