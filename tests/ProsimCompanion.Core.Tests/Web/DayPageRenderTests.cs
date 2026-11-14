using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.Tests.Localization;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// Renders the real Duty Day page (issue #239): an open day offers "Discard this day" beside
/// "End duty day"; no day and a closed day do not. The first render is never armed — the
/// confirm text needs a press.
/// </summary>
public sealed class DayPageRenderTests
{
    private sealed class FakeDayControl : IDayControl
    {
        public void StartDay()
        {
        }

        public void EndDay()
        {
        }

        public bool DiscardDay() => true;
    }

    private static async Task<string> RenderAsync(DaySnapshot snapshot, string language = "en")
    {
        var store = new DayStatusStore();
        store.Update(snapshot);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IDayControl>(new FakeDayControl());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddTestLocalizer(language);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProsimCompanion.Web.Pages.Day>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static DaySnapshot Snapshot(bool active, string state)
        => new(
            new DayView(active, state, 1, 1, 0, "EGLL", "LOWW", 0, 42, null),
            [new DayLegView(1, "EGLL", "LOWW", "BAW704", null, null, "2026-11-07T20:20:00Z", null, null, false, null, null)],
            null,
            DayId: "day-20261107-194915");

    [Fact]
    public async Task AnOpenDay_OffersDiscardBesideEnd_Unarmed()
    {
        var html = await RenderAsync(Snapshot(active: true, state: "OnLeg"));

        Assert.Contains("End duty day", html, StringComparison.Ordinal);
        Assert.Contains("Discard this day", html, StringComparison.Ordinal);
        Assert.Contains("btn danger day-discard", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("End duty day", StringComparison.Ordinal) < html.IndexOf("Discard this day", StringComparison.Ordinal));
        // The hint says what it does; the confirm step is not shown before a press.
        Assert.Contains("The unfinished leg is not logged", html, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIRM DISCARD", html, StringComparison.Ordinal);
        Assert.DoesNotContain("day-discard-note", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDay_AndAClosedDay_OfferNoDiscard()
    {
        var none = await RenderAsync(DaySnapshot.Empty);
        Assert.Contains("Start duty day", none, StringComparison.Ordinal);
        Assert.DoesNotContain("Discard this day", none, StringComparison.Ordinal);

        var ended = await RenderAsync(Snapshot(active: false, state: "Ended"));
        Assert.Contains("Start duty day", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("Discard this day", ended, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("de", "Diesen Tag verwerfen")]
    [InlineData("es", "Descartar esta jornada")]
    [InlineData("fr", "Abandonner cette journée")]
    [InlineData("ja", "この勤務日を破棄")]
    [InlineData("ko", "이 근무일 폐기")]
    [InlineData("nl", "Deze dag verwerpen")]
    public async Task TheDiscardButton_IsTranslated(string language, string text)
    {
        var html = await RenderAsync(Snapshot(active: true, state: "OnLeg"), language);
        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Discard this day", html, StringComparison.Ordinal);
    }
}
