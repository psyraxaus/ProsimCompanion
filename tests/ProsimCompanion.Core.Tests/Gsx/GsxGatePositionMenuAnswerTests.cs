using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Issue #156: GSX raises "Select Position at EFHK/Vantaa" itself; with the switch on
/// the gate service answers it for the armed gate through the executor — facility group, then
/// the position row — and the request becomes Assigned. Off, or no matching row: untouched.</summary>
public sealed class GsxGatePositionMenuAnswerTests : IDisposable
{
    private readonly FakeGsxApi _api = new();
    private readonly FakePhaseSource _phases = new();
    private readonly FakeDataRefs _refs = new();
    private readonly StubSimVars _simVars = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();

    private GsxGateSelectionService Service(bool answer)
    {
        var options = new FakeOptionsMonitor(new GsxOptions
        {
            MenuOpenTimeoutMs = 300,
            IntentVerifyTimeoutMs = 300,
            AnswerPositionMenuWithArrivalGate = answer,
        });
        var executor = new GsxMenuIntentExecutor(
            _api,
            new GsxMenuOpener(_api, Moq.Mock.Of<ProsimCompanion.Core.Aircraft.ISimVars>(), NullLogger<GsxMenuOpener>.Instance),
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

    [Fact]
    public async Task PositionMenu_IsAnsweredWithTheArmedGate_GroupThenPosition()
    {
        // The aircraft is still rolling: the planner's "send now / too late" paths stay out of the way.
        _phases.SetPhase(FlightPhase.LandingRollout);
        _phases.Data = new FlightDataSnapshot { IsValid = true, OnGround = true, GroundSpeedKt = 60 };
        using var service = Service(answer: true);
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.pick" && args?["index"]?.GetValue<int>() == 0 && _api.Mirror.Menu?.Title.StartsWith("Select Position", StringComparison.Ordinal) == true)
            {
                ShowMenu("All Apron 1W (Gates W34-W48)  positions", "Gate W34 [Medium]", "Gate W36 [Medium]", "Gate W40 [Heavy]", "Gate W42 [Medium]", "Previous");
            }
            else if (verb == "menu.pick")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }

            return new GsxCommandResult(true, "ok", null, null);
        };

        service.RequestGate("W40");
        ShowMenu("Select Position at EFHK/Vantaa", "Apron 1W (Gates W34-W48)", "Apron 1 (Gates 11-18)", "Apron 4 (Cargo, 401-411)", "Remote Stands 8XX/9XX");
        await service.TryAnswerPositionMenuAsync();

        var picks = _api.Commands.Where(c => c.Verb == "menu.pick").Select(c => c.Args?["index"]?.GetValue<int>()).ToList();
        Assert.Equal([0, 2], picks);                                   // the W group, then "Gate W40 [Heavy]"
        Assert.Equal(GsxGateRequestStatus.Assigned, service.Status);
        Assert.Contains("position menu", service.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchOff_LeavesTheMenuAlone()
    {
        _phases.SetPhase(FlightPhase.LandingRollout);
        using var service = Service(answer: false);

        service.RequestGate("W40");
        ShowMenu("Select Position at EFHK/Vantaa", "Apron 1W (Gates W34-W48)");
        await service.TryAnswerPositionMenuAsync();

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
        Assert.NotEqual(GsxGateRequestStatus.Assigned, service.Status);
    }

    [Fact]
    public async Task NoRowCoversTheGate_LeavesTheMenu_AndRecordsWhy()
    {
        _phases.SetPhase(FlightPhase.LandingRollout);
        using var service = Service(answer: true);

        service.RequestGate("W70");
        ShowMenu("Select Position at EFHK/Vantaa", "Apron 1W (Gates W34-W48)", "Apron 4 (Cargo, 401-411)");
        await service.TryAnswerPositionMenuAsync();

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
        Assert.NotEqual(GsxGateRequestStatus.Assigned, service.Status);
    }

    [Fact]
    public async Task OtherMenus_AreIgnored()
    {
        _phases.SetPhase(FlightPhase.LandingRollout);
        using var service = Service(answer: true);

        service.RequestGate("W40");
        ShowMenu("Request FollowMe?", "Yes", "No");
        await service.TryAnswerPositionMenuAsync();

        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
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
            Commands.Add((verb, args));
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
