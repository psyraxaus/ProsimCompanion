using ProsimCompanion.Reduce;
using Xunit;

namespace ProsimCompanion.Core.Tests.Reduce;

/// <summary>
/// The reducer's command-line guard: a path is accepted only below an allowed folder, and
/// outside text reaches the console as one line.
/// </summary>
public sealed class CliInputTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pc-cli-input");

    [Fact]
    public void ResolvePath_BelowTheRoot_IsTheAbsolutePath()
    {
        var inside = Path.Combine(Root, "bundles", "one.zip");
        Assert.Equal(Path.GetFullPath(inside), CliInput.ResolvePath(inside, [Root]));
    }

    [Fact]
    public void ResolvePath_TheRootItself_IsAccepted()
        => Assert.Equal(Path.GetFullPath(Root), CliInput.ResolvePath(Root, [Root]));

    [Fact]
    public void ResolvePath_ClimbingOutOfTheRoot_IsRefused()
        => Assert.Null(CliInput.ResolvePath(Path.Combine(Root, "..", "elsewhere", "one.zip"), [Root]));

    [Fact]
    public void ResolvePath_ASiblingThatSharesTheRootsPrefix_IsRefused()
        => Assert.Null(CliInput.ResolvePath(Root + "-other" + Path.DirectorySeparatorChar + "one.zip", [Root]));

    [Fact]
    public void ResolvePath_AnyOfTheRoots_IsEnough()
    {
        var second = Path.Combine(Path.GetTempPath(), "pc-cli-input-second");
        var inside = Path.Combine(second, "report.json");
        Assert.Equal(Path.GetFullPath(inside), CliInput.ResolvePath(inside, [Root, second]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_Blank_IsRefused(string path)
        => Assert.Null(CliInput.ResolvePath(path, [Root]));

    [Fact]
    public void ResolvePath_Default_AllowsTheWorkingFolderAndTheTempFolder()
    {
        Assert.NotNull(CliInput.ResolvePath("report.json"));
        Assert.NotNull(CliInput.ResolvePath(Path.Combine(Path.GetTempPath(), "report.json")));
    }

    [Fact]
    public void FindExisting_WalksToTheRealEntry_AndRefusesWhatIsNotThere()
    {
        var root = Directory.CreateTempSubdirectory("pc-cli-find-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "bundles")).FullName;
            var file = Path.Combine(folder, "one.zip");
            File.WriteAllText(file, "x");

            Assert.Equal(file, CliInput.FindExisting(file, [root]));
            Assert.Equal(folder, CliInput.FindExisting(folder + Path.DirectorySeparatorChar, [root]));
            Assert.Equal(root, CliInput.FindExisting(root, [root]));
            Assert.Equal(file, CliInput.FindExisting(Path.Combine(root, "bundles", "..", "bundles", "one.zip"), [root]));
            Assert.Null(CliInput.FindExisting(Path.Combine(folder, "missing.zip"), [root]));
            Assert.Null(CliInput.FindExisting(Path.Combine(folder, "..", "bundles-other", "one.zip"), [folder]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OutputFile_NeedsAnExistingFolderBelowTheRoot_TheFileMayBeNew()
    {
        var root = Directory.CreateTempSubdirectory("pc-cli-out-").FullName;
        try
        {
            Assert.Equal(Path.Combine(root, "report.json"), CliInput.OutputFile(Path.Combine(root, "report.json"), [root]));
            Assert.Null(CliInput.OutputFile(Path.Combine(root, "missing", "report.json"), [root]));
            Assert.Null(CliInput.OutputFile(Path.Combine(root, "..", "report.json"), [root]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SingleLine_RemovesEveryLineBreak()
    {
        Assert.Equal("report.jsonbundle refused: forged", CliInput.SingleLine("report.json\r\nbundle refused: forged"));
        Assert.Equal("ab", CliInput.SingleLine("a\nb"));
        Assert.Equal(string.Empty, CliInput.SingleLine(null));
    }
}
