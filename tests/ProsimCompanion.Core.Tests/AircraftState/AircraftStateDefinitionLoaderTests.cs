using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.AircraftState;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.AircraftState;

/// <summary>Round-trip + failure-surfacing tests for the cold-and-dark definition loader
/// (issue #63). Uses real temp files: the loader's contract includes File.Exists handling.</summary>
public sealed class AircraftStateDefinitionLoaderTests : IDisposable
{
    private const string ValidJson = """
        {
          // comments and trailing commas are tolerated, exactly like checklists
          "name": "Cold and Dark",
          "groups": [
            {
              "name": "Electrical",
              "items": [
                {
                  "label": "Battery 1 OFF",
                  "note": "value map documented here",
                  "mismatchPhrase": "battery 1 is on",
                  "verify": { "dataref": "system.switches.S_OH_ELEC_BAT1", "op": "equals", "value": 0 },
                },
              ],
            },
            {
              "name": "ADIRS",
              "items": [
                {
                  "label": "ADIRS selectors OFF",
                  "verify": {
                    "logic": "and",
                    "conditions": [
                      { "dataref": "system.switches.S_OH_NAV_IR1_MODE", "op": "equals", "value": 0 },
                      { "dataref": "system.switches.S_OH_NAV_IR2_MODE", "op": "oneOf", "values": [ 0 ] }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "psc-aircraft-state-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    private string WriteFile(string content, string name = "cold-and-dark.json")
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ValidFile_RoundTrips_GroupsItemsAndConditionTree()
    {
        var definition = AircraftStateDefinitionLoader.TryLoad(
            WriteFile(ValidJson), NullLogger.Instance);

        Assert.NotNull(definition);
        Assert.Equal("Cold and Dark", definition.Name);
        Assert.Equal(["Electrical", "ADIRS"], definition.Groups.Select(group => group.Name));

        var battery = definition.Groups[0].Items[0];
        Assert.Equal("Battery 1 OFF", battery.Label);
        Assert.Equal("battery 1 is on", battery.MismatchPhrase);
        Assert.NotNull(battery.Note);
        Assert.Equal(ComparisonOp.Equals, battery.Verify!.Op);
        Assert.Equal(0, battery.Verify.Value);

        var adirs = definition.Groups[1].Items[0];
        Assert.True(adirs.Verify!.IsCompound);
        Assert.Equal(
            ["system.switches.S_OH_NAV_IR1_MODE", "system.switches.S_OH_NAV_IR2_MODE"],
            adirs.Verify.ReferencedDatarefs());
    }

    [Fact]
    public void MissingFile_ReturnsNull_WithoutReportingAProblem()
    {
        var problems = new ConfigProblemStore();

        var definition = AircraftStateDefinitionLoader.TryLoad(
            Path.Combine(_directory, "does-not-exist.json"), NullLogger.Instance, problems);

        Assert.Null(definition);
        Assert.Empty(problems.Snapshot());
    }

    [Fact]
    public void MalformedFile_ReturnsNull_AndReportsToTheProblemStore()
    {
        var problems = new ConfigProblemStore();
        var path = WriteFile("{ \"groups\": [ { \"items\": [ oops ] } ] }");

        var definition = AircraftStateDefinitionLoader.TryLoad(path, NullLogger.Instance, problems);

        Assert.Null(definition);
        var problem = Assert.Single(problems.Snapshot());
        Assert.Equal(ConfigAreas.AircraftStates, problem.Area);
        Assert.Equal("cold-and-dark.json", problem.SourceFile);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    [Fact]
    public void FileWithNoVerifiableItems_ReturnsNull_AndReports()
    {
        var problems = new ConfigProblemStore();
        var path = WriteFile("""{ "name": "Empty", "groups": [ { "items": [ { "label": "note only" } ] } ] }""");

        var definition = AircraftStateDefinitionLoader.TryLoad(path, NullLogger.Instance, problems);

        Assert.Null(definition);
        var problem = Assert.Single(problems.Snapshot());
        Assert.Contains("verify", problem.Message);
    }

    [Fact]
    public void SuccessfulLoad_ClearsEarlierProblemsInTheArea()
    {
        var problems = new ConfigProblemStore();
        problems.Report(ConfigAreas.AircraftStates, "cold-and-dark.json", "old parse failure");

        var definition = AircraftStateDefinitionLoader.TryLoad(
            WriteFile(ValidJson), NullLogger.Instance, problems);

        Assert.NotNull(definition);
        Assert.Empty(problems.Snapshot());
    }
}
