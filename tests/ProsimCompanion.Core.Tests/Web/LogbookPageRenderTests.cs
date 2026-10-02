using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Logbook;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// Renders the real Logbook page to HTML (issue #146). A Razor mistake only shows at render
/// time — #142 was a page that compiled and threw on every render — so this holds the page
/// to "renders, with the totals, the table and the trend the service's data implies".
/// </summary>
public sealed class LogbookPageRenderTests
{
    private static async Task<string> RenderAsync(ILogbookService? logbook)
    {
        var services = new ServiceCollection();
        if (logbook is not null)
        {
            services.AddSingleton(logbook);
        }

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProsimCompanion.Web.Pages.Logbook>(ParameterView.Empty);
            // Decoded: the renderer writes "→" and "—" as character references.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static ILogbookService Service(params LogbookFlight[] flights)
    {
        var rates = flights.Where(f => f.TouchdownVerticalSpeedFpm is not null).Select(f => f.TouchdownVerticalSpeedFpm!.Value).ToList();
        var logbook = new Mock<ILogbookService>();
        logbook.SetupGet(l => l.Flights).Returns(flights);
        logbook.Setup(l => l.GetAggregates()).Returns(new LogbookAggregates(
            flights.Length, 4.2, 3.7, flights.Count(f => f.Landed), 1, 2, [],
            rates.Count > 0 ? Math.Round(rates.Average()) : null, rates.Count));
        return logbook.Object;
    }

    [Fact]
    public async Task WithFlights_RendersTotalsTrendAndTable_NewestFirst()
    {
        var html = await RenderAsync(Service(
            new LogbookFlight
            {
                SessionId = "session-20260913-070512", Date = "2026-09-13", Origin = "EGLL", Destination = "LIRF",
                BlockMinutes = 152, FlightMinutes = 121, Landed = true, TouchdownGroundSpeedKt = 133.5, ApproachResult = "stable",
            },
            new LogbookFlight
            {
                SessionId = "session-20261003-080000", Date = "2026-10-03", Origin = "LIRF", Destination = "EGLL",
                BlockMinutes = 130, FlightMinutes = 112, Landed = true, TouchdownGroundSpeedKt = 131,
                TouchdownVerticalSpeedFpm = -183, Bounces = 1, ApproachResult = "unstable",
            }));

        Assert.Contains("Pilot Logbook", html, StringComparison.Ordinal);
        Assert.Contains("href=\"api/logbook/export.csv\"", html, StringComparison.Ordinal);
        Assert.Contains("50 %", html, StringComparison.Ordinal);                // 1 stable of 2 judged
        Assert.Contains("-183<span class=\"figure-unit\">FPM</span>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"lb-spark\"", html, StringComparison.Ordinal);
        Assert.Contains("lb-spark-last", html, StringComparison.Ordinal);
        Assert.Contains("<title>2026-10-03 LIRF → EGLL · -183 fpm</title>", html, StringComparison.Ordinal);
        Assert.Contains("1 bounce<", html, StringComparison.Ordinal);
        Assert.Contains("2h 32m", html, StringComparison.Ordinal);
        Assert.Contains("tone-bad", html, StringComparison.Ordinal);
        // Newest first; the flight from before the recorder shows a dash, not a zero.
        Assert.True(html.IndexOf("2026-10-03", StringComparison.Ordinal) < html.IndexOf("2026-09-13", StringComparison.Ordinal));
        // No row is selected on load: the drawer and its delete button are absent.
        Assert.DoesNotContain("lb-drawer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete flight", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLogbook_RendersTheEmptyStates_NotABrokenChart()
    {
        var html = await RenderAsync(Service());

        Assert.Contains("The logbook is empty.", html, StringComparison.Ordinal);
        Assert.Contains("No measured landing yet.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg class=\"lb-spark\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLogbookService_DegradesToANotice()
    {
        var html = await RenderAsync(null);

        Assert.Contains("The logbook service is not wired into this build.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Export CSV", html, StringComparison.Ordinal);
    }
}
