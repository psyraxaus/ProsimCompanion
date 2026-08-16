using System.Text.RegularExpressions;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>
/// Fence for the store-observer migration (campaign #86): pages declare what they watch
/// through the observer base — hand-wired store subscriptions (subscribe + marshal + dispose
/// triplets) must not reappear in razor files.
/// </summary>
public sealed class StoreObserverGuardTests
{
    [Fact]
    public void RazorFiles_NeverHandWireStoreSubscriptions()
    {
        // Store change events are named Changed / PhaseChanged across the codebase. Non-store
        // events (e.g. PttCapture.PressedChanged) deliberately do not match: the pattern
        // requires the member to be exactly ".Changed"/".PhaseChanged". The one sanctioned
        // form is the WatchChanged plumbing lambda `h => Store.Changed += h`, so a right-hand
        // side that is exactly the parameter `h` is allowed; anything else (a named handler,
        // an inline lambda) is hand-wiring.
        var pattern = new Regex(@"\.(Changed|PhaseChanged)\s*[+-]=(?!\s*h\b)", RegexOptions.Singleline);
        var offenders = RazorFiles()
            .Where(file => pattern.IsMatch(File.ReadAllText(file.Absolute)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Components watch stores via StoreObserverComponent/StoreObserverLayout (#86) — "
            + "declare a Watch/WatchChanged instead of wiring the event. Offenders: "
            + string.Join(", ", offenders));
    }

    private static IEnumerable<(string Absolute, string Relative)> RazorFiles()
    {
        var root = RepoRoot();
        return Directory
            .EnumerateFiles(Path.Combine(root, "src", "ProsimCompanion.Web"), "*.razor", SearchOption.AllDirectories)
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
