using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The "SayIntentions copilot off" write (owner's rig 2026-11-14): the two-minute
/// re-send made the SayIntentions client play its two-note tone every two minutes.</summary>
public sealed class CopilotOffRuleTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(null)]
    public void TheFirstTickOfAFlight_SendsOnce_WhateverTheLvarReads(double? lvar)
        => Assert.Equal(CopilotOffReason.FlightStart, CopilotOffRule.Decide(false, lvar, Long));

    [Fact]
    public void WithTheCopilotOff_NothingIsEverSentAgain()
    {
        // The regression: two minutes, ten minutes, an hour — no tone.
        Assert.Equal(CopilotOffReason.None, CopilotOffRule.Decide(true, 0.0, TimeSpan.FromMinutes(2)));
        Assert.Equal(CopilotOffReason.None, CopilotOffRule.Decide(true, 0.0, Long));
        Assert.Equal(CopilotOffReason.None, CopilotOffRule.Decide(true, 0.0, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void WithTheLvarUnreadable_OnlyTheFlightStartSendRemains()
        => Assert.Equal(CopilotOffReason.None, CopilotOffRule.Decide(true, null, TimeSpan.FromHours(3)));

    [Fact]
    public void TheCopilotBackOn_IsToldOffAgain_ButNotMoreOftenThanTheGap()
    {
        Assert.Equal(CopilotOffReason.CopilotOn, CopilotOffRule.Decide(true, 1.0, CopilotOffRule.MinGap));
        Assert.Equal(CopilotOffReason.CopilotOn, CopilotOffRule.Decide(true, 1.0, Long));
        Assert.Equal(CopilotOffReason.None, CopilotOffRule.Decide(true, 1.0, TimeSpan.FromSeconds(15)));
    }
}
