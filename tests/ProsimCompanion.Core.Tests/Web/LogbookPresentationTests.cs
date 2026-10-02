using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

public sealed class LogbookPresentationTests
{
    private static LogbookFlight Flight(
        string day, double? rate = null, int? block = null, string? origin = "EGLL", string? destination = "LIRF",
        string? approach = null, bool landed = true)
        => new()
        {
            SessionId = $"session-202610{day}-080000",
            Date = $"2026-10-{day}",
            Origin = origin,
            Destination = destination,
            BlockMinutes = block,
            TouchdownVerticalSpeedFpm = rate,
            ApproachResult = approach,
            Landed = landed,
        };

    private static string Days(IEnumerable<LogbookFlight> flights) => string.Join(",", flights.Select(f => f.Date[^2..]));

    // Append (fold) order, oldest first — as the store returns them.
    private static readonly LogbookFlight[] Book =
    [
        Flight("01", rate: -250, block: 90, approach: "stable"),
        Flight("02", rate: null, block: 200, destination: "EHAM"),
        Flight("03", rate: -120, block: null, origin: "EHAM", destination: "EGLL", approach: "unstable"),
        Flight("04", rate: -400, block: 45, approach: "stable"),
    ];

    [Fact]
    public void Sorted_ByDate_NewestFirstByDefault()
    {
        Assert.Equal("04,03,02,01", Days(LogbookPresentation.Sorted(Book, LogbookSort.Date, descending: true)));
        Assert.Equal("01,02,03,04", Days(LogbookPresentation.Sorted(Book, LogbookSort.Date, descending: false)));
    }

    [Fact]
    public void Sorted_FlightsWithNoValue_GoLast_InBothDirections()
    {
        // Descending rate = softest first (−120 > −250 > −400); the unmeasured flight is last.
        Assert.Equal("03,01,04,02", Days(LogbookPresentation.Sorted(Book, LogbookSort.TouchdownRate, descending: true)));
        // Ascending = firmest first; the unmeasured flight is STILL last.
        Assert.Equal("04,01,03,02", Days(LogbookPresentation.Sorted(Book, LogbookSort.TouchdownRate, descending: false)));
        Assert.Equal("02,01,04,03", Days(LogbookPresentation.Sorted(Book, LogbookSort.Block, descending: true)));
    }

    [Fact]
    public void Sorted_Ties_KeepNewestFirst()
        => Assert.Equal("04,01,03,02", Days(LogbookPresentation.Sorted(Book, LogbookSort.Approach, descending: false)));

    [Fact]
    public void Sorted_ByRoute_IsOrdinal()
        => Assert.Equal("02,04,01,03", Days(LogbookPresentation.Sorted(Book, LogbookSort.Route, descending: false)));

    [Fact]
    public void Sparkline_PlotsMeasuredLandingsOldestLeft_FirmLow()
    {
        var spark = LogbookPresentation.Sparkline(Book);

        Assert.Equal(3, spark.Points.Count);                    // the unmeasured flight is skipped
        Assert.Equal(LogbookSparkline.Pad, spark.Points[0].X);
        Assert.Equal(LogbookSparkline.Width - LogbookSparkline.Pad, spark.Points[^1].X);
        Assert.Equal(LogbookSparkline.Pad, spark.Points[1].Y);                              // −120, the softest: top
        Assert.Equal(LogbookSparkline.Height - LogbookSparkline.Pad, spark.Points[2].Y);    // −400, the firmest: bottom
        Assert.Equal("2026-10-01 EGLL → LIRF · -250 fpm", spark.Points[0].Label);
        Assert.StartsWith("M8 ", spark.Path, StringComparison.Ordinal);
        Assert.Equal(2, spark.Path.Count(c => c == 'L'));
        Assert.InRange(spark.AverageY!.Value, LogbookSparkline.Pad, LogbookSparkline.Height - LogbookSparkline.Pad);
    }

    [Fact]
    public void Sparkline_DegenerateInputs_DoNotDivideByZero()
    {
        var none = LogbookPresentation.Sparkline([Flight("01")]);
        var one = LogbookPresentation.Sparkline([Flight("01", rate: -200)]);
        var flat = LogbookPresentation.Sparkline([Flight("01", rate: -200), Flight("02", rate: -200)]);
        var notLanded = LogbookPresentation.Sparkline([Flight("01", rate: -200, landed: false)]);

        Assert.Empty(none.Points);
        Assert.Equal("", none.Path);
        var only = Assert.Single(one.Points);
        Assert.Equal(LogbookSparkline.Width / 2, only.X);
        Assert.Equal(LogbookSparkline.Height / 2, only.Y);
        Assert.Equal("", one.Path);
        Assert.Null(one.AverageY);
        Assert.All(flat.Points, p => Assert.Equal(LogbookSparkline.Height / 2, p.Y));
        Assert.Empty(notLanded.Points);
    }

    [Fact]
    public void Sparkline_ShowsOnlyTheMostRecentLandings()
    {
        var many = Enumerable.Range(0, 45)
            .Select(i => new LogbookFlight { SessionId = $"s{i:00}", Date = $"d{i:00}", Landed = true, TouchdownVerticalSpeedFpm = -100 - i })
            .ToList();

        var spark = LogbookPresentation.Sparkline(many);

        Assert.Equal(LogbookPresentation.SparklineLandings, spark.Points.Count);
        Assert.StartsWith("d15", spark.Points[0].Label, StringComparison.Ordinal);
        Assert.StartsWith("d44", spark.Points[^1].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Figures_Format()
    {
        Assert.Equal("-183", LogbookPresentation.Rate(-183.4));
        Assert.Equal("—", LogbookPresentation.Rate(null));
        Assert.Equal("1h 52m", LogbookPresentation.Minutes(112));
        Assert.Equal("48m", LogbookPresentation.Minutes(48));
        Assert.Equal("—", LogbookPresentation.Minutes(null));
        Assert.Equal("131", LogbookPresentation.Knots(131.4));
        Assert.Equal("+4.6°", LogbookPresentation.Degrees(4.6));
        Assert.Equal("-1.0°", LogbookPresentation.Degrees(-1));
        Assert.Equal("2026-10-03 08:00Z", LogbookPresentation.Stamp(new DateTimeOffset(2026, 10, 3, 10, 0, 5, TimeSpan.FromHours(2))));
        Assert.Equal("—", LogbookPresentation.Stamp(null));
        Assert.Equal("92 %", LogbookPresentation.Percent(92));
        Assert.Equal("—", LogbookPresentation.Percent(null));
        Assert.Equal("? → LIRF", LogbookPresentation.Route(new LogbookFlight { Destination = "LIRF" }));
        Assert.Equal("—", LogbookPresentation.Route(new LogbookFlight()));
        Assert.Equal("tone-ok", LogbookPresentation.ApproachTone("stable"));
        Assert.Equal("tone-bad", LogbookPresentation.ApproachTone("unstable"));
        Assert.Equal("tone-neutral", LogbookPresentation.ApproachTone(null));
    }
}
