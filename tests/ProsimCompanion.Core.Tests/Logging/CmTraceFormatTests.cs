using ProsimCompanion.Core.Logging;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logging;

public sealed class CmTraceFormatTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 1, 14, 30, 45, 123, TimeSpan.FromHours(10));

    [Fact]
    public void Line_ProducesCmTraceStructure()
    {
        var line = CmTraceFormat.Line(
            Timestamp,
            "Connected to ProSim at localhost",
            "SdkConnection",
            "ProsimCompanion.Prosim.Sdk.SdkConnection",
            CmTraceFormat.SeverityInfo,
            12);

        Assert.StartsWith("<![LOG[Connected to ProSim at localhost]LOG]!>", line, StringComparison.Ordinal);
        Assert.Contains("time=\"14:30:45.123+600\"", line, StringComparison.Ordinal);
        Assert.Contains("date=\"08-01-2026\"", line, StringComparison.Ordinal);
        Assert.Contains("component=\"SdkConnection\"", line, StringComparison.Ordinal);
        Assert.Contains("type=\"1\"", line, StringComparison.Ordinal);
        Assert.Contains("thread=\"12\"", line, StringComparison.Ordinal);
        Assert.Contains("file=\"ProsimCompanion.Prosim.Sdk.SdkConnection\"", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CmTraceFormat.SeverityWarning, "type=\"2\"")]
    [InlineData(CmTraceFormat.SeverityError, "type=\"3\"")]
    public void Line_MapsSeverity(int severity, string expected)
    {
        var line = CmTraceFormat.Line(Timestamp, "msg", "C", "S", severity, 1);

        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_NegativeUtcOffset_FormatsSignedMinutes()
    {
        var timestamp = new DateTimeOffset(2026, 8, 1, 6, 0, 0, TimeSpan.FromHours(-5));

        var line = CmTraceFormat.Line(timestamp, "msg", "C", "S", CmTraceFormat.SeverityInfo, 1);

        Assert.Contains("time=\"06:00:00.000-300\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_MessageContainingCloseMarker_IsSanitized()
    {
        var line = CmTraceFormat.Line(Timestamp, "evil ]LOG]!> payload", "C", "S", CmTraceFormat.SeverityInfo, 1);

        Assert.Contains("<![LOG[evil ]LOG]! > payload]LOG]!>", line, StringComparison.Ordinal);
    }
}
