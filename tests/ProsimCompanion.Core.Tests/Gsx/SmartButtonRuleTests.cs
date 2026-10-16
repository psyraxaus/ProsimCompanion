using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Pure-rule tests for the configurable "next service" smart button (2026-10-17):
/// options + pushed value + phase/cycle in, "call the next service" out — no subscriptions,
/// no service.</summary>
public sealed class SmartButtonRuleTests
{
    private static GsxOptions Options(bool enabled = true, string dataRef = "", int trigger = 0) => new()
    {
        SmartButtonEnabled = enabled,
        SmartButtonDataRef = dataRef,
        SmartButtonTriggerValue = trigger,
    };

    [Fact]
    public void DefaultPath_IntOnTheRocker_FiresWithTheIntRadSource()
    {
        var options = Options();

        Assert.True(SmartButtonRule.ShouldFire(options, 0, GsxAutomationPhase.Preparation, cycleStarted: true, cycleComplete: false));
        Assert.True(SmartButtonRule.UsesDefaultRocker(options.SmartButtonDataRef));
        Assert.Equal("INT/RAD", SmartButtonRule.Source(options.SmartButtonDataRef));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DefaultPath_OffOrRad_DoesNotFire(int value)
    {
        Assert.False(SmartButtonRule.ShouldFire(Options(), value, GsxAutomationPhase.Preparation, cycleStarted: true, cycleComplete: false));
    }

    [Fact]
    public void Disabled_NeverFires()
    {
        Assert.False(SmartButtonRule.ShouldFire(Options(enabled: false), 0, GsxAutomationPhase.Preparation, cycleStarted: true, cycleComplete: false));
        Assert.False(SmartButtonRule.ShouldFire(Options(enabled: false, dataRef: "system.switches.S_ASP_CAB_SEND", trigger: 1), 1, GsxAutomationPhase.SessionStart, cycleStarted: true, cycleComplete: false));
    }

    [Fact]
    public void CustomDataRef_MatchingValue_FiresWithTheSmartButtonSource()
    {
        var options = Options(dataRef: "system.switches.S_ASP_CAB_SEND", trigger: 1);

        Assert.True(SmartButtonRule.ShouldFire(options, 1, GsxAutomationPhase.SessionStart, cycleStarted: true, cycleComplete: false));
        Assert.False(SmartButtonRule.UsesDefaultRocker(options.SmartButtonDataRef));
        Assert.Equal("smart-button", SmartButtonRule.Source(options.SmartButtonDataRef));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(SmartButtonRule.NoValue)]
    public void CustomDataRef_OtherValue_DoesNotFire(int value)
    {
        Assert.False(SmartButtonRule.ShouldFire(Options(dataRef: "system.switches.S_ASP_CAB_SEND", trigger: 1), value, GsxAutomationPhase.Preparation, cycleStarted: true, cycleComplete: false));
    }

    [Fact]
    public void NoValueSentinel_NeverMatchesEvenWhenConfiguredAsTheTrigger()
    {
        Assert.False(SmartButtonRule.ShouldFire(Options(dataRef: "system.switches.S_ASP_CAB_SEND", trigger: SmartButtonRule.NoValue), SmartButtonRule.NoValue, GsxAutomationPhase.Preparation, cycleStarted: true, cycleComplete: false));
    }

    [Theory]
    [InlineData(GsxAutomationPhase.Preparation, false, false)]
    [InlineData(GsxAutomationPhase.Preparation, true, true)]
    [InlineData(GsxAutomationPhase.Flight, true, false)]
    [InlineData(GsxAutomationPhase.Arrival, true, false)]
    public void GroundPrepGuard_IsNotConfigurable(GsxAutomationPhase phase, bool started, bool complete)
    {
        Assert.False(SmartButtonRule.ShouldFire(Options(), 0, phase, started, complete));
    }

    [Fact]
    public void WhitespaceDataRef_ReadsAsTheDefaultRocker()
    {
        Assert.True(SmartButtonRule.UsesDefaultRocker("   "));
        Assert.True(SmartButtonRule.UsesDefaultRocker(null));
        Assert.Equal("INT/RAD", SmartButtonRule.Source(null));
    }
}
