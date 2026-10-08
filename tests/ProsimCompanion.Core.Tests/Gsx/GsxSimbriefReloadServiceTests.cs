using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The GSX SimBrief reload (2026-10-09): a keyword pick on the gate menu, verified by
/// the handlerData patch, once per OFP, at the gate only.</summary>
public sealed class GsxSimbriefReloadServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-reload-").FullName;
    private readonly JsonlEventLog _eventLog;
    private readonly FakeGsxApi _api = new();
    private readonly Mock<ISimVars> _simVars = new();
    private readonly Mock<IFlightPhaseSource> _flight = new();
    private readonly OfpStore _ofp = new();
    private readonly GsxOptions _options = new() { MenuOpenTimeoutMs = 300, IntentVerifyTimeoutMs = 300 };
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly GsxSimbriefReloadService _service;

    public GsxSimbriefReloadServiceTests()
    {
        _eventLog = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        AtGate(FlightPhase.Preflight);
        var options = new FakeOptionsMonitor(_options);
        _service = new GsxSimbriefReloadService(
            _api,
            new GsxMenuIntentExecutor(_api, new GsxMenuOpener(_api, _simVars.Object, NullLogger<GsxMenuOpener>.Instance), options, NullLogger<GsxMenuIntentExecutor>.Instance),
            _ofp,
            _flight.Object,
            options,
            _diagnostics,
            _eventLog,
            NullLogger<GsxSimbriefReloadService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _service.Dispose();
        await _eventLog.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void AtGate(FlightPhase phase, bool engineRunning = false, double groundSpeed = 0)
    {
        _flight.SetupGet(f => f.CurrentPhase).Returns(phase);
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(
            phase,
            new FlightDataSnapshot { IsValid = true, OnGround = true, ParkBrakeSet = true, AnyEngineRunning = engineRunning, GroundSpeedKt = groundSpeed },
            false,
            true));
    }

    private void ShowMenu(string title, params string[] entries)
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = title,
            ["entries"] = new JsonArray([.. entries.Select(e => JsonValue.Create(e))]),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
    }

    /// <summary>GSX opens the gate menu on menu.open and, on the SimBrief pick, patches
    /// handlerData (integration guide §8.13) while re-showing the same menu.</summary>
    private void GsxWithSimbriefLine(bool patchHandlerData = true)
        => _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at Gate D57", "Reposition Aircraft", "Operate Jetway", "SimBrief", "Customize airplane");
            }

            if (verb == "menu.pick")
            {
                Assert.Equal(2, (int?)args?["index"]);
                if (patchHandlerData)
                {
                    _api.Mirror.ApplyState("handlerData", new JsonObject { ["airport"] = new JsonObject { ["icao"] = "EGLL" } });
                }
            }

            if (verb == "menu.close")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }

            return new GsxCommandResult(true, "ok", null, null);
        };

    [Fact]
    public async Task Manual_AtTheGate_PicksTheSimbriefLine_AndReportsReloaded()
    {
        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        GsxWithSimbriefLine();

        var outcome = await _service.ReloadAsync("web");

        Assert.Equal(GsxSimbriefReloadStatus.Reloaded, outcome.Status);
        Assert.Contains("handler data patched", outcome.Detail);
        Assert.Contains(_api.Commands, c => c.Verb == "menu.pick");
        Assert.Contains(_api.Commands, c => c.Verb == "menu.close"); // we opened it, we close it
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Action == "SimBrief reload" && d.Reason.StartsWith("web: Reloaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manual_PickAcknowledgedWithoutAnySignal_IsSentUnconfirmed()
    {
        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        GsxWithSimbriefLine(patchHandlerData: false);

        var outcome = await _service.ReloadAsync("command");

        Assert.Equal(GsxSimbriefReloadStatus.SentUnconfirmed, outcome.Status);
        Assert.Contains(_api.Commands, c => c.Verb == "menu.pick");
    }

    [Fact]
    public async Task NoSimbriefLine_IsNotPerformed_AndTheMenuStaysForTheUser()
    {
        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at Gate D57", "Reposition Aircraft", "Operate Jetway");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var outcome = await _service.ReloadAsync("web");

        Assert.Equal(GsxSimbriefReloadStatus.NotPerformed, outcome.Status);
        Assert.Contains("ItemNotAvailable", outcome.Detail);
        Assert.DoesNotContain(_api.Commands, c => c.Verb is "menu.pick" or "menu.close");
    }

    [Theory]
    [InlineData(FlightPhase.Cruise, false, 0)]
    [InlineData(FlightPhase.TaxiOut, false, 12)]
    [InlineData(FlightPhase.Preflight, true, 0)]
    public async Task NotParkedAtTheGate_IsRefused_WithoutTouchingTheMenu(FlightPhase phase, bool engine, double gs)
    {
        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        AtGate(phase, engine, gs);
        GsxWithSimbriefLine();

        var outcome = await _service.ReloadAsync("web");

        Assert.Equal(GsxSimbriefReloadStatus.NotPerformed, outcome.Status);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task NoOfp_OrGsxNotReady_IsRefused()
    {
        GsxWithSimbriefLine();
        Assert.Equal(GsxSimbriefReloadStatus.NotPerformed, (await _service.ReloadAsync("web")).Status);

        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        _api.Readiness = GsxReadiness.ConnectedGsxNotRunning;
        Assert.Equal(GsxSimbriefReloadStatus.NotPerformed, (await _service.ReloadAsync("web")).Status);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task Automatic_RunsOncePerOfpRequestId_OnlyWhenTheOptionIsOn()
    {
        GsxWithSimbriefLine();
        _service.Start();

        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        await Task.Delay(200);
        Assert.Empty(_api.Commands); // option off by default

        _options.ReloadSimbriefOnNewOfp = true;
        _ofp.Set(new OfpData { RequestId = "B", OriginIcao = "EGLL" });
        await WaitForPicksAsync(1);
        Assert.Single(_api.Commands, c => c.Verb == "menu.pick");

        _ofp.Set(new OfpData { RequestId = "B", OriginIcao = "EGLL" }); // same plan again
        await Task.Delay(200);
        Assert.Single(_api.Commands, c => c.Verb == "menu.pick");

        _ofp.Set(new OfpData { RequestId = "C", OriginIcao = "EGLL" });
        await WaitForPicksAsync(2);
        Assert.Equal(2, _api.Commands.Count(c => c.Verb == "menu.pick"));
    }

    [Fact]
    public async Task Automatic_PlanFetchedAirborne_WaitsForTheGate()
    {
        _options.ReloadSimbriefOnNewOfp = true;
        GsxWithSimbriefLine();
        AtGate(FlightPhase.Cruise);
        _service.Start();

        _ofp.Set(new OfpData { RequestId = "A", OriginIcao = "EGLL" });
        await Task.Delay(200);
        Assert.Empty(_api.Commands);

        AtGate(FlightPhase.Preflight);
        _flight.Raise(f => f.PhaseChanged += null, _flight.Object, new FlightPhaseChangedEventArgs(FlightPhase.Cruise, FlightPhase.Preflight));
        await WaitForPicksAsync(1);
        Assert.Single(_api.Commands, c => c.Verb == "menu.pick");
    }

    private async Task WaitForPicksAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && _api.Commands.Count(c => c.Verb == "menu.pick") < count)
        {
            await Task.Delay(25);
        }
    }

    private sealed class FakeGsxApi : IGsxRemoteApi
    {
#pragma warning disable CS0067
        public event Action<GsxReadiness>? ReadinessChanged;
#pragma warning restore CS0067

        public GsxReadiness Readiness { get; set; } = GsxReadiness.Ready;
        public GsxStateMirror Mirror { get; } = new();
        public List<(string Verb, JsonObject? Args)> Commands { get; } = [];
        public Func<string, JsonObject?, GsxCommandResult>? OnCommand { get; set; }

        public bool HasCapability(string token) => true;

        public Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default)
        {
            lock (Commands)
            {
                Commands.Add((verb, args));
            }

            return Task.FromResult(OnCommand?.Invoke(verb, args) ?? new GsxCommandResult(true, "ok", null, null));
        }
    }

    private sealed class FakeOptionsMonitor(GsxOptions value) : IOptionsMonitor<GsxOptions>
    {
        public GsxOptions CurrentValue { get; } = value;
        public GsxOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GsxOptions, string?> listener) => null;
    }
}
