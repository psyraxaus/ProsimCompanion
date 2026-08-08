using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Checklists;
using Xunit;

namespace ProsimCompanion.Core.Tests.Checklists;

public sealed class Prosim2GsxChecklistSetLoaderTests
{
    /// <summary>A representative slice of Prosim2GSX's a320_default.json, kept in the NATIVE
    /// shape (PascalCase, condition strings) — the loader must consume the original files.</summary>
    private const string SampleJson = """
        {
          "_README": [ "authoring notes are ignored" ],
          "Name": "A320 Default",
          "AircraftType": "A320",
          "Sections": [
            {
              "Title": "PRE START",
              "Items": [
                { "Label": "PARKING BRAKE", "Value": "SET", "DataRef": "system.switches.S_MIP_PARKING_BRAKE", "DataRefCondition": "== 1" },
                { "Label": "CHOCKS", "Value": "REMOVED", "DataRef": null },
                { "Label": "THROTTLE", "Value": "IDLE",
                  "DataRefs": [
                    { "DataRef": "aircraft.flightControls.throttle.1.lever", "Condition": "<= 0.05" },
                    { "DataRef": "aircraft.flightControls.throttle.2.lever", "Condition": "<= 0.05" }
                  ] },
                { "Label": "EXT POWER", "Value": "ON", "DataRef": "system.switches.S_OH_ELEC_EXT_PWR", "Momentary": true, "SteadyDataRef": "system.indicators.I_OH_ELEC_EXT_PWR_L", "DataRefCondition": "!= 0" },
                { "IsNote": true, "Label": "Check Weather (ATIS, Flight Services)" },
                { "IsSeparator": true },
                { "Label": "APU", "Value": "START", "DataRef": "system.gates.B_APU_RUNNING", "DataRefCondition": "== true" },
                { "Label": "PROBE HEAT", "Value": "AUTO", "DataRef": "system.switches.S_OH_PROBE_HEAT", "DataRefCondition": ">= 0" }
              ]
            },
            {
              "Title": "STARTUP",
              "Items": [
                { "Label": "MODE SELECTOR", "Value": "NORM", "DataRef": "system.switches.S_ENG_MODE", "DataRefCondition": "== 1" }
              ]
            }
          ]
        }
        """;

    private static ChecklistSet ParseSample(string json = SampleJson, string fallback = "file-name")
    {
        var set = Prosim2GsxChecklistSetLoader.Parse(json, fallback, NullLogger.Instance);
        Assert.NotNull(set);
        return set;
    }

    [Fact]
    public void Parse_SectionsBecomeChecklists_InFileOrder()
    {
        var set = ParseSample();

        Assert.Equal("A320 Default", set.Name);
        Assert.Equal(["PRE START", "STARTUP"], set.Definitions.Select(d => d.Checklist));
        Assert.Equal([1, 2], set.Definitions.Select(d => d.Order));
    }

    [Fact]
    public void Parse_SetNameFallsBackToFilename_WhenFileHasNoName()
    {
        var set = ParseSample("""{ "Sections": [ { "Title": "A", "Items": [] } ] }""");
        Assert.Equal("file-name", set.Name);
    }

    [Fact]
    public void Parse_NoSections_ReturnsNull()
    {
        Assert.Null(Prosim2GsxChecklistSetLoader.Parse("""{ "Name": "x" }""", "f", NullLogger.Instance));
    }

    [Fact]
    public void Parse_PlainItem_MapsLabelAndValueOntoSayAndResponse()
    {
        var chocks = ParseSample().Definitions[0].Items[1];

        Assert.Equal("CHOCKS", chocks.Say);
        Assert.Equal("REMOVED", chocks.ExpectedResponse);
        Assert.Equal("acknowledge", chocks.Behavior);
        Assert.False(chocks.IsAuto); // DataRef: null is the P2GSX manual-item idiom
    }

    [Fact]
    public void Parse_SingleCondition_MapsToEqualsLeaf()
    {
        var brake = ParseSample().Definitions[0].Items[0];

        Assert.True(brake.IsAuto);
        Assert.Equal("system.switches.S_MIP_PARKING_BRAKE", brake.Verify!.Dataref);
        Assert.Equal(ComparisonOp.Equals, brake.Verify.Op);
        Assert.Equal(1, brake.Verify.Value);
    }

    [Fact]
    public void Parse_BooleanOperand_MapsTrueToOne()
    {
        var apu = ParseSample().Definitions[0].Items[6];

        Assert.Equal(ComparisonOp.Equals, apu.Verify!.Op);
        Assert.Equal(1, apu.Verify.Value);
        Assert.True(ConditionEvaluator.Evaluate(apu.Verify, _ => 1.0)); // booleans read 1/0
        Assert.False(ConditionEvaluator.Evaluate(apu.Verify, _ => 0.0));
    }

    [Fact]
    public void Parse_CompoundDataRefs_BecomeAnAndTree()
    {
        var throttle = ParseSample().Definitions[0].Items[2];
        var verify = throttle.Verify!;

        Assert.True(verify.IsCompound);
        Assert.Equal(ConditionLogic.And, verify.Logic);
        Assert.Equal(2, verify.Conditions!.Count);
        // "<= 0.05" has no direct op — the one-sided inclusive Between is the exact mapping.
        Assert.All(verify.Conditions, leaf =>
        {
            Assert.Equal(ComparisonOp.Between, leaf.Op);
            Assert.Equal(0.05, leaf.High);
            Assert.Null(leaf.Low);
        });
        Assert.True(ConditionEvaluator.Evaluate(verify, _ => 0.05));  // inclusive boundary
        Assert.False(ConditionEvaluator.Evaluate(verify, _ => 0.06));
    }

    [Fact]
    public void Parse_GreaterOrEqual_MapsToOneSidedBetween()
    {
        var probe = ParseSample().Definitions[0].Items[7];

        Assert.Equal(ComparisonOp.Between, probe.Verify!.Op);
        Assert.Equal(0, probe.Verify.Low);
        Assert.Null(probe.Verify.High);
        Assert.True(ConditionEvaluator.Evaluate(probe.Verify, _ => 0.0)); // inclusive boundary
    }

    [Fact]
    public void Parse_SteadyDataRef_ReplacesTheDocumentedDataRef()
    {
        // Momentary switch: the JSON documents the switch, the worker polls the LED.
        var extPower = ParseSample().Definitions[0].Items[3];

        Assert.Equal("system.indicators.I_OH_ELEC_EXT_PWR_L", extPower.Verify!.Dataref);
        Assert.Equal(ComparisonOp.NotEquals, extPower.Verify.Op);
        Assert.Equal(0, extPower.Verify.Value);
    }

    [Fact]
    public void Parse_NotesAndSeparators_GetTheirKind_AndStayManualless()
    {
        var items = ParseSample().Definitions[0].Items;

        Assert.Equal("note", items[4].Kind);
        Assert.Equal("Check Weather (ATIS, Flight Services)", items[4].Say);
        Assert.Equal("separator", items[5].Kind);
        Assert.True(items[4].IsDisplayOnly);
        Assert.True(items[5].IsDisplayOnly);
        Assert.Null(items[4].Verify);
    }

    [Theory]
    [InlineData("bogus 1")]   // no operator prefix
    [InlineData("== maybe")]  // unparseable operand
    [InlineData(">= true")]   // boolean only compares for (in)equality
    public void Parse_UnrepresentableCondition_FallsBackToManualAcknowledge(string condition)
    {
        var json = $$"""
            {
              "Name": "S",
              "Sections": [ { "Title": "T", "Items": [
                { "Label": "X", "Value": "SET", "DataRef": "some.dataref", "DataRefCondition": "{{condition}}" }
              ] } ]
            }
            """;

        var item = ParseSample(json).Definitions[0].Items[0];

        Assert.Null(item.Verify);
        Assert.False(item.IsAuto);
        Assert.Equal("acknowledge", item.Behavior);
    }

    [Fact]
    public void Parse_CompoundWithOneUnmappableLeaf_DemotesTheWholeItemToManual()
    {
        // Dropping the bad AND leaf would let the item auto-complete on a partial truth.
        var json = """
            {
              "Name": "S",
              "Sections": [ { "Title": "T", "Items": [
                { "Label": "X", "Value": "SET",
                  "DataRefs": [
                    { "DataRef": "a", "Condition": "== 1" },
                    { "DataRef": "b", "Condition": "nonsense" }
                  ] }
              ] } ]
            }
            """;

        Assert.Null(ParseSample(json).Definitions[0].Items[0].Verify);
    }
}

public sealed class ChecklistRunnerDisplayOnlyTests
{
    private static ChecklistItemDefinition Manual(string say)
        => new() { Behavior = "acknowledge", Say = say };

    private static ChecklistItemDefinition Note(string say)
        => new() { Kind = "note", Say = say };

    private static ChecklistItemDefinition Separator()
        => new() { Kind = "separator" };

    private static ChecklistDefinition Definition(params ChecklistItemDefinition[] items)
        => new() { Checklist = "Test", Items = [.. items] };

    [Fact]
    public void DisplayOnlyRows_AutoCompleteInstantly_AndNeverGate()
    {
        var runner = new ChecklistRunner(Definition(Note("weather"), Manual("brakes"), Separator(), Manual("doors")));
        runner.Evaluate(_ => 0.0);

        var view = runner.Snapshot();
        Assert.Equal(ChecklistItemStatus.Done, view.Items[0].Status);   // note completed itself
        Assert.Equal(ChecklistItemStatus.Active, view.Items[1].Status); // gating landed on the real item

        runner.Check(1);
        runner.Evaluate(_ => 0.0);
        view = runner.Snapshot();
        Assert.Equal(ChecklistItemStatus.Done, view.Items[2].Status);   // separator cascaded through
        Assert.Equal(ChecklistItemStatus.Active, view.Items[3].Status);

        runner.Check(3);
        runner.Evaluate(_ => 0.0);
        Assert.True(runner.IsComplete); // display rows never block completion
    }

    [Fact]
    public void Snapshot_CarriesKindThrough_ForTheWebPage()
    {
        var runner = new ChecklistRunner(Definition(Note("weather"), Separator(), Manual("doors")));

        var view = runner.Snapshot();
        Assert.Equal("note", view.Items[0].Kind);
        Assert.Equal("separator", view.Items[1].Kind);
        Assert.Equal("normal", view.Items[2].Kind);
    }
}

public sealed class ChecklistRunnerManualOverrideTests
{
    private static ChecklistItemDefinition Auto(string say, string dataref, double expected)
        => new()
        {
            Behavior = "verify",
            Say = say,
            Verify = new VerifyCondition { Dataref = dataref, Op = ComparisonOp.Equals, Value = expected },
        };

    private static ChecklistItemDefinition Manual(string say)
        => new() { Behavior = "acknowledge", Say = say };

    private static ChecklistDefinition Definition(params ChecklistItemDefinition[] items)
        => new() { Checklist = "Test", Items = [.. items] };

    [Fact]
    public void OverrideOff_CheckStillRefusesAutoItems()
    {
        // The service routes Check() to runner.Check when checklists.allowManualOverride is
        // off — the strict predecessor rule stays the default.
        var runner = new ChecklistRunner(Definition(Auto("beacon", "beacon", 1)));
        runner.Evaluate(_ => 0.0);

        Assert.False(runner.Check(0));
        Assert.Equal(ChecklistItemStatus.Active, runner.Snapshot().Items[0].Status);
    }

    [Fact]
    public void OverrideOn_ForceCheckCompletesTheAutoItem_AndFreezesAgainstRetreat()
    {
        // The service routes Check() to ForceCheck when the option is on: the tick lands even
        // though the condition reads unsatisfied, and the retreat pass must not un-tick it.
        var runner = new ChecklistRunner(Definition(Auto("beacon", "beacon", 1), Manual("doors")));
        runner.Evaluate(_ => 0.0);

        Assert.True(runner.ForceCheck(0));
        runner.Evaluate(_ => 0.0); // retreat pass runs with the condition still unsatisfied

        var view = runner.Snapshot();
        Assert.Equal(ChecklistItemStatus.Done, view.Items[0].Status);
        Assert.Equal(ChecklistItemStatus.Active, view.Items[1].Status);
    }

    [Fact]
    public void ForceCheck_IsStillActiveLineGated()
    {
        // The override relaxes WHO may tick, not the in-order discipline.
        var runner = new ChecklistRunner(Definition(Manual("first"), Auto("beacon", "beacon", 1)));
        runner.Evaluate(_ => 0.0);

        Assert.False(runner.ForceCheck(1)); // not the active line
        Assert.Equal(ChecklistItemStatus.Pending, runner.Snapshot().Items[1].Status);
    }
}
