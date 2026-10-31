using System.Text.RegularExpressions;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Web.Components;
using ProsimCompanion.Web.Layout;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The hub-usability pass (issue #199): three rail groups over the one Voice First Officer
/// route, the pack-driven rail search, and the per-browser card folds.
/// </summary>
public sealed partial class SettingsHubTests
{
    // ---- SettingsNav: three groups, one route ----

    [Theory]
    [InlineData("settings/speech", "speech", "status")]
    [InlineData("settings/speech/general", "speech", "general")]
    [InlineData("settings/speech/providers/kokoro", "speech", "providers")]
    [InlineData("settings/speech/callouts", "flightDeck", "callouts")]
    [InlineData("settings/speech/sayIntentions", "flightDeck", "sayIntentions")]
    [InlineData("settings/speech/groundCrew", "crew", "groundCrew")]
    [InlineData("settings/speech/techlog", "crew", "techlog")]
    [InlineData("settings/speech/no-such-section", "speech", "status")]
    [InlineData("settings/gsx/pushback", "gsx", "pushback")]
    [InlineData("settings", "setup", null)]
    public void GroupFor_PrefersTheGroupThatListsTheItem(string path, string group, string? item)
    {
        var owner = SettingsNav.GroupFor(path);

        Assert.NotNull(owner);
        Assert.Equal(group, owner.Key);
        Assert.Equal(item, owner.ActiveItem(path)?.Key);
    }

    [Fact]
    public void SpeechSections_AreTheThreeGroupsJoined_WithNoDuplicates()
    {
        var keys = SettingsNav.SpeechSections.Select(s => s.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(16, keys.Count);
        Assert.All(SettingsNav.SpeechSections, s => Assert.StartsWith("settings/speech/", s.Href, StringComparison.Ordinal));
        Assert.All(SettingsNav.SpeechSections, s => Assert.StartsWith("settings.nav.speech.", s.LabelTextId, StringComparison.Ordinal));
        Assert.Equal("status", SettingsNav.ResolveSection(SettingsNav.SpeechSections, null));
        Assert.Equal("callouts", SettingsNav.ResolveSection(SettingsNav.SpeechSections, "CALLOUTS"));
    }

    [Fact]
    public void TheSpeechPillarNotice_CoversAllThreeGroups()
    {
        Assert.True(SettingsNav.IsSpeechGroup(SettingsNav.GroupFor("settings/speech")));
        Assert.True(SettingsNav.IsSpeechGroup(SettingsNav.GroupFor("settings/speech/callouts")));
        Assert.True(SettingsNav.IsSpeechGroup(SettingsNav.GroupFor("settings/speech/cabin")));
        Assert.False(SettingsNav.IsSpeechGroup(SettingsNav.GroupFor("settings/audio")));
        Assert.False(SettingsNav.IsSpeechGroup(null));
    }

    // ---- SettingsSearch ----

    [Fact]
    public void Search_FindsTheTransitionRemindersCard_UnderFlightDeckCallouts()
    {
        var index = SettingsSearch.Build(English().Strings);

        var hits = SettingsSearch.Find(index, "transition reminders");

        var first = Assert.Single(hits.Take(1));
        Assert.Equal("settings/speech/callouts", first.Href);
        Assert.Equal("flightDeck", first.Group.Key);
        Assert.Equal("Transition Reminders", first.Title);
    }

    [Fact]
    public void Search_RailLabelsRankFirst_EveryWordMustMatch_ShortQueriesAreEmpty()
    {
        var index = SettingsSearch.Build(English().Strings);

        var cabin = SettingsSearch.Find(index, "cabin crew");
        Assert.True(cabin.Count > 0);
        Assert.True(cabin[0].IsNavLabel);
        Assert.Equal("settings/speech/cabin", cabin[0].Href);

        Assert.Empty(SettingsSearch.Find(index, "c"));
        Assert.Empty(SettingsSearch.Find(index, ""));
        Assert.Empty(SettingsSearch.Find(index, "cabin zzzzqqq"));
        Assert.True(SettingsSearch.Find(index, "qnh", max: 3).Count <= 3);
    }

    [Theory]
    [InlineData("setup.prosim", "setup", null, "settings")]
    [InlineData("cockpit.general", "cockpit", null, "settings/cockpit")]
    [InlineData("flightPhase.speeds", "advanced", "flightPhase", "settings/flight-phase")]
    [InlineData("logs.capture", "advanced", "logs", "settings/logs")]
    [InlineData("skalarkiSettings.link", "hardware", "skalarki", "settings/hardware/skalarki")]
    [InlineData("speechStatus.fo", "speech", "status", "settings/speech/status")]
    [InlineData("speechSettings.smallTalk", "flightDeck", "briefing", "settings/speech/briefing")]
    [InlineData("speechSettings.techLog", "crew", "techlog", "settings/speech/techlog")]
    [InlineData("gsxSettings.menuCard", "gsx", "status", "settings/gsx/status")]
    [InlineData("audioSettings.housekeeping", "audio", "advanced", "settings/audio/advanced")]
    [InlineData("display.multi", "display", "multiwindow", "settings/display/multiwindow")]
    public void Locate_MapsBlocksToTheirRailPlace(string block, string group, string? item, string href)
    {
        var location = SettingsSearch.Locate(block);

        Assert.NotNull(location);
        Assert.Equal(group, location.Value.Group.Key);
        Assert.Equal(item, location.Value.Item?.Key);
        Assert.Equal(href, location.Value.Href);
    }

    [Fact]
    public void Locate_IgnoresPacksThatAreNotSettingsPages()
    {
        Assert.Null(SettingsSearch.Locate("flightStatus.hero"));
        Assert.Null(SettingsSearch.Locate("monitor.rows"));
    }

    /// <summary>The block table against the Razor sources: every pack block a sectioned page
    /// uses inside a <c>CurrentSection == "x"</c> branch must locate to a section that renders
    /// it — a card moved to another section, or a new block, fails here rather than sending a
    /// search hit to the wrong page.</summary>
    [Theory]
    [InlineData("SpeechSettings.razor", "speechSettings", "settings/speech")]
    [InlineData("GsxSettings.razor", "gsxSettings", "settings/gsx")]
    [InlineData("AudioSettings.razor", "audioSettings", "settings/audio")]
    [InlineData("DisplaySettings.razor", "display", "settings/display")]
    public void EveryBlockOfASectionedPage_LocatesToASectionThatRendersIt(string page, string root, string routeBase)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ProsimCompanion.Web", "Pages", page));
        var markup = source[..source.IndexOf("@code", StringComparison.Ordinal)];
        var usedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var chunks = SectionBranch().Split(markup);
        for (var i = 1; i < chunks.Length; i += 2)
        {
            var section = chunks[i];
            // A bare "else" after the last branch is the page's remaining section (Housekeeping on
            // the Audio page): the regex cannot name it, so its texts are not attributed.
            var body = chunks[i + 1];
            var bareElse = BareElse().Match(body);
            if (bareElse.Success)
            {
                body = body[..bareElse.Index];
            }

            foreach (Match m in BlockLookup().Matches(body))
            {
                if (m.Groups["root"].Value != root)
                {
                    continue;
                }

                var block = $"{root}.{m.Groups["block"].Value}";
                if (!usedBy.TryGetValue(block, out var sections))
                {
                    sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    usedBy[block] = sections;
                }

                sections.Add(section);
            }
        }

        Assert.NotEmpty(usedBy);
        var problems = new List<string>();
        foreach (var (block, sections) in usedBy)
        {
            if (block is "speechSettings.common" or "speechSettings.options" or "gsxSettings.options")
            {
                continue; // shared wording / choice lists, deliberately not a card
            }

            var location = SettingsSearch.Locate(block);
            if (location is null || location.Value.Item is null || !sections.Contains(location.Value.Item.Key))
            {
                problems.Add($"{block}: located at {location?.Href ?? "nowhere"}, rendered in {string.Join("/", sections)}");
            }

            if (location is not null && !location.Value.Href.StartsWith(routeBase, StringComparison.Ordinal))
            {
                problems.Add($"{block}: off the page ({location.Value.Href})");
            }
        }

        Assert.True(problems.Count == 0, "Search block table and the Razor page disagree:\n" + string.Join('\n', problems));
    }

    // ---- SectionFoldStore ----

    [Fact]
    public void Folds_DefaultUntilSet_PersistEveryChange_AndFoldAllCoversThePage()
    {
        var store = new SectionFoldStore { Page = "settings/speech/callouts" };
        var persisted = new List<IReadOnlyDictionary<string, bool>>();
        store.Persist += persisted.Add;
        var standard = store.IdFor("Standard Callouts");
        var transition = store.IdFor("Transition Reminders");
        store.Register(standard, defaultFolded: false);
        store.Register(transition, defaultFolded: true);

        Assert.False(store.IsFolded(standard, false));
        Assert.True(store.IsFolded(transition, true));
        Assert.True(store.AnyFolded);
        Assert.True(store.AnyUnfolded);

        store.Set(transition, false);
        Assert.False(store.IsFolded(transition, true));
        Assert.Equal(new Dictionary<string, bool> { [transition] = false }, persisted.Single());

        store.SetAll(true);
        Assert.True(store.IsFolded(standard, false));
        Assert.True(store.IsFolded(transition, true));
        Assert.False(store.AnyUnfolded);
        Assert.Equal(2, persisted[1].Count);

        // The browser's saved states win over the defaults; unknown ids are ignored.
        store.Load(new Dictionary<string, bool> { [standard] = false, ["other|card"] = true });
        Assert.False(store.IsFolded(standard, false));
        Assert.True(store.IsFolded(transition, true)); // back to its default — no saved state

        store.Unregister(transition);
        store.Set(standard, true);
        Assert.False(store.AnyUnfolded);
        Assert.Equal("settings/speech/callouts|Standard Callouts", standard);
    }

    private static LanguagePack English()
    {
        var folder = Path.Combine(RepoRoot(), "src", "ProsimCompanion.App", "config", "lang", "en");
        var parts = Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => LanguagePack.Load(f, "en")).ToList();
        return LanguagePack.Combine("en", parts, out _);
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

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    [GeneratedRegex(@"CurrentSection\s*==\s*""(\w+)""")]
    private static partial Regex SectionBranch();

    [GeneratedRegex(@"T\[""(?<root>\w+)\.(?<block>\w+)(?:\.|"")")]
    private static partial Regex BlockLookup();

    [GeneratedRegex(@"\}\s*else\s*\{")]
    private static partial Regex BareElse();
}
