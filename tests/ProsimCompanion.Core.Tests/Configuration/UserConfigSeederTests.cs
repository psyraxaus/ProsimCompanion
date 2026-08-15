using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// The keep-user-edits seeding pass (ADR-0007, issue #55): missing files seed, unedited files
/// follow shipped updates, edited files are never overwritten, and files the app never
/// shipped are never touched. Each test runs in its own temp pair of shipped/user roots.
/// </summary>
public sealed class UserConfigSeederTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));

    private string ShippedRoot => Path.Combine(_root, "shipped");

    private string UserRoot => Path.Combine(_root, "user");

    public UserConfigSeederTests()
    {
        Directory.CreateDirectory(ShippedRoot);
        Directory.CreateDirectory(UserRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private void Ship(string relative, string content)
    {
        var path = Path.Combine(ShippedRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void PlaceUser(string relative, string content)
    {
        var path = Path.Combine(UserRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadUser(string relative) => File.ReadAllText(Path.Combine(UserRoot, relative));

    private UserConfigSeedResult Seed(params string[] entries)
        => UserConfigSeeder.Seed(ShippedRoot, UserRoot, entries, NullLogger.Instance);

    [Fact]
    public void SeedsMissingFiles_IncludingNestedFolders_AndSingleFileEntries()
    {
        Ship(@"checklists\taxi.json", "{\"checklist\":\"Taxi\"}");
        Ship(@"checklists\sets\a320.json", "{\"set\":true}");
        Ship("commands.json", "{\"commands\":[]}");

        var result = Seed("checklists", "commands.json");

        Assert.Equal(3, result.Seeded);
        Assert.Equal("{\"checklist\":\"Taxi\"}", ReadUser(@"checklists\taxi.json"));
        Assert.Equal("{\"set\":true}", ReadUser(@"checklists\sets\a320.json"));
        Assert.Equal("{\"commands\":[]}", ReadUser("commands.json"));
    }

    [Fact]
    public void KeepsUserEditedFile_WhenShippedContentChanges()
    {
        Ship(@"checklists\taxi.json", "original");
        Seed("checklists");

        PlaceUser(@"checklists\taxi.json", "user edit");
        Ship(@"checklists\taxi.json", "new shipped default");
        var result = Seed("checklists");

        Assert.Equal(1, result.KeptEdited);
        Assert.Equal("user edit", ReadUser(@"checklists\taxi.json"));
    }

    [Fact]
    public void RefreshesUneditedFile_WhenShippedContentChanges()
    {
        Ship(@"checklists\taxi.json", "original");
        Seed("checklists");

        Ship(@"checklists\taxi.json", "new shipped default");
        var result = Seed("checklists");

        Assert.Equal(1, result.Updated);
        Assert.Equal("new shipped default", ReadUser(@"checklists\taxi.json"));
    }

    [Fact]
    public void AdoptsIdenticalUnmanifestedFile_SoItAutoUpdatesFromThenOn()
    {
        // Migration from the pre-ADR-0007 layout: the user copied the folder by hand, so the
        // file matches shipped but no manifest entry exists yet.
        Ship(@"abnormals\apu-fire.json", "v1");
        PlaceUser(@"abnormals\apu-fire.json", "v1");

        var first = Seed("abnormals");
        Assert.Equal(1, first.Current);

        Ship(@"abnormals\apu-fire.json", "v2");
        var second = Seed("abnormals");

        Assert.Equal(1, second.Updated);
        Assert.Equal("v2", ReadUser(@"abnormals\apu-fire.json"));
    }

    [Fact]
    public void DivergedFileWithoutManifest_CountsAsUserEdited()
    {
        // Same migration path but the user had EDITED the copy: no manifest, contents differ
        // from shipped — the conservative rule keeps it.
        Ship(@"checklists\taxi.json", "shipped");
        PlaceUser(@"checklists\taxi.json", "an old personal edit");

        var result = Seed("checklists");

        Assert.Equal(1, result.KeptEdited);
        Assert.Equal("an old personal edit", ReadUser(@"checklists\taxi.json"));
    }

    [Fact]
    public void UserFilesWithoutShippedCounterpart_AreNeverTouched()
    {
        Ship(@"checklists\taxi.json", "shipped");
        PlaceUser(@"checklists\my-own-flow.json", "personal checklist");

        Seed("checklists");

        Assert.Equal("personal checklist", ReadUser(@"checklists\my-own-flow.json"));
    }

    [Fact]
    public void MissingShippedEntries_AreSkippedWithoutError()
    {
        var result = Seed("themes", "atc-requests.json");

        Assert.Equal(new UserConfigSeedResult(0, 0, 0, 0), result);
    }
}
