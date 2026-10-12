using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Localization;
using Xunit;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>
/// The catalog's rules (ADR-0014): the user folder shadows the shipped one by code, the
/// configured language wins over the browser's, a region tag finds its primary pack, every
/// miss falls back to English and then to the id, a broken file is skipped, and an edited
/// file is picked up on the next scan. Each test owns a temp pair of folders.
/// </summary>
public sealed class LanguagePackCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));

    private string UserDir => Path.Combine(_root, "user");

    private string ShippedDir => Path.Combine(_root, "shipped");

    public LanguagePackCatalogTests()
    {
        Directory.CreateDirectory(UserDir);
        Directory.CreateDirectory(ShippedDir);
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

    private void Ship(string code, string json) => File.WriteAllText(Path.Combine(ShippedDir, code + ".json"), json);

    private void UserFile(string code, string json) => File.WriteAllText(Path.Combine(UserDir, code + ".json"), json);

    private LanguagePackCatalog Catalog() => new([UserDir, ShippedDir], NullLogger<LanguagePackCatalog>.Instance, TimeSpan.Zero);

    [Fact]
    public void Packs_EnglishFirst_ThenByNativeName_UserShadowsShipped()
    {
        Ship("en", """{ "$language": { "name": "English" }, "a": "A" }""");
        Ship("de", """{ "$language": { "name": "Deutsch" }, "a": "shipped" }""");
        Ship("fr", """{ "$language": { "name": "Français" }, "a": "F" }""");
        UserFile("de", """{ "$language": { "name": "Deutsch" }, "a": "user" }""");

        var catalog = Catalog();

        Assert.Equal(["en", "de", "fr"], catalog.Packs.Select(p => p.Code));
        Assert.Equal("user", catalog.Find("de")!.Strings["a"]);
    }

    [Fact]
    public void Resolve_ConfiguredWins_ThenBrowser_ThenEnglish()
    {
        Ship("en", """{ "a": "A" }""");
        Ship("de", """{ "a": "D" }""");
        Ship("ja", """{ "a": "J" }""");

        var catalog = Catalog();

        Assert.Equal("de", catalog.Resolve("de", ["ja"]));
        Assert.Equal("ja", catalog.Resolve("", ["ja-jp", "en"]));
        Assert.Equal("de", catalog.Resolve("xx", ["de-at"]));
        Assert.Equal("en", catalog.Resolve(null, ["zh", "ko"]));
        Assert.Equal("en", catalog.Resolve(null, []));
    }

    [Fact]
    public void Find_RegionTag_FallsBackToPrimarySubtag()
    {
        Ship("en", """{ "a": "A" }""");
        Ship("pt-BR", """{ "a": "BR" }""");
        Ship("de", """{ "a": "D" }""");

        var catalog = Catalog();

        Assert.Equal("pt-br", catalog.Find("pt-BR")!.Code);
        Assert.Equal("de", catalog.Find("de-CH")!.Code);
        Assert.Null(catalog.Find("pt"));
        Assert.Null(catalog.Find(""));
    }

    [Fact]
    public void Lookup_FallsBackToEnglish_ThenToTheId()
    {
        Ship("en", """{ "only": { "english": "E" }, "both": "EB" }""");
        Ship("de", """{ "both": "DB" }""");

        var catalog = Catalog();

        Assert.Equal("DB", catalog.Lookup("de", "both", out var found1));
        Assert.True(found1);
        Assert.Equal("E", catalog.Lookup("de", "only.english", out var found2));
        Assert.True(found2);
        Assert.Equal("nowhere", catalog.Lookup("de", "nowhere", out var found3));
        Assert.False(found3);
    }

    [Fact]
    public void BrokenFile_IsSkipped_OthersStillLoad()
    {
        Ship("en", """{ "a": "A" }""");
        Ship("de", "{ not json");

        var catalog = Catalog();

        Assert.Equal(["en"], catalog.Packs.Select(p => p.Code));
    }

    [Fact]
    public void EditedFile_IsReadAgain_AfterRefresh()
    {
        Ship("en", """{ "a": "first" }""");
        var catalog = Catalog();
        Assert.Equal("first", catalog.Lookup("en", "a", out _));

        // A write inside the same second keeps the same mtime on coarse file systems;
        // push it forward explicitly so the signature differs.
        var file = Path.Combine(ShippedDir, "en.json");
        File.WriteAllText(file, """{ "a": "second" }""");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        catalog.Refresh();

        Assert.Equal("second", catalog.Lookup("en", "a", out _));
    }

    [Fact]
    public void LanguageFolder_MergesItsPageFiles_FirstFileWinsADuplicate_UserFolderShadowsShipped()
    {
        var shippedDe = Path.Combine(ShippedDir, "de");
        Directory.CreateDirectory(shippedDe);
        File.WriteAllText(Path.Combine(shippedDe, "a-shell.json"), """{ "$language": { "name": "Deutsch" }, "nav": { "fuel": "Kraftstoff" }, "twice": "shell" }""");
        File.WriteAllText(Path.Combine(shippedDe, "b-fuel.json"), """{ "fuel": { "title": "Kraftstoff" }, "twice": "fuel" }""");
        Ship("en", """{ "nav": { "fuel": "Fuel" }, "fuel": { "title": "Fuel" }, "twice": "en" }""");

        var catalog = Catalog();

        var de = catalog.Find("de")!;
        Assert.Equal("Deutsch", de.NativeName);
        Assert.Equal("Kraftstoff", de.Strings["nav.fuel"]);
        Assert.Equal("Kraftstoff", de.Strings["fuel.title"]);
        Assert.Equal("shell", de.Strings["twice"]);
        Assert.Equal(shippedDe, de.Path);

        // A user folder of the same code replaces the shipped folder whole.
        var userDe = Path.Combine(UserDir, "de");
        Directory.CreateDirectory(userDe);
        File.WriteAllText(Path.Combine(userDe, "shell.json"), """{ "$language": { "name": "Deutsch (eigen)" }, "nav": { "fuel": "Sprit" } }""");
        catalog.Refresh();

        var shadowed = catalog.Find("de")!;
        Assert.Equal("Sprit", shadowed.Strings["nav.fuel"]);
        Assert.False(shadowed.Strings.ContainsKey("fuel.title"));
        Assert.Equal("Fuel", catalog.Lookup("de", "fuel.title", out _));
    }

    [Fact]
    public void StaleSingleFile_BesideAFolderOfTheSameCode_IsIgnored()
    {
        // The rc.7 install seeded de.json; the per-page split then seeded a de folder beside
        // it and the seeder never deletes a user file whose shipped source went away. The
        // folder must win, or every new page text shows English (2026-10-13 live run).
        var userDe = Path.Combine(UserDir, "de");
        Directory.CreateDirectory(userDe);
        File.WriteAllText(Path.Combine(userDe, "shell.json"), """{ "$language": { "name": "Deutsch" }, "nav": { "fuel": "Kraftstoff" } }""");
        File.WriteAllText(Path.Combine(userDe, "fuel.json"), """{ "fuel": { "title": "Kraftstoff" } }""");
        UserFile("de", """{ "$language": { "name": "Deutsch" }, "nav": { "fuel": "ALT" } }""");
        Ship("en", """{ "nav": { "fuel": "Fuel" }, "fuel": { "title": "Fuel" } }""");

        var catalog = Catalog();

        Assert.Equal("Kraftstoff", catalog.Lookup("de", "nav.fuel", out _));
        Assert.Equal("Kraftstoff", catalog.Lookup("de", "fuel.title", out _));
        Assert.Equal(["en", "de"], catalog.Packs.Select(p => p.Code));
    }

    [Fact]
    public void NoPacksAtAll_StillResolves_AndShowsTheId()
    {
        var catalog = Catalog();

        Assert.Equal("en", catalog.Resolve(null, ["de"]));
        Assert.Equal("x.y", catalog.Lookup("en", "x.y", out var found));
        Assert.False(found);
    }
}
