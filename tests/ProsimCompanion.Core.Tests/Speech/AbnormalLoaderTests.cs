using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Abnormals;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Loader-path reporting for issue #74: a malformed abnormal file must load the rest
/// of the folder AND surface (file, message) through the problem callback, which the monitor
/// feeds into <see cref="ConfigProblemStore"/>.</summary>
public sealed class AbnormalLoaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "ProsimCompanionTests", Path.GetRandomFileName());

    public AbnormalLoaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only — never fail a test over it.
        }
    }

    [Fact]
    public void MalformedFile_IsSkipped_AndReported_GoodFileStillLoads()
    {
        File.WriteAllText(Path.Combine(_folder, "good.json"),
            """{ "id": "engFire", "title": "ENG 1 FIRE", "announce": "Engine fire." }""");
        var badPath = Path.Combine(_folder, "bad.json");
        File.WriteAllText(badPath, """{ "id": "broken", "title": ["""); // truncated JSON

        var problems = new List<(string File, string Message)>();
        var definitions = AbnormalLoader.LoadFolder(_folder, (file, message) => problems.Add((file, message)));

        var definition = Assert.Single(definitions);
        Assert.Equal("engFire", definition.Id);
        var problem = Assert.Single(problems);
        Assert.Equal(badPath, problem.File);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    [Fact]
    public void IdLessFile_IsReported()
    {
        File.WriteAllText(Path.Combine(_folder, "noid.json"), """{ "title": "NO ID" }""");

        var problems = new List<(string File, string Message)>();
        var definitions = AbnormalLoader.LoadFolder(_folder, (file, message) => problems.Add((file, message)));

        Assert.Empty(definitions);
        var problem = Assert.Single(problems);
        Assert.Contains("id", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportedProblems_FlowIntoTheStore()
    {
        File.WriteAllText(Path.Combine(_folder, "bad.json"), "{ not json");
        var store = new ConfigProblemStore();

        AbnormalLoader.LoadFolder(_folder, (file, message)
            => store.Report(ConfigAreas.Abnormals, file, message));

        var problem = Assert.Single(store.Snapshot());
        Assert.Equal(ConfigAreas.Abnormals, problem.Area);
        Assert.EndsWith("bad.json", problem.SourceFile);
    }

    [Fact]
    public void NoCallback_StillLoadsWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_folder, "bad.json"), "{ not json");
        Assert.Empty(AbnormalLoader.LoadFolder(_folder));
    }
}
