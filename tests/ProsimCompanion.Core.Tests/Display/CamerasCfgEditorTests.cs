using System.Text;
using ProsimCompanion.Core.Display;
using Xunit;

namespace ProsimCompanion.Core.Tests.Display;

/// <summary>The surgical cameras.cfg rewrite behind the Screen &amp; FOV page: only the zoom
/// (and on request the pitch) of cockpit pilot-view blocks changes; every other byte, the
/// line endings and a BOM survive; and the write helper refuses anything outside a LocalCache
/// SimObjects\Airplanes folder.</summary>
public sealed class CamerasCfgEditorTests
{
    private static readonly string[] FixtureLines =
    [
        "[VERSION]",
        "Major = 1",
        "Minor = 1",
        "",
        "[CAMERADEFINITION.0]",
        "Title = \"VFR Pilot\"",
        "Guid = \"{A3D29B45-0000-4000-8000-000000000001}\"",
        "Description = \"\"",
        "Origin = \"Virtual Cockpit\"",
        "SnapPbhAdjust = \"Swivel\"",
        "Category = \"Cockpit\"",
        "SubCategory = \"Pilot\"",
        "SubCategoryItem = \"VFRPilot\"",
        "InitialXyz = 0.0, 0.0, 0.0",
        "InitialPbh = 2.5, 0, 0",
        "InitialZoom = 0.35",
        "",
        "[CAMERADEFINITION.1]",
        "Title = \"Landing Pilot\"",
        "Category=Cockpit",
        "SubCategoryItem=LandingPilot",
        "InitialPbh = -1 ,  0.0 ,180",
        "InitialZoom=0.5 ; keep this comment",
        "",
        "[CAMERADEFINITION.2]",
        "Title = \"Custom 1\"",
        "Category = \"Cockpit\"",
        "SubCategoryItem = \"Custom1\"",
        "InitialPbh = 0, 0, 0",
        "InitialZoom = 0.35",
        "",
        "[CAMERADEFINITION.3]",
        "Title = \"Chase\"",
        "Category = \"Chase\"",
        "SubCategoryItem = \"VFRPilot\"",
        "InitialPbh = 0, 0, 0",
        "InitialZoom = 0.35",
    ];

    private static string Fixture(string ending) => string.Join(ending, FixtureLines) + ending;

    [Fact]
    public void Apply_ChangesOnlyTheTwoPilotViewBlocks()
    {
        var edit = CamerasCfgEditor.Apply(Fixture("\n"), 0.1367, null);

        Assert.Equal(2, edit.BlocksChanged);
        var lines = edit.Content.Split('\n');
        Assert.Equal("InitialZoom = 0.1367", lines[15]);
        Assert.Equal("InitialZoom=0.1367 ; keep this comment", lines[22]);
        Assert.Equal("InitialZoom = 0.35", lines[29]); // Custom1 is not a pilot view
        Assert.Equal("InitialZoom = 0.35", lines[36]); // Chase category
    }

    [Fact]
    public void Apply_PreservesEveryUntouchedLine_ByteForByte()
    {
        var edit = CamerasCfgEditor.Apply(Fixture("\n"), 0.1367, null);

        var before = FixtureLines;
        var after = edit.Content.Split('\n');
        Assert.Equal(before.Length + 1, after.Length); // the trailing newline makes one empty last entry
        for (var i = 0; i < before.Length; i++)
        {
            if (i is 15 or 22)
            {
                continue;
            }

            Assert.Equal(before[i], after[i]);
        }

        Assert.Equal("", after[^1]);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Apply_KeepsTheLineEndingStyle(string ending)
    {
        var edit = CamerasCfgEditor.Apply(Fixture(ending), 0.2, null);

        var other = ending == "\n" ? "\r\n" : "\n";
        Assert.DoesNotContain(other == "\n" ? "\n" : "\r", edit.Content.Replace(ending, ""), StringComparison.Ordinal);
        Assert.Equal(FixtureLines.Length, edit.Content.Split(ending).Length - 1);
        Assert.EndsWith(ending, edit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithoutATrailingNewline_DoesNotAddOne()
    {
        var content = string.Join("\n", FixtureLines);

        var edit = CamerasCfgEditor.Apply(content, 0.2, null);

        Assert.Equal(2, edit.BlocksChanged);
        Assert.EndsWith("InitialZoom = 0.35", edit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_KeepsABom()
    {
        var content = "\uFEFF" + Fixture("\r\n");

        var edit = CamerasCfgEditor.Apply(content, 0.2, null);

        Assert.Equal(2, edit.BlocksChanged);
        Assert.StartsWith("\uFEFF[VERSION]\r\n", edit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MatchesQuotedAndUnquotedValues_InAnyCase()
    {
        var content = string.Join("\n",
        [
            "[cameradefinition.7]",
            "category = cockpit",
            "subcategoryitem = ifrpilot",
            "initialzoom = 0.35",
            "",
            "[CAMERADEFINITION.8]",
            "Category = \"COCKPIT\"",
            "SubCategoryItem = \"ClosePilot\"",
            "InitialZoom = 0.35",
        ]);

        var edit = CamerasCfgEditor.Apply(content, 0.25, null);

        Assert.Equal(2, edit.BlocksChanged);
        Assert.Contains("initialzoom = 0.25", edit.Content, StringComparison.Ordinal);
        Assert.Contains("InitialZoom = 0.25", edit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithPitch_ReplacesOnlyTheFirstPbhComponent()
    {
        var edit = CamerasCfgEditor.Apply(Fixture("\n"), 0.1367, -7.6);

        var lines = edit.Content.Split('\n');
        Assert.Equal("InitialPbh = -7.6, 0, 0", lines[14]);
        Assert.Equal("InitialPbh = -7.6 ,  0.0 ,180", lines[21]); // odd spacing of bank/heading kept
        Assert.Equal("InitialPbh = 0, 0, 0", lines[28]);          // Custom1 untouched
        Assert.Equal("InitialPbh = 0, 0, 0", lines[35]);          // Chase untouched
    }

    [Fact]
    public void Apply_WithoutPitch_LeavesPbhAlone()
    {
        var edit = CamerasCfgEditor.Apply(Fixture("\n"), 0.1367, null);

        Assert.Contains("InitialPbh = 2.5, 0, 0", edit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_FormatsTheZoomWithUpToFourDecimals()
    {
        Assert.Contains("InitialZoom = 0.1367", CamerasCfgEditor.Apply(Fixture("\n"), 0.136724, null).Content, StringComparison.Ordinal);
        Assert.Contains("InitialZoom = 0.35", CamerasCfgEditor.Apply(Fixture("\n"), 0.35, null).Content, StringComparison.Ordinal);
        Assert.Contains("InitialZoom = 1", CamerasCfgEditor.Apply(Fixture("\n"), 1.0, null).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_NoPilotViewBlock_ReturnsZeroAndTheSameText()
    {
        var content = string.Join("\n", ["[VERSION]", "Major = 1", "", "[CAMERADEFINITION.0]", "Category = \"Chase\"", "SubCategoryItem = \"VFRPilot\"", "InitialZoom = 0.35"]);

        var edit = CamerasCfgEditor.Apply(content, 0.2, null);

        Assert.Equal(0, edit.BlocksChanged);
        Assert.Equal(content, edit.Content);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\SimObjects\Airplanes\prosim-a320\presets\x\y\config\cameras.CFG", true)]
    [InlineData(@"C:\Users\x\AppData\Roaming\Microsoft Flight Simulator 2024\LocalCache\SimObjects\Airplanes\a320\cameras.cfg", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Official\OneStore\asobo-aircraft-a320\SimObjects\Airplanes\Asobo_A320\cameras.cfg", false)]
    [InlineData(@"D:\MSFS\Packages\Community\some-aircraft\SimObjects\Airplanes\x\cameras.cfg", false)]
    [InlineData(@"D:\MSFS\Packages\Official\OneStore\x\SimObjects\Airplanes\x\cameras.cfg", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalState\WASM\MSFS2024\p42-util-chaseplane\work\cameras.cfg", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\SimObjects\Airplanes\x\aircraft.cfg", false)]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\SimObjects\Airplanes\..\..\Packages\Official\x\cameras.cfg", false)]
    [InlineData("", false)]
    public void IsWritablePath_OnlyALocalCacheCustomCamera(string path, bool expected)
        => Assert.Equal(expected, CamerasCfgEditor.IsWritablePath(path));

    [Fact]
    public void WriteWithBackup_RefusesAPathOutsideTheLocalCache()
    {
        using var temp = new TempTree();
        var file = temp.File(Path.Combine("Packages", "Official", "OneStore", "x", "SimObjects", "Airplanes", "x", "cameras.cfg"), "[VERSION]\n");

        var ex = Assert.Throws<InvalidOperationException>(() => CamerasCfgEditor.WriteWithBackup(file, "changed"));

        Assert.Contains("Refusing", ex.Message, StringComparison.Ordinal);
        Assert.Equal("[VERSION]\n", File.ReadAllText(file));
    }

    [Fact]
    public void WriteWithBackup_WritesTheNewText_AndKeepsTheOldOneAsABackup()
    {
        using var temp = new TempTree();
        var original = "\uFEFF[VERSION]\r\nMajor = 1\r\n";
        var file = temp.LocalCacheFile(original);

        var backup = CamerasCfgEditor.WriteWithBackup(file, "\uFEFF[VERSION]\r\nMajor = 2\r\n", new DateTime(2026, 10, 28, 9, 30, 0));

        Assert.Equal(Path.Combine(Path.GetDirectoryName(file)!, "cameras.cfg.bak-20261028-093000"), backup);
        Assert.Equal(new UTF8Encoding(false).GetBytes(original), File.ReadAllBytes(backup));
        Assert.Equal(new UTF8Encoding(false).GetBytes("\uFEFF[VERSION]\r\nMajor = 2\r\n"), File.ReadAllBytes(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void ReadRaw_KeepsTheBom_SoTheRewriteRoundTrips()
    {
        using var temp = new TempTree();
        var file = temp.LocalCacheFile("\uFEFF[VERSION]\r\n");

        Assert.StartsWith("\uFEFF", CamerasCfgEditor.ReadRaw(file), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteWithBackup_PrunesToTheNewestFiveBackups()
    {
        using var temp = new TempTree();
        var file = temp.LocalCacheFile("v0");
        var folder = Path.GetDirectoryName(file)!;

        for (var i = 1; i <= 7; i++)
        {
            CamerasCfgEditor.WriteWithBackup(file, "v" + i, new DateTime(2026, 10, 28, 10, 0, i));
        }

        var backups = Directory.GetFiles(folder, "cameras.cfg.bak-*").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(CamerasCfgEditor.BackupsKept, backups.Count);
        Assert.Equal("cameras.cfg.bak-20261028-100003", backups[0]); // the two oldest are gone
        Assert.Equal("cameras.cfg.bak-20261028-100007", backups[^1]);
        Assert.Equal("v7", File.ReadAllText(file));
        Assert.Equal("v6", File.ReadAllText(Path.Combine(folder, "cameras.cfg.bak-20261028-100007")));
    }

    [Fact]
    public void FindCandidateFiles_NarrowsToTheAircraftFolder_NewestFirst_AndFallsBackToAll()
    {
        using var temp = new TempTree();
        var a320 = temp.File(Path.Combine("LocalCache", "SimObjects", "Airplanes", "prosim-a320", "presets", "a", "config", "cameras.CFG"), "x");
        var b738 = temp.File(Path.Combine("LocalCache", "SimObjects", "Airplanes", "pmdg-737-800", "cameras.cfg"), "x");
        temp.File(Path.Combine("LocalCache", "SimObjects", "Airplanes", "prosim-a320", "aircraft.cfg"), "x");
        File.SetLastWriteTimeUtc(a320, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(b738, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var root = Path.Combine(temp.Root, "LocalCache");

        Assert.Equal([a320], CamerasCfgEditor.FindCandidateFiles(root, "ProSim-A320 Lufthansa"));
        Assert.Equal([a320], CamerasCfgEditor.FindCandidateFiles(root, "A320"));
        Assert.Equal([b738, a320], CamerasCfgEditor.FindCandidateFiles(root, "Fenix A321")); // no folder matches: everything, newest first
        Assert.Equal([b738, a320], CamerasCfgEditor.FindCandidateFiles(root, null));
        Assert.Empty(CamerasCfgEditor.FindCandidateFiles(Path.Combine(temp.Root, "nowhere"), null));
    }

    /// <summary>A throw-away folder tree; the LocalCache helper puts the file under the
    /// segments the write helper insists on.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", "cameras-" + Guid.NewGuid().ToString("N"));

        public string File(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
            return path;
        }

        public string LocalCacheFile(string content)
            => File(Path.Combine("LocalCache", "SimObjects", "Airplanes", "prosim-a320", "cameras.cfg"), content);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
