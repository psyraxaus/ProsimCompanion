using Xunit;
using ProsimCompanion.Web.Components;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The pure rules behind the settings text fields' red/amber lines (2026-10-13).
/// Empty is never a problem; red (blocking) is for values that can never work; amber is
/// advisory (a path that is not on this PC).</summary>
public sealed class FieldValidationTests
{
    [Theory]
    [InlineData(TextFieldKind.Host, "")]
    [InlineData(TextFieldKind.Host, "   ")]
    [InlineData(TextFieldKind.Url, "")]
    [InlineData(TextFieldKind.Mac, "")]
    [InlineData(TextFieldKind.FilePath, "")]
    [InlineData(TextFieldKind.Text, "http://anything goes here")]
    public void Empty_or_plain_text_is_never_a_problem(TextFieldKind kind, string value)
        => Assert.Null(FieldValidation.Check(kind, value));

    [Theory]
    [InlineData("localhost")]
    [InlineData("192.168.1.10")]
    [InlineData("sim-pc")]
    [InlineData("sim-pc.local")]
    public void Host_accepts_names_and_addresses(string value)
        => Assert.Null(FieldValidation.Check(TextFieldKind.Host, value));

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void Host_rejects_ipv6_literals_the_consumers_cannot_build_a_url_from(string value)
    {
        var problem = FieldValidation.Check(TextFieldKind.Host, value);
        Assert.NotNull(problem);
        Assert.True(problem.Blocking);
        Assert.Contains("IPv6", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.10")]
    [InlineData("192.168.1.10:5000")]
    [InlineData("sim-pc/efb")]
    [InlineData("my pc")]
    public void Host_rejects_scheme_port_path_and_spaces(string value)
    {
        var problem = FieldValidation.Check(TextFieldKind.Host, value);
        Assert.NotNull(problem);
        Assert.True(problem.Blocking);
        Assert.Contains("No http://", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.50:8000")]
    [InlineData("https://api.elevenlabs.io")]
    [InlineData("http://localhost:11434/")]
    public void Url_accepts_absolute_http_and_https(string value)
        => Assert.Null(FieldValidation.Check(TextFieldKind.Url, value));

    [Theory]
    [InlineData("192.168.1.50:8000")]
    [InlineData("ftp://host/x")]
    [InlineData("localhost")]
    [InlineData("http://")]
    public void Url_rejects_anything_else(string value)
    {
        var problem = FieldValidation.Check(TextFieldKind.Url, value);
        Assert.NotNull(problem);
        Assert.True(problem.Blocking);
    }

    [Theory]
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.1.255", true)]
    [InlineData("192.168.1", false)]
    [InlineData("::1", false)]
    [InlineData("broadcast", false)]
    public void IpAddress_wants_ipv4(string value, bool ok)
        => Assert.Equal(ok, FieldValidation.Check(TextFieldKind.IpAddress, value) is null);

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", true)]
    [InlineData("aa-bb-cc-dd-ee-ff", true)]
    [InlineData("AABBCCDDEEFF", true)]
    [InlineData("AA:BB:CC:DD:EE", false)]
    [InlineData("AA:BB:CC:DD:EE:GG", false)]
    public void Mac_wants_six_hex_pairs(string value, bool ok)
        => Assert.Equal(ok, FieldValidation.Check(TextFieldKind.Mac, value) is null);

    [Theory]
    [InlineData("YSSY", true)]
    [InlineData("egll", true)]
    [InlineData("K1G4", true)]
    [InlineData("YSS", false)]
    [InlineData("YSSYX", false)]
    [InlineData("YS-Y", false)]
    public void Icao_wants_four_characters(string value, bool ok)
        => Assert.Equal(ok, FieldValidation.Check(TextFieldKind.Icao, value) is null);

    [Theory]
    [InlineData("16R", true)]
    [InlineData("34", true)]
    [InlineData("07c", true)]
    [InlineData("9", true)]
    [InlineData("37", false)]
    [InlineData("00", false)]
    [InlineData("16X", false)]
    [InlineData("RWY16", false)]
    public void Runway_wants_a_designator(string value, bool ok)
        => Assert.Equal(ok, FieldValidation.Check(TextFieldKind.Runway, value) is null);

    [Theory]
    [InlineData("1, 2, 3", true)]
    [InlineData("1,4", true)]
    [InlineData("1 2", true)]
    [InlineData("0", false)]
    [InlineData("1, two", false)]
    [InlineData("-1", false)]
    public void IntList_wants_positive_whole_numbers(string value, bool ok)
        => Assert.Equal(ok, FieldValidation.Check(TextFieldKind.IntList, value) is null);

    [Fact]
    public void FilePath_present_is_fine_and_missing_is_only_a_warning()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.Null(FieldValidation.Check(TextFieldKind.FilePath, file));
            // Explorer's "Copy as path" quotes are tolerated the way the pages strip them.
            Assert.Null(FieldValidation.Check(TextFieldKind.FilePath, "\"" + file + "\""));

            var missing = FieldValidation.Check(TextFieldKind.FilePath, file + ".nope");
            Assert.NotNull(missing);
            Assert.False(missing.Blocking);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FolderPath_and_OutputFilePath_look_at_the_folder()
    {
        var folder = Path.GetTempPath();
        Assert.Null(FieldValidation.Check(TextFieldKind.FolderPath, folder));
        Assert.Null(FieldValidation.Check(TextFieldKind.FileOrFolderPath, folder));
        Assert.Null(FieldValidation.Check(TextFieldKind.OutputFilePath, Path.Combine(folder, "not-yet-written.json")));

        var missingParent = FieldValidation.Check(TextFieldKind.OutputFilePath, Path.Combine(folder, "no-such-dir-" + Guid.NewGuid().ToString("N"), "x.json"));
        Assert.NotNull(missingParent);
        Assert.False(missingParent.Blocking);
    }

    [Fact]
    public void FolderList_names_each_missing_folder()
    {
        var present = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var absent = Path.Combine(present, "gone-" + Guid.NewGuid().ToString("N"));
        Assert.Null(FieldValidation.Check(TextFieldKind.FolderList, present + "; " + present));

        var problem = FieldValidation.Check(TextFieldKind.FolderList, present + ";" + absent);
        Assert.NotNull(problem);
        Assert.False(problem.Blocking);
        Assert.Contains(absent, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_path_characters_block()
    {
        var problem = FieldValidation.Check(TextFieldKind.FolderPath, "C:\\bad\0name");
        Assert.NotNull(problem);
        Assert.True(problem.Blocking);
    }

    [Fact]
    public void Validity_tally_counts_distinct_blocking_fields_and_raises_only_on_change()
    {
        var tally = new FieldValidity();
        var raised = 0;
        tally.Changed += () => raised++;

        tally.Report("a", blocking: true);
        tally.Report("a", blocking: true);
        tally.Report("b", blocking: true);
        Assert.Equal(2, tally.BlockingCount);
        Assert.Equal(2, raised);

        tally.Report("b", blocking: false);
        tally.Clear("a");
        tally.Clear("a");
        Assert.Equal(0, tally.BlockingCount);
        Assert.Equal(4, raised);
    }

    [Fact]
    public void Validity_tally_names_blocking_fields_and_reset_empties_it_once()
    {
        var tally = new FieldValidity();
        var raised = 0;
        tally.Changed += () => raised++;

        tally.Report("LAN ASR URL", blocking: true);
        tally.Report("Base URL", blocking: true);
        Assert.Equal(["Base URL", "LAN ASR URL"], tally.BlockingFields);

        tally.Reset();
        tally.Reset();
        Assert.Equal(0, tally.BlockingCount);
        Assert.Equal(3, raised);
    }
}
