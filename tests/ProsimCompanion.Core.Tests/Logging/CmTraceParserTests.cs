using ProsimCompanion.Core.Logging;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logging;

/// <summary>The CMTrace reader must round-trip what the writer emits, including the
/// multi-line exception form and a non-zero UTC offset.</summary>
public sealed class CmTraceParserTests
{
    [Fact]
    public void Parse_RoundTripsTheWriterFormat()
    {
        var at = new DateTimeOffset(2026, 9, 26, 14, 5, 6, 789, TimeSpan.FromHours(10));
        var text = CmTraceFormat.Line(at, "ProsimCompanion 0.5.0 (abc) starting", "Program", "ProsimCompanion.App.Program", 1, 3) + "\n"
            + CmTraceFormat.Line(at.AddSeconds(1), "Gate refused\r\nSystem.IO.IOException: boom\r\n   at X.Y()", "GsxClient", "ProsimCompanion.Gsx.GsxClient", 3, 12) + "\n"
            + "garbage line that is not a record\n";

        var entries = CmTraceParser.Parse(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal(at, entries[0].Timestamp);
        Assert.Equal("Information", entries[0].Level);
        Assert.Equal("Program", entries[0].Component);
        Assert.Equal("ProsimCompanion.App.Program", entries[0].Source);
        Assert.Equal(3, entries[0].ThreadId);
        Assert.Equal("Error", entries[1].Level);
        Assert.StartsWith("Gate refused", entries[1].Message, StringComparison.Ordinal);
        Assert.Contains("at X.Y()", entries[1].Message, StringComparison.Ordinal);
        Assert.Equal(12, entries[1].ThreadId);
    }

    [Fact]
    public void Parse_NegativeOffset_AndWarningSeverity()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(-5));
        var entries = CmTraceParser.Parse(CmTraceFormat.Line(at, "careful", "C", "S", 2, 0));

        var entry = Assert.Single(entries);
        Assert.Equal(at, entry.Timestamp);
        Assert.Equal("Warning", entry.Level);
    }
}
