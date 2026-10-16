using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logging;

/// <summary>The rules behind "verbose logging switches itself off" (2026-10-17): what counts
/// as verbose, when it reverts, what the quiet options look like, and when a reload is worth
/// a log line.</summary>
public sealed class VerboseLoggingPolicyTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 17, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Debug", true)]
    [InlineData("verbose", true)]
    [InlineData("Information", false)]
    [InlineData("Warning", false)]
    [InlineData(null, false)]
    public void IsVerboseLevel_RecognisesTheTwoLevelsBelowInformation(string? level, bool expected)
    {
        Assert.Equal(expected, VerboseLoggingPolicy.IsVerboseLevel(level));
    }

    [Fact]
    public void IsVerbose_CountsTheDefaultLevel_AnyOverride_AndTheWireTrace()
    {
        Assert.False(VerboseLoggingPolicy.IsVerbose(new LoggingOptions()));
        Assert.True(VerboseLoggingPolicy.IsVerbose(new LoggingOptions { DefaultLevel = "Debug" }));
        Assert.True(VerboseLoggingPolicy.IsVerbose(new LoggingOptions { WireTrace = true }));
        Assert.True(VerboseLoggingPolicy.IsVerbose(new LoggingOptions { Overrides = { ["ProsimCompanion.Gsx"] = "Verbose" } }));
        Assert.False(VerboseLoggingPolicy.IsVerbose(new LoggingOptions { Overrides = { ["ProsimCompanion.Gsx"] = "Warning" } }));
    }

    [Fact]
    public void RevertAt_IsOneWindowAfterTheStart_UnlessKeptOrQuiet()
    {
        var verbose = new LoggingOptions { DefaultLevel = "Debug" };

        Assert.Equal(Since + VerboseLoggingPolicy.Window, VerboseLoggingPolicy.RevertAt(verbose, Since));
        Assert.Null(VerboseLoggingPolicy.RevertAt(verbose, null));
        Assert.Null(VerboseLoggingPolicy.RevertAt(new LoggingOptions { DefaultLevel = "Debug", KeepVerbose = true }, Since));
        Assert.Null(VerboseLoggingPolicy.RevertAt(new LoggingOptions(), Since));
    }

    [Fact]
    public void Quiet_ClosesEveryVerboseStream_AndKeepsTheRest()
    {
        var options = new LoggingOptions
        {
            DefaultLevel = "Debug",
            WireTrace = true,
            KeepVerbose = false,
            MirrorToSession = LogMirrorLevel.ErrorAndAbove,
            Overrides = { ["ProsimCompanion.Gsx"] = "Verbose", ["Microsoft"] = "Error" },
        };

        var quiet = VerboseLoggingPolicy.Quiet(options);

        Assert.Equal("Information", quiet.DefaultLevel);
        Assert.False(quiet.WireTrace);
        Assert.Equal(LogMirrorLevel.ErrorAndAbove, quiet.MirrorToSession);
        Assert.Equal(["Microsoft"], quiet.Overrides.Keys);
        Assert.False(VerboseLoggingPolicy.IsVerbose(quiet));
        // A non-verbose default stays as the pilot set it.
        Assert.Equal("Warning", VerboseLoggingPolicy.Quiet(new LoggingOptions { DefaultLevel = "Warning", WireTrace = true }).DefaultLevel);
    }

    [Fact]
    public void SameLevels_IgnoresCasing_AndSeesEveryDrivingField()
    {
        var a = new LoggingOptions { DefaultLevel = "Debug", Overrides = { ["ProsimCompanion.Gsx"] = "Warning" } };
        var b = new LoggingOptions { DefaultLevel = "debug", Overrides = { ["prosimcompanion.gsx"] = "warning" } };

        Assert.True(VerboseLoggingPolicy.SameLevels(a, b));
        Assert.False(VerboseLoggingPolicy.SameLevels(a, new LoggingOptions { DefaultLevel = "Debug" }));
        Assert.False(VerboseLoggingPolicy.SameLevels(a, new LoggingOptions { DefaultLevel = "Debug", WireTrace = true, Overrides = { ["ProsimCompanion.Gsx"] = "Warning" } }));
        Assert.False(VerboseLoggingPolicy.SameLevels(a, new LoggingOptions { DefaultLevel = "Debug", MirrorToSession = LogMirrorLevel.Off, Overrides = { ["ProsimCompanion.Gsx"] = "Warning" } }));
        // KeepVerbose does not drive the pipeline, so it is not a reload.
        Assert.True(VerboseLoggingPolicy.SameLevels(a, new LoggingOptions { DefaultLevel = "Debug", KeepVerbose = true, Overrides = { ["ProsimCompanion.Gsx"] = "Warning" } }));
    }
}
