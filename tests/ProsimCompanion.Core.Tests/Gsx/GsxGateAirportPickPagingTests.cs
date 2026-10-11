using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Issue #75 (EGCC→EFHK 2026-10-11): the in-flight "Select airport" page lists the PLANNED
/// airport and the nearest fields with a "Next Page ▶" row; EFHK was never on page 1 and the
/// pick failed three times without turning a page. The walk now pages, a walk is what counts
/// as an attempt, and GSX's own "Select Position at EFHK" page ends the pick.
/// </summary>
public sealed class GsxGateAirportPickPagingTests : IDisposable
{
    private const string NextPage = "Next Page ▶";

    private readonly FakeGsxApi _api = new();
    private readonly FakePhaseSource _phases = new();
    private readonly FakeDataRefs _refs = new();
    private readonly StubSimVars _simVars = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();

    private GsxGateSelectionService Service(int pages = 4)
    {
        var options = new FakeOptionsMonitor(new GsxOptions { MenuOpenTimeoutMs = 300, IntentVerifyTimeoutMs = 300, AirportPickMenuPages = pages });
        var executor = new GsxMenuIntentExecutor(
            _api,
            new GsxMenuOpener(_api, Moq.Mock.Of<ISimVars>(), NullLogger<GsxMenuOpener>.Instance),
            options,
            NullLogger<GsxMenuIntentExecutor>.Instance);
        _refs.Values[ProsimDataRefNames.FmsDestination.Name] = "EFHK";
        return new GsxGateSelectionService(_api, executor, _phases, _refs, _simVars, _eventLog,
            NullLogger<GsxGateSelectionService>.Instance, options);
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

    /// <summary>GSX as recorded on 2026-10-11: page 1 = planned airport + nearby fields; the
    /// destination on page 2; the row pick answers with GSX's own position page.</summary>
    private void GsxWithEfhkOnPageTwo(bool destinationPresent = true)
        => _api.OnCommand = (verb, args) =>
        {
            switch (verb)
            {
                case "menu.open":
                    ShowMenu("Select airport", "Moving Map", "EGCC Manchester at 69.47 nm [PLANNED]", "EGNV Teesside Intl at 6.69 nm", "EGHIL at 8.55 nm", NextPage);
                    break;
                case "menu.pick":
                    var entry = _api.Mirror.Menu!.Entries[args!["index"]!.GetValue<int>()];
                    if (entry == NextPage)
                    {
                        ShowMenu("Select airport", destinationPresent ? "EFHK Helsinki-Vantaa at 420.10 nm" : "EFHF Malmi at 425.00 nm", "EFTU Turku at 310.20 nm", "↑ Back");
                    }
                    else if (entry.StartsWith("EFHK", StringComparison.Ordinal))
                    {
                        ShowMenu("Select Position at EFHK/Vantaa", "Select from Map", "Search parking...", "Apron 1W (Gates W34-W48) \t(11 suitable parkings)");
                    }
                    break;
                case "menu.close":
                    _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
                    break;
                case "gate.select":
                    return new GsxCommandResult(true, "ok", new JsonObject
                    {
                        ["code"] = "ok",
                        ["gate"] = new JsonObject { ["uiName"] = "Gate 40", ["gate"] = "Gate 40", ["number"] = 40, ["bglName"] = "Gate W 40" },
                    }, null);
            }

            return new GsxCommandResult(true, "ok", null, null);
        };

    private async Task ArmInCruiseAsync(GsxGateSelectionService service, string gate)
    {
        _api.Readiness = GsxReadiness.ConnectedGsxNotRunning;
        service.RequestGate(gate);
        _phases.Data = new FlightDataSnapshot { IsValid = true, OnGround = false, GroundSpeedKt = 440, AnyEngineRunning = true };
        _phases.SetPhase(FlightPhase.Cruise);
        _api.Mirror.ApplyState("airport", new JsonObject { ["icao"] = "EGCC" });
        _api.Readiness = GsxReadiness.Ready;
        await service.TryDispatchAsync();
    }

    private List<int> Picks() => _api.Commands.Where(c => c.Verb == "menu.pick").Select(c => c.Args!["index"]!.GetValue<int>()).ToList();

    [Fact]
    public async Task DestinationOnPageTwo_IsFoundByTurningThePage_AndTheGateGoesOut()
    {
        GsxWithEfhkOnPageTwo();
        using var service = Service();

        await ArmInCruiseAsync(service, "W40");

        Assert.Equal([4, 0], Picks());                                    // Next Page, then the EFHK row
        Assert.Single(_api.Commands, c => c.Verb == "gate.select");
        Assert.Equal(GsxGateRequestStatus.Assigned, service.Status);
    }

    [Fact]
    public async Task DestinationOnNoPage_LeavesTheRequestArmed_ClosesOurMenu_AndDoesNotWalkAgainAtOnce()
    {
        GsxWithEfhkOnPageTwo(destinationPresent: false);
        using var service = Service();

        await ArmInCruiseAsync(service, "W40");

        Assert.Equal([4], Picks());                                       // one page turn, no row pick
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "gate.select");
        Assert.Contains(_api.Commands, c => c.Verb == "menu.close");      // we opened it, we close it
        Assert.Equal(GsxGateRequestStatus.Armed, service.Status);

        var opens = _api.Commands.Count(c => c.Verb == "menu.open");
        await service.TryDispatchAsync();                                 // the ten-minute walk backoff
        Assert.Equal(opens, _api.Commands.Count(c => c.Verb == "menu.open"));
    }

    [Fact]
    public async Task PageBoundHolds()
    {
        GsxWithEfhkOnPageTwo();
        using var service = Service(pages: 1);

        await ArmInCruiseAsync(service, "W40");

        Assert.Empty(Picks());
        Assert.Equal(GsxGateRequestStatus.Armed, service.Status);
    }

    [Fact]
    public async Task GsxAlreadyOnTheDestinationsPositionPage_PicksNothing_AndSends()
    {
        GsxWithEfhkOnPageTwo();
        ShowMenu("Select Position at EFHK/Vantaa", "Select from Map", "Search parking...");
        using var service = Service();

        await ArmInCruiseAsync(service, "W40");

        Assert.DoesNotContain(_api.Commands, c => c.Verb is "menu.open" or "menu.pick");
        Assert.Single(_api.Commands, c => c.Verb == "gate.select");
    }

    public void Dispose() => _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();

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

    private sealed class StubSimVars : ISimVars
    {
        public IDataRefSubscription SubscribeDynamic(string simVarName, string unit, DataRefTier tier) => new StubSubscription { Name = simVarName };

        public Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSubscription : IDataRefSubscription
    {
        public required string Name { get; init; }
        public object? RawValue => null;
        public bool IsStale => false;
        public DateTimeOffset? LastUpdatedUtc => null;
        public event EventHandler? ValueChanged { add { } remove { } }
        public T GetValue<T>(T fallback) => fallback;
        public void Dispose() { }
    }
}
