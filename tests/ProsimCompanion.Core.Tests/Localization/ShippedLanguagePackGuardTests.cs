using System.Text.RegularExpressions;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Web.Layout;
using Xunit;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>
/// Source guards for the shipped language packs (ADR-0014). <c>en.json</c> is the source of
/// truth: every other pack must carry exactly its ids with the same placeholders, every id a
/// Razor file asks for must exist in English, and the settings rail's English labels must
/// equal the pack's — so a new text, a renamed id or a half-done translation fails here, not
/// on a pilot's tablet.
/// </summary>
public sealed partial class ShippedLanguagePackGuardTests
{
    private static string PackDir => Path.Combine(RepoRoot(), "src", "ProsimCompanion.App", "config", "lang");

    private static LanguagePack English => LanguagePack.Load(Path.Combine(PackDir, "en.json"));

    private static IEnumerable<string> PackFiles() => Directory.EnumerateFiles(PackDir, "*.json");

    [Fact]
    public void TheSixShippedPacks_ArePresent_AndParse()
    {
        var codes = PackFiles().Select(f => LanguagePack.Load(f).Code).OrderBy(c => c, StringComparer.Ordinal).ToList();

        Assert.Equal(["de", "en", "es", "fr", "ja", "ko"], codes);
    }

    [Fact]
    public void EveryPack_HasExactlyTheEnglishIds()
    {
        var english = English;
        var problems = new List<string>();
        foreach (var file in PackFiles())
        {
            var pack = LanguagePack.Load(file);
            if (pack.Code == "en")
            {
                continue;
            }

            foreach (var id in english.Strings.Keys.Except(pack.Strings.Keys, StringComparer.Ordinal))
            {
                problems.Add($"{pack.Code}: missing {id}");
            }

            foreach (var id in pack.Strings.Keys.Except(english.Strings.Keys, StringComparer.Ordinal))
            {
                problems.Add($"{pack.Code}: extra {id} (not in en.json)");
            }

            if (pack.NativeName == pack.Code)
            {
                problems.Add($"{pack.Code}: no $language.name");
            }
        }

        Assert.True(problems.Count == 0, "Language packs drifted from en.json:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryPack_KeepsTheEnglishPlaceholders()
    {
        var english = English;
        var problems = new List<string>();
        foreach (var file in PackFiles())
        {
            var pack = LanguagePack.Load(file);
            foreach (var (id, text) in pack.Strings)
            {
                if (!english.Strings.TryGetValue(id, out var source))
                {
                    continue; // reported by the id test
                }

                var expected = Placeholders(source);
                var actual = Placeholders(text);
                if (!expected.SetEquals(actual))
                {
                    problems.Add($"{pack.Code}: {id} has {{{string.Join(",", actual)}}} but English has {{{string.Join(",", expected)}}}");
                }
            }
        }

        Assert.True(problems.Count == 0, "Placeholders differ from en.json:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryIdARazorFileAsksFor_ExistsInEnglish()
    {
        var english = English;
        var missing = new List<string>();
        var webRoot = Path.Combine(RepoRoot(), "src", "ProsimCompanion.Web");
        foreach (var file in Directory.EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (Match match in LiteralLookup().Matches(line))
                {
                    var id = match.Groups["id"].Value;
                    if (!english.Strings.ContainsKey(id))
                    {
                        missing.Add($"{Path.GetRelativePath(webRoot, file)}:{lineNumber}: {id}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Razor asks for ids en.json does not have:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void SettingsRail_EnglishLabels_MatchThePack()
    {
        var english = English;
        var problems = new List<string>();
        foreach (var group in SettingsNav.Groups)
        {
            Check(group.LabelTextId, group.Label);
            foreach (var item in group.Items)
            {
                Check(item.LabelTextId, item.Label);
            }
        }

        Assert.True(problems.Count == 0, "Settings rail and en.json disagree:\n" + string.Join('\n', problems));

        void Check(string id, string label)
        {
            if (!english.Strings.TryGetValue(id, out var text))
            {
                problems.Add($"missing {id} (\"{label}\")");
            }
            else if (!string.Equals(text, label, StringComparison.Ordinal))
            {
                problems.Add($"{id}: pack says \"{text}\", SettingsNav says \"{label}\"");
            }
        }
    }

    private static HashSet<string> Placeholders(string text)
        => [.. PlaceholderPattern().Matches(text).Select(m => m.Value)];

    // T["some.id"] and T["some.id", args] in Razor, Text("some.id") in the static App root.
    [GeneratedRegex("""(?:\bT\[|\bText\()"(?<id>[A-Za-z0-9.]+)"[,\]\)]""")]
    private static partial Regex LiteralLookup();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }
}
