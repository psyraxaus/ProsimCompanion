using System.Text.RegularExpressions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Source-level fence for the trigger slot invariant (campaign #77, CONTEXT.md "trigger
/// slot"): only <c>GsxTriggerSlot</c> may build a <c>service.trigger</c> payload. The
/// PCGSX001 analyzer enforces this at compile time inside the IDE/build; this test is the
/// belt-and-braces sweep over the raw sources so the rule survives even with analyzers
/// disabled.
/// </summary>
public sealed class TriggerPathGuardTests
{
    /// <summary>Files allowed to contain the raw wire literal at all: the slot (sends it),
    /// the transport client (wire-trace verb abbreviation map — receive side), and the
    /// analyzer that defines the fence.</summary>
    private static readonly string[] LiteralAllowList =
    [
        @"src\ProsimCompanion.Gsx\Automation\GsxTriggerSlot.cs",
        @"src\ProsimCompanion.Gsx\GsxRemoteApiClient.cs",
        @"src\ProsimCompanion.Analyzers\TriggerSlotAnalyzer.cs",
    ];

    [Fact]
    public void OnlyTheTriggerSlot_SendsServiceTrigger()
    {
        var sendPattern = new Regex(
            "SendCommandAsync\\(\\s*\"service\\.trigger\"",
            RegexOptions.Singleline);
        var offenders = ProductionSources()
            .Where(file => sendPattern.IsMatch(File.ReadAllText(file.Absolute)))
            .Where(file => !LiteralAllowList.Any(allowed =>
                file.Relative.EndsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "service.trigger may only be sent by GsxTriggerSlot (the trigger slot invariant); "
            + $"direct sends found in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheWireLiteral_AppearsOnlyInAllowListedFiles()
    {
        var offenders = ProductionSources()
            .Where(file => File.ReadAllText(file.Absolute).Contains("\"service.trigger\"", StringComparison.Ordinal))
            .Where(file => !LiteralAllowList.Any(allowed =>
                file.Relative.EndsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The \"service.trigger\" literal belongs to the trigger slot (plus the transport's "
            + $"verb map); found in: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<(string Absolute, string Relative)> ProductionSources()
    {
        var root = RepoRoot();
        var srcDir = Path.Combine(root, "src");
        return Directory
            .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (path, Path.GetRelativePath(root, path)));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent!;
        }
        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }
}
