using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>Issue #154: the Fuel Log page renders the empty state without an OFP and the
/// hero / chart / table / checks with a log in progress.</summary>
public sealed class FuelLogRenderTests
{
    private static readonly DateTimeOffset Takeoff = new(2026, 10, 4, 13, 7, 0, TimeSpan.Zero);

    private static OfpData Ofp() => new()
    {
        RequestId = "r1",
        OriginIcao = "EGLL",
        DestinationIcao = "EKCH",
        FuelPlanLandingKg = 4120,
        Navlog =
        [
            new OfpFix("EGLL", new GeoPoint(51.47, -0.46), 9600, TimeSpan.Zero, 0, false),
            new OfpFix("DET", new GeoPoint(51.30, 0.60), 8850, TimeSpan.FromMinutes(16), 20000, false),
            new OfpFix("KONAN", new GeoPoint(51.08, 2.00), 8390, TimeSpan.FromMinutes(26), 30000, false),
            new OfpFix("KOK", new GeoPoint(51.10, 2.65), 8180, TimeSpan.FromMinutes(31), 36000, false),
            new OfpFix("LNO", new GeoPoint(51.55, 4.70), 7520, TimeSpan.FromMinutes(47), 36000, false),
            new OfpFix("ARPOP", new GeoPoint(52.20, 6.50), 6900, TimeSpan.FromMinutes(63), 36000, false),
            new OfpFix("RIDSU", new GeoPoint(53.00, 8.60), 6180, TimeSpan.FromMinutes(82), 36000, false),
            new OfpFix("SPY", new GeoPoint(53.80, 10.30), 5660, TimeSpan.FromMinutes(96), 36000, false),
            new OfpFix("TOD", new GeoPoint(54.60, 11.70), 5020, TimeSpan.FromMinutes(113), 36000, false),
            new OfpFix("MAKIK", new GeoPoint(55.20, 12.30), 4640, TimeSpan.FromMinutes(126), 12000, false),
            new OfpFix("EKCH", new GeoPoint(55.62, 12.65), 4120, TimeSpan.FromMinutes(145), 0, false),
        ],
    };

    private static async Task<string> RenderAsync(bool withPlan, FuelLogStore log, FuelCheckLogStore checks, GeoPoint? position, double? fob)
    {
        var ofp = new OfpStore();
        if (withPlan)
        {
            ofp.Set(Ofp());
        }

        var progress = new FlightProgressStore();
        if (position is { } p)
        {
            progress.Update(s => s with { Position = p });
        }

        var dataRefs = new FakeDataRefs();
        if (fob is { } f)
        {
            dataRefs.Values[ProsimDataRefNames.FuelTotal.Name] = f;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IProsimDataRefs>(dataRefs);
        services.AddSingleton(ofp);
        services.AddSingleton(log);
        services.AddSingleton(checks);
        services.AddSingleton(progress);
        services.AddSingleton(new DisplayUnitService(dataRefs, new FixedOptionsMonitor<WebUiOptions>(new WebUiOptions())));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProsimCompanion.Web.Pages.FuelLog>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    [Fact]
    public async Task WithoutAnOfp_ShowsTheEmptyState()
    {
        var html = await RenderAsync(false, new FuelLogStore(), new FuelCheckLogStore(), null, null);

        Assert.Contains("No fuel plan loaded", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fuellog-table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidFlight_RendersHeroChartTableAndChecks()
    {
        var ofp = Ofp();
        var store = new FuelLogStore();
        var log = FuelLogSnapshot.Empty;
        // Fly the route to just past SPY, stamping as we go.
        var actuals = new (double Lat, double Lon, double Fob, int Min)[]
        {
            (51.40, -0.30, 9576, 1), (51.25, 0.80, 8790, 16), (51.08, 2.10, 8310, 27), (51.12, 2.80, 8090, 32),
            (51.60, 4.90, 7390, 49), (52.25, 6.70, 6740, 66), (53.05, 8.80, 6020, 85), (53.85, 10.45, 5530, 98),
        };
        foreach (var (lat, lon, fobKg, min) in actuals)
        {
            (log, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(lat, lon), fobKg, Takeoff, Takeoff.AddMinutes(min));
        }

        store.Update(_ => log);
        var checks = new FuelCheckLogStore();
        checks.Add(new FuelCheckRecord(Takeoff.AddMinutes(37), "periodic", "navlog", "KOK", 8090, 8180, -90, 4030, 4120, false,
            "Fuel check. Past Kok, fuel on board eight point one tonnes, 90 kilos below plan. Estimated landing four point zero tonnes against four point one planned."));
        checks.Add(new FuelCheckRecord(Takeoff.AddMinutes(97), "periodic", "navlog", "SPY", 5530, 5660, -130, 3990, 4120, false,
            "Fuel check. Passing Spy, fuel on board five point five tonnes, 130 kilos below plan. Estimated landing four point zero tonnes against four point one planned."));

        var html = await RenderAsync(true, store, checks, new GeoPoint(54.1, 10.9), 5470);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "fuellog-render.html"), html);

        Assert.Equal(8, log.PassedCount);
        Assert.Contains("Plan vs actual", html, StringComparison.Ordinal);
        Assert.Contains("between SPY and TOD", html, StringComparison.Ordinal);
        Assert.Contains("fuellog-chart", html, StringComparison.Ordinal);
        Assert.Contains("PLANNED LANDING 4,120", html, StringComparison.Ordinal);
        Assert.Contains("8 of 11 fixes passed", html, StringComparison.Ordinal);
        Assert.Contains("SPY<span class=\"pill tone-active\">last</span>", html, StringComparison.Ordinal);
        Assert.Contains("Passing Spy", html, StringComparison.Ordinal);
        Assert.Contains(">holding<", html, StringComparison.Ordinal);                    // −160 · −160 · −130 at the last three fixes
    }
}
