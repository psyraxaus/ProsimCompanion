using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #67: the engine-start/flap call decision core. Verbal only — nothing here may ever
/// write a dataref. Placards use the flap HANDLE scale (0=Up 1=F1 2=1+F 3=F2 4=F3 5=F4), the
/// same keying CalloutsEngine.Placards reads, so "flaps two" checks handle 3.
/// </summary>
public sealed class EngineFlapCallDeciderTests
{
    private static readonly List<FlapPlacard> DefaultPlacards = [.. SopOptions.DefaultFlapPlacards];

    private static FlightDataSnapshot Snapshot(
        double ias = 0, string? eng1 = "off", string? eng2 = "off")
        => new() { IsValid = true, IndicatedAirspeedKt = ias, RawEngine1State = eng1, RawEngine2State = eng2 };

    // ---- Engine starts ----

    [Theory]
    [InlineData("starting engine one", "Engine one.")]
    [InlineData("start engine two", "Engine two.")]
    [InlineData("engine two start", "Engine two.")]
    public void EngineStart_EngineOff_BriefAcknowledgement(string call, string expected)
        => Assert.Equal(expected, EngineFlapCallDecider.Decide(call, Snapshot(), DefaultPlacards));

    [Fact]
    public void EngineStart_EngineAlreadyRunning_GentleCorrection()
        => Assert.Equal(
            "Engine two is already running.",
            EngineFlapCallDecider.Decide("starting engine two", Snapshot(eng2: "running"), DefaultPlacards));

    [Fact]
    public void EngineStart_MissingStateString_ReadsAsNotRunning()
        // Degrade, not fail: no dataref must never produce a wrong "already running".
        => Assert.Equal(
            "Engine one.",
            EngineFlapCallDecider.Decide("starting engine one", Snapshot(eng1: null), DefaultPlacards));

    // ---- Flap calls: placard check against the REQUESTED handle ----

    [Fact]
    public void FlapsTwo_BelowPlacard_SpeedChecked()
        // "two" is HANDLE 3 (F2, 185 kt default) — handle 2 is the 1+F config.
        => Assert.Equal(
            "Speed checked, flaps two.",
            EngineFlapCallDecider.Decide("flaps two", Snapshot(ias: 180), DefaultPlacards));

    [Fact]
    public void FlapsTwo_AbovePlacard_NegativeWithBothSpeeds()
        => Assert.Equal(
            "Negative — speed 240, flaps two limit is 185.",
            EngineFlapCallDecider.Decide("flaps two", Snapshot(ias: 240), DefaultPlacards));

    [Fact]
    public void FlapsOne_ChecksHandleOne()
        => Assert.Equal(
            "Negative — speed 231, flaps one limit is 230.",
            EngineFlapCallDecider.Decide("flaps one", Snapshot(ias: 231), DefaultPlacards));

    [Fact]
    public void FlapsThree_ChecksHandleFour_BoundaryIsInclusive()
        // Exactly AT the placard is not an exceedance (same comparison the reactive
        // placard advisory uses).
        => Assert.Equal(
            "Speed checked, flaps three.",
            EngineFlapCallDecider.Decide("flaps three", Snapshot(ias: 177), DefaultPlacards));

    [Fact]
    public void FlapsFull_ChecksHandleFive()
        => Assert.Equal(
            "Speed checked, flaps full.",
            EngineFlapCallDecider.Decide("flaps full", Snapshot(ias: 170), DefaultPlacards));

    [Theory]
    [InlineData("flaps up")]
    [InlineData("flaps zero")]
    public void Retraction_PlainAcknowledgement_NoSpeedCheck(string call)
        => Assert.Equal("Flaps up.", EngineFlapCallDecider.Decide(call, Snapshot(ias: 400), DefaultPlacards));

    [Fact]
    public void FlapCall_PlacardMissingFromUserEditedTable_AcknowledgesWithoutClaimingACheck()
        => Assert.Equal(
            "Flaps two.",
            EngineFlapCallDecider.Decide("flaps two", Snapshot(ias: 240), []));

    [Theory]
    [InlineData("flaps forty")] // not an Airbus call
    [InlineData("gear down")]
    [InlineData("starting engine three")]
    public void UnrelatedUtterances_AreNotClaimed(string call)
        => Assert.Null(EngineFlapCallDecider.Decide(call, Snapshot(), DefaultPlacards));
}

public sealed class EngineFlapCallFeatureTests
{
    private static EngineFlapCallFeature Feature(
        FakeArbiter arbiter, FakeFlightSource source, bool enabled = true)
        => new(
            SpeechTestSupport.SpeechMonitor(new SpeechOptions { EngineFlapCallouts = enabled }),
            SpeechTestSupport.SopMonitor(new SopOptions()),
            source,
            arbiter,
            SpeechTestSupport.TempEventLog(),
            NullLogger<EngineFlapCallFeature>.Instance);

    [Fact]
    public void Disabled_ContributesNoPhrases_AndClaimsNothing()
    {
        var arbiter = new FakeArbiter();
        var feature = Feature(arbiter, new FakeFlightSource(), enabled: false);

        Assert.Empty(feature.Phrases);
        Assert.False(feature.TryHandle("flaps two"));
        Assert.Empty(arbiter.Requests);
    }

    [Fact]
    public void Enabled_PhrasesReachTheGrammar_AndTheResponseIsTagged()
    {
        var arbiter = new FakeArbiter();
        var source = new FakeFlightSource
        {
            Snapshot = new FlightDataSnapshot { IsValid = true, IndicatedAirspeedKt = 180 },
        };
        var feature = Feature(arbiter, source);

        Assert.Contains("flaps two", feature.Phrases);
        Assert.Contains("starting engine one", feature.Phrases);
        Assert.True(feature.TryHandle("Flaps two"));

        var request = Assert.Single(arbiter.Requests);
        Assert.Equal("Speed checked, flaps two.", request.Text);
        Assert.Equal("crewCall", request.Tag); // every spoken line carries a tag (issue #66)
    }

    [Fact]
    public void NonMatchingUtterance_FallsThroughToOtherFeatures()
    {
        var arbiter = new FakeArbiter();
        var feature = Feature(arbiter, new FakeFlightSource());

        Assert.False(feature.TryHandle("descend flight level one two zero"));
        Assert.Empty(arbiter.Requests);
    }
}
