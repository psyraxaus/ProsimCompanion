using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Gate;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Core.Theming;
using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// Renders the real Flight Monitor page to HTML in flight mode (issue #145). The route strip
/// is inline SVG written in Razor, and a Razor mistake only shows at render time — #142 was
/// exactly that, a page that compiled and threw on every render. This holds the strip to
/// "renders, with the aircraft where the progress store says it is".
/// </summary>
public sealed class MonitorRouteStripRenderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly string _logoDirectory = Path.Combine(Path.GetTempPath(), $"monitor-logos-{Guid.NewGuid():N}");
    private readonly FlightProgressStore _progress = new();

    private async Task<string> RenderAsync(FlightPhase phase)
    {
        var data = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            GroundSpeedKt = 450,
            AltitudeFt = 36000,
            FmsCruiseAltFt = 36000,
            Position = new GeoPoint(0, 13),
            TrackTrueDeg = 120,
        };
        var flight = new Mock<IFlightPhaseSource>();
        flight.SetupGet(f => f.CurrentPhase).Returns(phase);
        flight.SetupGet(f => f.IsLive).Returns(true);
        flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(phase, data, true, true));

        var clock = new Mock<ISimClock>();
        clock.SetupGet(c => c.UtcNowOrReal).Returns(Now);

        var ofp = new OfpStore();
        ofp.Set(new OfpData
        {
            Callsign = "KLM1023",
            OriginIcao = "AAAA",
            DestinationIcao = "BBBB",
            EstimatedEnroute = TimeSpan.FromMinutes(120),
        });

        var times = new FlightTimesStore();
        times.Update(_ => new FlightTimesSnapshot(Now.AddMinutes(-60), Now.AddMinutes(-45), null, null));

        var dataRefs = new FakeDataRefs();
        var services = new ServiceCollection();
        services.AddSingleton(flight.Object);
        services.AddSingleton(Mock.Of<IGsxDepartureControl>());
        services.AddSingleton(new GsxDiagnosticsStore());
        services.AddSingleton(new GateStatusStore());
        services.AddSingleton(new HeroWeatherStore());
        services.AddSingleton(times);
        services.AddSingleton(_progress);
        services.AddSingleton(ofp);
        services.AddSingleton(new ConnectionStatusStore());
        services.AddSingleton(new ArrivalGateCoordinator(
            flight.Object, ofp, null, null, NullLogger<ArrivalGateCoordinator>.Instance));
        services.AddSingleton(new AirlineLogoStore(_logoDirectory));
        services.AddSingleton<IProsimDataRefs>(dataRefs);
        services.AddSingleton(clock.Object);
        services.AddSingleton(new DisplayUnitService(dataRefs, new FixedOptionsMonitor<WebUiOptions>(new WebUiOptions())));
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProsimCompanion.Web.Pages.Monitor>(ParameterView.Empty);
            // Decoded: the renderer writes "·" and "—" as character references.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private void PublishPositionProgress()
    {
        var core = new FlightProgressCore();
        var origin = new AirportLocation("AAAA", new GeoPoint(0, 10), 0, "test");
        var destination = new AirportLocation("BBBB", new GeoPoint(0, 20), 1000, "test");
        var data = new FlightDataSnapshot
        {
            IsValid = true,
            GroundSpeedKt = 450,
            AltitudeFt = 36000,
            Position = new GeoPoint(0, 13),
            TrackTrueDeg = 120,
        };
        var (snapshot, _) = core.Evaluate(
            new FlightProgressInputs(
                FlightPhase.Cruise, true, data,
                new FlightTimesSnapshot(Now.AddMinutes(-60), Now.AddMinutes(-45), null, null),
                new OfpData { EstimatedEnroute = TimeSpan.FromMinutes(120) }, null, origin, destination, null),
            Now, 10);
        _progress.Update(_ => snapshot);
    }

    [Fact]
    public async Task FlightMode_WithAPosition_RendersTheStripTheMarkerAndTheDirectDistance()
    {
        PublishPositionProgress();

        var html = await RenderAsync(FlightPhase.Cruise);

        Assert.Contains("class=\"mon-strip\"", html, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 1200 72\"", html, StringComparison.Ordinal);
        // 3° of 10° along the leg: x = 40 + 0.3 × 1120 = 376, on the line, turned 30° right
        // (tracking 120 on a 090 route).
        Assert.Contains("translate(376 36) rotate(30)", html, StringComparison.Ordinal);
        Assert.Contains("mon-strip-aircraft", html, StringComparison.Ordinal);
        Assert.Contains("mon-strip-tod", html, StringComparison.Ordinal);
        Assert.Contains("T/D EST", html, StringComparison.Ordinal);
        Assert.Contains("TO GO 420 NM DIRECT", html, StringComparison.Ordinal);
        Assert.Contains("Great-circle direct · from position", html, StringComparison.Ordinal);
        Assert.Contains("ETA 10:56Z GS", html, StringComparison.Ordinal);
        // Flight mode shows the strip INSTEAD of the bar.
        Assert.DoesNotContain("mon-bar-fill", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlightMode_WithoutAPosition_FallsBackToTheTimeBasedMarker()
    {
        // The store has never ticked: the board computes the time fraction itself
        // (60 of 120 minutes) and says so.
        var html = await RenderAsync(FlightPhase.Cruise);

        Assert.Contains("class=\"mon-strip\"", html, StringComparison.Ordinal);
        Assert.Contains("translate(600 36) rotate(0)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mon-strip-tod", html, StringComparison.Ordinal);
        Assert.Contains("TO GO — DIRECT", html, StringComparison.Ordinal);
        Assert.Contains("ETA 11:15Z", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ETA 11:15Z GS", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateMode_KeepsTheBar_AndShowsNoStrip()
    {
        var html = await RenderAsync(FlightPhase.Preflight);

        Assert.Contains("mon-bar-fill", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mon-strip", html, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logoDirectory))
        {
            Directory.Delete(_logoDirectory, recursive: true);
        }
    }
}
