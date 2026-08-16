using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class NumberVerifierTests
{
    [Fact]
    public void Check_AllowsMatchingNumbers_WithinTolerance()
    {
        double[] allowed = [145, 110.3, 1013];

        var check = NumberVerifier.Check(
            "Lift-off at 145 knots, I L S one one zero decimal three 110.30, Q N H 1013.", allowed);

        Assert.True(check.Ok);
        Assert.Empty(check.Offending);
        Assert.Equal(allowed, check.Allowed);
    }

    [Fact]
    public void Check_FlagsInventedNumbers()
    {
        var check = NumberVerifier.Check("Block time 320 minutes.", [62.0]);

        Assert.False(check.Ok);
        Assert.Equal(["320"], check.Offending);
    }

    [Fact]
    public void Check_IgnoresOneAndTwoDigitTokens()
        // Runway numbers, flap settings, small counts — deliberately unchecked.
        => Assert.True(NumberVerifier.Check("Flaps 3, runway 34, 12 callouts.", []).Ok);

    [Fact]
    public void Check_DecimalTokensAreAlwaysSignificant()
        => Assert.False(NumberVerifier.Check("Fuel 4.2 tonnes.", [3.1]).Ok);

    [Fact]
    public void DescribeAllowed_FormatsAndDeduplicates()
        => Assert.Equal("145, 110.3, 62", NumberVerifier.DescribeAllowed([145, 110.3, 62, 145]));
}

public sealed class DebriefLlmTests
{
    private static DebriefFacts Facts() => DebriefFacts.Empty with
    {
        BlockMinutes = 95,
        FlightMinutes = 62,
        LiftoffIasKt = 144.6,
        TouchdownGroundSpeedKt = 128.4,
        Gates = [new GateFact("1000", 1000, "stable", null)],
        CalloutsFired = 12,
        ChecklistsCompleted = 5,
        ChecklistNames = ["Before Start", "After Landing"],
        FinalFobKg = 2340,
        FuelUsedKg = 4160,
        Abnormals = [new AbnormalFact("HYD G RSVR LO LVL", Cleared: true)],
        Origin = "YSSY",
        Destination = "YMML",
        DepartureRunway = "16R",
    };

    [Fact]
    public void SystemPrompt_VerbosityControlsTargetLength_PredecessorWording()
    {
        var brief = DebriefLlm.SystemPrompt(DebriefVerbosity.Brief);
        var full = DebriefLlm.SystemPrompt(DebriefVerbosity.Full);

        Assert.Contains("about 30 words", brief, StringComparison.Ordinal);
        Assert.Contains("about 80 words", full, StringComparison.Ordinal);
        Assert.StartsWith(
            "You are the First Officer of an Airbus A320 giving a short, friendly spoken post-flight debrief to the Captain.",
            full, StringComparison.Ordinal);
        Assert.EndsWith("End on a positive note.", full, StringComparison.Ordinal);
    }

    [Fact]
    public void FactBlock_LabelValueLines_OnlyNonEmpty()
    {
        var block = DebriefLlm.FactBlock(Facts());

        Assert.StartsWith("POST-FLIGHT FACTS:", block, StringComparison.Ordinal);
        Assert.Contains("- Origin: YSSY runway 16R", block, StringComparison.Ordinal);
        Assert.Contains("- Destination: YMML", block, StringComparison.Ordinal);
        Assert.Contains("- Block time (minutes): 95", block, StringComparison.Ordinal);
        Assert.Contains("- Lift-off speed (knots): 145", block, StringComparison.Ordinal); // rounded
        Assert.Contains("- Approach gate 1000 ft: stable", block, StringComparison.Ordinal);
        Assert.Contains("- Abnormal handled: HYD G RSVR LO LVL, cleared", block, StringComparison.Ordinal);
        Assert.Contains("- Fuel on board (tonnes): 2.3", block, StringComparison.Ordinal);
        Assert.Contains("- Fuel used (tonnes): 4.2", block, StringComparison.Ordinal);

        // Empty facts leave no line behind.
        Assert.DoesNotContain("Cabin reports", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Radio tunes", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Tech log", block, StringComparison.Ordinal);
    }

    [Fact]
    public void FactBlock_AirportNameResolver_PresentsNameWithIcao()
    {
        // Issue #70: "Name (ICAO)" when the resolver knows the airport, raw ICAO otherwise.
        var block = DebriefLlm.FactBlock(Facts(), icao => icao == "YSSY" ? "Sydney" : null);

        Assert.Contains("- Origin: Sydney (YSSY) runway 16R", block, StringComparison.Ordinal);
        Assert.Contains("- Destination: YMML", block, StringComparison.Ordinal); // resolver miss
    }

    [Fact]
    public void AllowedNumbers_MinutesRoundedSpeedsGatesCountsAndFuelTonnes()
    {
        var allowed = DebriefLlm.AllowedNumbers(Facts());

        Assert.Contains(95, allowed);     // block minutes
        Assert.Contains(62, allowed);     // flight minutes
        Assert.Contains(145, allowed);    // lift-off, rounded as presented
        Assert.Contains(128, allowed);    // touchdown, rounded
        Assert.Contains(1000, allowed);   // gate AGL
        Assert.Contains(12, allowed);     // callouts
        Assert.Contains(5, allowed);      // checklists
        Assert.Contains(2.3, allowed);    // FOB tonnes, 1 dp
        Assert.Contains(4.2, allowed);    // fuel used tonnes, 1 dp
        Assert.DoesNotContain(144.6, allowed); // the unrounded speed is never offered
        Assert.DoesNotContain(2340, allowed);  // kilograms are never offered
    }
}
