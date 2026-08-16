using ProsimCompanion.Core.AircraftState;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Pure-verdict tests for the session-start cold-and-dark check (issue #63):
/// definition + dataref snapshot in, verdict out — no timers, no services.</summary>
public sealed class AircraftStateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 7, 40, 0, TimeSpan.Zero);

    private static AircraftStateDefinition Definition() => new()
    {
        Name = "Cold and Dark",
        Groups =
        [
            new AircraftStateGroup
            {
                Name = "Electrical",
                Items =
                [
                    new AircraftStateExpectation
                    {
                        Label = "Battery 1 OFF",
                        MismatchPhrase = "battery 1 is on",
                        Verify = Leaf("system.switches.S_OH_ELEC_BAT1", 0),
                    },
                    new AircraftStateExpectation
                    {
                        Label = "Battery 2 OFF",
                        MismatchPhrase = "battery 2 is on",
                        Verify = Leaf("system.switches.S_OH_ELEC_BAT2", 0),
                    },
                ],
            },
            new AircraftStateGroup
            {
                Name = "Brakes",
                Items =
                [
                    new AircraftStateExpectation
                    {
                        Label = "Parking brake SET",
                        MismatchPhrase = "the parking brake is off",
                        Verify = Leaf("system.switches.S_MIP_PARKING_BRAKE", 1),
                    },
                    // Display furniture: no verify condition — must never gate or count.
                    new AircraftStateExpectation { Label = "Chocks in place" },
                ],
            },
        ],
    };

    private static VerifyCondition Leaf(string dataref, double expected)
        => new() { Dataref = dataref, Op = ComparisonOp.Equals, Value = expected };

    private static AircraftStateCheckContext Context(
        Dictionary<string, double>? values = null,
        bool enabled = true,
        bool announce = true,
        bool airborne = false,
        bool turnaround = false,
        FlightPhase phase = FlightPhase.ColdAndDark,
        AircraftStateDefinition? definition = null,
        bool noDefinition = false)
        => new(
            Enabled: enabled,
            AnnounceMismatches: announce,
            HasBeenAirborne: airborne,
            TurnaroundDetected: turnaround,
            Phase: phase,
            Definition: noDefinition ? null : definition ?? Definition(),
            Read: name => values is not null && values.TryGetValue(name, out var value)
                ? value
                : null);

    private static readonly Dictionary<string, double> ColdAndDarkValues = new()
    {
        ["system.switches.S_OH_ELEC_BAT1"] = 0,
        ["system.switches.S_OH_ELEC_BAT2"] = 0,
        ["system.switches.S_MIP_PARKING_BRAKE"] = 1,
    };

    [Fact]
    public void Pass_WhenEveryExpectationHolds()
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues), Now);

        Assert.Equal(AircraftStateCheckStatus.Pass, verdict.Status);
        Assert.Empty(verdict.Mismatches);
        Assert.Empty(verdict.UncheckedLabels);
        Assert.False(verdict.Announce);
        Assert.Equal(Now, verdict.Timestamp);
    }

    [Fact]
    public void Mismatch_ListsFailedItems_WithSpokenPhrases()
    {
        var values = new Dictionary<string, double>(ColdAndDarkValues)
        {
            ["system.switches.S_OH_ELEC_BAT1"] = 1,
            ["system.switches.S_MIP_PARKING_BRAKE"] = 0,
        };

        var verdict = AircraftStateCheck.Assess(Context(values), Now);

        Assert.Equal(AircraftStateCheckStatus.Mismatch, verdict.Status);
        Assert.Equal(
            ["Battery 1 OFF", "Parking brake SET"],
            verdict.Mismatches.Select(mismatch => mismatch.Label));
        Assert.Equal(
            ["battery 1 is on", "the parking brake is off"],
            verdict.Mismatches.Select(mismatch => mismatch.Phrase));
        Assert.True(verdict.Announce);
    }

    [Fact]
    public void Mismatch_WithoutPhrase_FallsBackToLabelText()
    {
        var definition = Definition();
        definition.Groups[0].Items[0].MismatchPhrase = null;
        var values = new Dictionary<string, double>(ColdAndDarkValues)
        {
            ["system.switches.S_OH_ELEC_BAT1"] = 1,
        };

        var verdict = AircraftStateCheck.Assess(Context(values, definition: definition), Now);

        Assert.Equal("Battery 1 OFF is not satisfied", Assert.Single(verdict.Mismatches).Phrase);
    }

    [Fact]
    public void Mismatch_AnnounceOff_SuppressesTheAdvisoryFlagOnly()
    {
        var values = new Dictionary<string, double>(ColdAndDarkValues)
        {
            ["system.switches.S_OH_ELEC_BAT1"] = 1,
        };

        var verdict = AircraftStateCheck.Assess(Context(values, announce: false), Now);

        Assert.Equal(AircraftStateCheckStatus.Mismatch, verdict.Status);
        Assert.False(verdict.Announce);
    }

    [Fact]
    public void MissingDataref_SkipsThatItem_NotesIt_AndStillJudgesTheRest()
    {
        // Battery 2's dataref never reported — its item must be listed unchecked, never
        // fail-closed into a false mismatch, while the others still evaluate.
        var values = new Dictionary<string, double>(ColdAndDarkValues);
        values.Remove("system.switches.S_OH_ELEC_BAT2");
        values["system.switches.S_MIP_PARKING_BRAKE"] = 0;

        var verdict = AircraftStateCheck.Assess(Context(values), Now);

        Assert.Equal(AircraftStateCheckStatus.Mismatch, verdict.Status);
        Assert.Equal("Battery 2 OFF", Assert.Single(verdict.UncheckedLabels));
        Assert.Equal("Parking brake SET", Assert.Single(verdict.Mismatches).Label);
    }

    [Fact]
    public void MissingDataref_WithEverythingElsePassing_IsStillAPass()
    {
        var values = new Dictionary<string, double>(ColdAndDarkValues);
        values.Remove("system.switches.S_OH_ELEC_BAT2");

        var verdict = AircraftStateCheck.Assess(Context(values), Now);

        Assert.Equal(AircraftStateCheckStatus.Pass, verdict.Status);
        Assert.Equal("Battery 2 OFF", Assert.Single(verdict.UncheckedLabels));
    }

    [Fact]
    public void NoDatarefsAvailable_SkipsHonestly_NeverAHollowPass()
    {
        var verdict = AircraftStateCheck.Assess(Context(values: new Dictionary<string, double>()), Now);

        Assert.Equal(AircraftStateCheckStatus.Skipped, verdict.Status);
        Assert.Contains("unavailable", verdict.Reason);
        Assert.False(verdict.Announce);
    }

    [Fact]
    public void Disabled_Skips()
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues, enabled: false), Now);

        Assert.Equal(AircraftStateCheckStatus.Skipped, verdict.Status);
        Assert.Equal("check disabled", verdict.Reason);
    }

    [Fact]
    public void AirborneThisSession_Skips()
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues, airborne: true), Now);

        Assert.Equal(AircraftStateCheckStatus.Skipped, verdict.Status);
        Assert.Contains("airborne", verdict.Reason);
    }

    [Fact]
    public void Turnaround_Skips_AircraftIsLegitimatelyPowered()
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues, turnaround: true), Now);

        Assert.Equal(AircraftStateCheckStatus.Skipped, verdict.Status);
        Assert.Contains("turnaround", verdict.Reason);
        Assert.False(verdict.Announce);
    }

    [Fact]
    public void NoDefinition_Skips()
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues, noDefinition: true), Now);

        Assert.Equal(AircraftStateCheckStatus.Skipped, verdict.Status);
        Assert.Contains("definition", verdict.Reason);
    }

    [Theory]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.TaxiIn)]
    [InlineData(FlightPhase.Unknown)]
    public void NonPredeparturePhase_Skips(FlightPhase phase)
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues, phase: phase), Now);

        Assert.Equal(AircraftStateCheckStatus.Skipped, verdict.Status);
        Assert.Contains(phase.ToString(), verdict.Reason);
    }

    [Theory]
    [InlineData(FlightPhase.ColdAndDark)]
    [InlineData(FlightPhase.Preflight)]
    public void PredeparturePhases_Run(FlightPhase phase)
    {
        var verdict = AircraftStateCheck.Assess(Context(ColdAndDarkValues, phase: phase), Now);

        Assert.Equal(AircraftStateCheckStatus.Pass, verdict.Status);
    }

    [Fact]
    public void CompoundCondition_MissingOneLeafDataref_ListsTheItemUnchecked()
    {
        var definition = new AircraftStateDefinition
        {
            Groups =
            [
                new AircraftStateGroup
                {
                    Items =
                    [
                        new AircraftStateExpectation
                        {
                            Label = "ADIRS selectors OFF",
                            Verify = new VerifyCondition
                            {
                                Logic = ConditionLogic.And,
                                Conditions =
                                [
                                    Leaf("system.switches.S_OH_NAV_IR1_MODE", 0),
                                    Leaf("system.switches.S_OH_NAV_IR2_MODE", 0),
                                ],
                            },
                        },
                        new AircraftStateExpectation
                        {
                            Label = "Beacon OFF",
                            Verify = Leaf("system.switches.S_OH_EXT_LT_BEACON", 0),
                        },
                    ],
                },
            ],
        };
        var values = new Dictionary<string, double>
        {
            ["system.switches.S_OH_NAV_IR1_MODE"] = 0,
            // IR2 never reported.
            ["system.switches.S_OH_EXT_LT_BEACON"] = 0,
        };

        var verdict = AircraftStateCheck.Assess(Context(values, definition: definition), Now);

        Assert.Equal(AircraftStateCheckStatus.Pass, verdict.Status);
        Assert.Equal("ADIRS selectors OFF", Assert.Single(verdict.UncheckedLabels));
    }
}
