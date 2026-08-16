using System.Collections;
using System.Reflection;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// Fences for the option-section registry (#84) — the rules the compiler cannot hold:
/// every section class actually registers, no collection default hides where the shared
/// binder list-append fix cannot reach it, and razor pages never hand-write settings JSON.
/// </summary>
public sealed class OptionSectionGuardTests
{
    [Fact]
    public void EveryOptionSectionClass_IsRegisteredInTheCoreRegistry()
    {
        var registered = SettingsDefaultsWriterTests.CoreRegistry()
            .Sections.Select(s => s.OptionsType)
            .ToHashSet();
        var missing = typeof(IOptionSection).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IOptionSection).IsAssignableFrom(t))
            .Where(t => !registered.Contains(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "IOptionSection classes must be registered via AddOptionSection in AddCoreServices "
            + "(that is what puts their defaults in settings.json and their lists behind the "
            + "binder fix): " + string.Join(", ", missing));
    }

    [Fact]
    public void NoRegisteredSection_HidesACollectionDefault_BelowTheTopLevel()
    {
        // The binder list-append fix (OptionsListBinding) clears/restores TOP-LEVEL lists only,
        // and dictionaries are binder-merged at every level. A non-empty collection default
        // anywhere else re-opens the round-4 doubling bug — extend the fix before allowing one.
        var offenders = new List<string>();
        foreach (var section in SettingsDefaultsWriterTests.CoreRegistry().Sections)
        {
            Walk(section.CreateDefaults(), section.SectionName, topLevel: true, [], offenders);
        }

        Assert.True(offenders.Count == 0, "Collection defaults outside the guarded top level: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void RazorPages_NeverTouchTheSettingsFileDirectly()
    {
        var offenders = RazorFiles()
            .Where(file => File.ReadAllText(file.Absolute).Contains("JsonSettingsFile", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Pages persist through SettingsWriter drafts/expressions only (#84) — hand-written "
            + "section/key strings do not survive renames. Offenders: "
            + string.Join(", ", offenders));
    }

    private static void Walk(
        object instance, string path, bool topLevel, HashSet<object> visited, List<string> offenders)
    {
        if (!visited.Add(instance))
        {
            return;
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(instance);
            var childPath = $"{path}.{property.Name}";
            switch (value)
            {
                case null or string:
                    break;
                case IDictionary dictionary:
                    if (dictionary.Count > 0)
                    {
                        offenders.Add(childPath);
                    }

                    break;
                case IList list:
                    if (topLevel)
                    {
                        // Item defaults are rebuilt by the binder per file entry, so an item
                        // type carrying its own collection defaults would re-trap — walk them.
                        foreach (var item in list)
                        {
                            Walk(item, $"{childPath}[]", topLevel: false, visited, offenders);
                        }
                    }
                    else if (list.Count > 0)
                    {
                        offenders.Add(childPath);
                    }

                    break;
                default:
                    if (value.GetType().Namespace?.StartsWith("ProsimCompanion", StringComparison.Ordinal) is true)
                    {
                        Walk(value, childPath, topLevel: false, visited, offenders);
                    }

                    break;
            }
        }
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
