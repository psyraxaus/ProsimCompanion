using System.Text.RegularExpressions;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Web.Layout;
using Xunit;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>
/// Source guards for the shipped language packs (ADR-0014). The <c>en</c> folder is the source
/// of truth: every other language folder must carry the same page files with exactly the same
/// ids and placeholders, every id a Razor file asks for must exist in English, and the
/// settings rail's English labels must equal the pack's — so a new text, a renamed id or a
/// half-done translation fails here, not on a pilot's tablet.
/// </summary>
public sealed partial class ShippedLanguagePackGuardTests
{
    /// <summary>The languages the app ships (owner list 2026-10-13, Dutch added the same day).</summary>
    private static readonly string[] ShippedCodes = ["de", "en", "es", "fr", "ja", "ko", "nl"];

    private static string PackRoot => Path.Combine(RepoRoot(), "src", "ProsimCompanion.App", "config", "lang");

    private static IEnumerable<string> LanguageFolders()
        => Directory.EnumerateDirectories(PackRoot).OrderBy(d => d, StringComparer.Ordinal);

    private static IReadOnlyList<string> PageFiles(string folder)
        => [.. Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)!];

    private static LanguagePack LoadFolder(string folder)
    {
        var code = Path.GetFileName(folder);
        var parts = Directory.EnumerateFiles(folder, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => LanguagePack.Load(f, code))
            .ToList();
        return LanguagePack.Combine(code, parts, out _);
    }

    private static LanguagePack English => LoadFolder(Path.Combine(PackRoot, "en"));

    [Fact]
    public void TheShippedLanguages_ArePresent_AsFolders_AndParse()
    {
        var codes = LanguageFolders().Select(f => LoadFolder(f).Code).ToList();

        Assert.Equal(ShippedCodes, codes);
        Assert.Empty(Directory.EnumerateFiles(PackRoot, "*.json")); // no stray single-file packs beside the folders
    }

    [Fact]
    public void EveryLanguage_HasTheSamePageFiles_AsEnglish()
    {
        var english = PageFiles(Path.Combine(PackRoot, "en"));
        var problems = new List<string>();
        foreach (var folder in LanguageFolders())
        {
            var files = PageFiles(folder);
            foreach (var missing in english.Except(files, StringComparer.Ordinal))
            {
                problems.Add($"{Path.GetFileName(folder)}: missing {missing}");
            }

            foreach (var extra in files.Except(english, StringComparer.Ordinal))
            {
                problems.Add($"{Path.GetFileName(folder)}: extra {extra} (not in en)");
            }
        }

        Assert.True(problems.Count == 0, "Language folders differ from en:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryPageFile_HasExactlyTheEnglishIds_AndNoIdTwice()
    {
        var problems = new List<string>();
        var englishFolder = Path.Combine(PackRoot, "en");
        foreach (var folder in LanguageFolders())
        {
            var code = Path.GetFileName(folder);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in PageFiles(folder))
            {
                var englishFile = Path.Combine(englishFolder, file);
                if (!File.Exists(englishFile))
                {
                    continue; // reported by the file test
                }

                var english = LanguagePack.Load(englishFile, "en");
                var pack = LanguagePack.Load(Path.Combine(folder, file), code);
                foreach (var id in english.Strings.Keys.Except(pack.Strings.Keys, StringComparer.Ordinal))
                {
                    problems.Add($"{code}\\{file}: missing {id}");
                }

                foreach (var id in pack.Strings.Keys.Except(english.Strings.Keys, StringComparer.Ordinal))
                {
                    problems.Add($"{code}\\{file}: extra {id} (not in en\\{file})");
                }

                foreach (var id in pack.Strings.Keys.Where(id => !seen.Add(id)))
                {
                    problems.Add($"{code}\\{file}: {id} is also defined in another file of this language");
                }
            }

            if (LoadFolder(folder).NativeName == code)
            {
                problems.Add($"{code}: no file declares $language.name");
            }
        }

        Assert.True(problems.Count == 0, "Language packs drifted from en:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryLanguage_KeepsTheEnglishPlaceholders()
    {
        var english = English;
        var problems = new List<string>();
        foreach (var folder in LanguageFolders())
        {
            var pack = LoadFolder(folder);
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

        Assert.True(problems.Count == 0, "Placeholders differ from en:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryIdARazorFileAsksFor_ExistsInEnglish()
    {
        var english = English;
        var missing = new List<string>();
        var webRoot = Path.Combine(RepoRoot(), "src", "ProsimCompanion.Web");
        foreach (var file in Directory.EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories))
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
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

        Assert.True(missing.Count == 0, "Razor asks for ids the en folder does not have:\n" + string.Join('\n', missing));
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

        Assert.True(problems.Count == 0, "Settings rail and the en folder disagree:\n" + string.Join('\n', problems));

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

    /// <summary>Copied-English guard (audit 2026-11-03, #236): the id/placeholder checks above
    /// pass a pack whose values are the English text pasted six times, which is how ~90
    /// texts shipped untranslated. A text that reads the same in every non-en pack is taken
    /// as never translated, and so is any Latin-lowercase text left standing in the Japanese
    /// or Korean pack (a CJK pack never keeps an English sentence as its translation). Texts
    /// that are invariant by nature are skipped: those with no lowercase Latin letter once
    /// the product and brand names are removed (ICAO/IATA codes, units, "OK", "COM1",
    /// "{0} — {1}"), and the short list of ids below. A loanword that one language keeps
    /// ("Pushback" in German, "Checklists" in Dutch) passes because some other language
    /// changed it.</summary>
    [Fact]
    public void EveryText_IsTranslated_InSomeLanguage_AndNeverLeftEnglishInJapaneseOrKorean()
    {
        var english = English;
        var packs = LanguageFolders().Select(LoadFolder).Where(p => p.Code != "en").ToList();
        var problems = new List<string>();
        foreach (var (id, text) in english.Strings)
        {
            if (!NeedsTranslation(id, text))
            {
                continue;
            }

            var unchanged = packs
                .Where(p => p.Strings.TryGetValue(id, out var own) && string.Equals(own, text, StringComparison.Ordinal))
                .Select(p => p.Code)
                .ToList();
            if (unchanged.Count == packs.Count)
            {
                problems.Add($"{id}: \"{text}\" is still English in every pack");
                continue;
            }

            foreach (var code in unchanged.Where(c => c is "ja" or "ko"))
            {
                problems.Add($"{code}: {id} is still \"{text}\"");
            }
        }

        Assert.True(problems.Count == 0, "Texts shipped as copied English (translate them, or add the id to InvariantIds with a reason):\n" + string.Join('\n', problems));
    }

    /// <summary>Ids whose English text is the right text in every language. Keep this short:
    /// every entry needs the reason beside it.</summary>
    private static readonly HashSet<string> InvariantIds = new(StringComparer.Ordinal)
    {
        // The ECAM control-panel legends the Checklists page copies (cockpit English).
        "checklists.actions.menu",
        "checklists.actions.msgList",
        "checklists.actions.complete",
        // Minima abbreviations with their units: DA/DH/MDA, baro/radio, ft MSL.
        "speechStatus.minima.da",
        "speechStatus.minima.dh",
        "speechStatus.minima.mda",
        // Flight-phase engine tokens — phase names and 'hold' exactly as the rule table, the
        // hints that quote them and the CMTrace log print them.
        "flightPhase.rules.hold",
        "flightPhase.settle.descentToCruise.label",
        "flightPhase.settle.cruiseToClimb.label",
        "flightPhase.settle.descentToClimb.label",
        // MSFS 2024's own menu name, which the note tells the pilot to open.
        "display.multi.title",
        "settings.nav.display.multiwindow",
        // A quoted English trigger phrase: the voice First Officer hears English only.
        "speechSettings.briefing.ask.flyingOver.label",
        // Example Windows paths.
        "setup.web.pfx.placeholder",
        "setup.weather.snapshot.placeholder",
    };

    /// <summary>Product, brand and file names that stay as they are in every language; a
    /// text that is nothing but these (plus codes, units and placeholders) needs no
    /// translation.</summary>
    private static readonly string[] InvariantTokens =
    [
        "ProsimCompanion", "ProSim", "SimBrief", "GSX", "Kokoro", "ElevenLabs", "VoiceMeeter", "Voicemeeter",
        "ChasePlane", "SayIntentions", "CoreAudio", "Google Cloud TTS", "Chirp", "Wake-on-LAN", "Stream Deck",
        "Navigraph", "Skalarki", "Profiler", "vJoyInterface.dll", "localhost", "InitialZoom", "WinRT", "SAPI5",
    ];

    private static bool NeedsTranslation(string id, string english)
    {
        if (InvariantIds.Contains(id))
        {
            return false;
        }

        var rest = english;
        foreach (var token in InvariantTokens)
        {
            rest = rest.Replace(token, "", StringComparison.Ordinal);
        }

        return rest.Any(c => c is >= 'a' and <= 'z');
    }

    private static HashSet<string> Placeholders(string text)
        => [.. PlaceholderPattern().Matches(text).Select(m => m.Value)];

    // T["some.id"] / T["some.id", args] in Razor and C#, Text("some.id") in the static App root.
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
