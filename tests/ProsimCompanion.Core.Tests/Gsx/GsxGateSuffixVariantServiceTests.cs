using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Airports.Parking;
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

/// <summary>
/// Issue #156 at the service level (EFHK W36, 2026-10-11): GSX answers the number with the
/// scenery's suffix variants. The pilot's suffix wins; a lone jetway decides; otherwise the
/// pilot is asked (web / voice) and the first variant goes at taxi-in; GSX anchoring on a
/// variant itself is adopted; and (issue #75) <c>no_airport</c> keeps the request armed.
/// </summary>
public sealed class GsxGateSuffixVariantServiceTests : IDisposable
{
    private readonly FakeGsxApi _api = new();
    private readonly FakePhaseSource _phases = new();
    private readonly FakeDataRefs _refs = new();
    private readonly StubSimVars _simVars = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly GateChoiceStore _choices = new(new GroundOpsSignals());
    private readonly FakeCatalogue _catalogue = new();
    private readonly GsxOptions _options = new() { MenuOpenTimeoutMs = 300, IntentVerifyTimeoutMs = 300 };

    private GsxGateSelectionService Service(bool withChoices = true)
    {
        var options = new FakeOptionsMonitor(_options);
        var executor = new GsxMenuIntentExecutor(
            _api,
            new GsxMenuOpener(_api, Moq.Mock.Of<ISimVars>(), NullLogger<GsxMenuOpener>.Instance),
            options,
            NullLogger<GsxMenuIntentExecutor>.Instance);
        _refs.Values[ProsimDataRefNames.FmsDestination.Name] = "EFHK";
        return new GsxGateSelectionService(_api, executor, _phases, _refs, _simVars, _eventLog,
            NullLogger<GsxGateSelectionService>.Instance, options, _catalogue, withChoices ? _choices : null);
    }

    private static JsonObject AmbiguousThirtySix() => new()
    {
        ["code"] = "ambiguous",
        ["message"] = "multiple parkings match '36'",
        ["candidates"] = new JsonArray(
            new JsonObject { ["uiName"] = "Apron 1W (Gates W34-W48) | Gate 36A", ["gate"] = " Gate 36A", ["number"] = 36, ["bglName"] = "Gate W 36A" },
            new JsonObject { ["uiName"] = "Apron 1W (Gates W34-W48) | Gate 36B", ["gate"] = " Gate 36B", ["number"] = 36, ["bglName"] = "Gate W 36B" }),
    };

    private static JsonObject Accepted(string name) => new()
    {
        ["code"] = "ok",
        ["status"] = "prepared",
        ["gate"] = new JsonObject { ["uiName"] = name, ["gate"] = name, ["number"] = 36, ["bglName"] = name },
    };

    /// <summary>GSX as recorded: the integer answers ambiguous, the accepted bglName ok, every
    /// other string not_found.</summary>
    private void GsxSplitsThirtySix(string accepted)
        => _api.OnCommand = (verb, args) =>
        {
            if (verb != "gate.select")
            {
                return new GsxCommandResult(true, "ok", null, null);
            }

            var gate = args?["gate"];
            if (gate is JsonValue value && value.TryGetValue<int>(out var number) && number == 36)
            {
                return new GsxCommandResult(false, "ambiguous", null, AmbiguousThirtySix());
            }

            return string.Equals(gate?.ToString(), accepted, StringComparison.Ordinal)
                ? new GsxCommandResult(true, "ok", Accepted(accepted), null)
                : new GsxCommandResult(false, "not_found", null, new JsonObject { ["code"] = "not_found" });
        };

    /// <summary>Arms the request while GSX is not Ready (the planner waits), then puts the
    /// aircraft in the descent with EFHK loaded — the dispatch is driven explicitly.</summary>
    private async Task ArmAndDispatchAsync(GsxGateSelectionService service, string gate)
    {
        _api.Readiness = GsxReadiness.ConnectedGsxNotRunning;
        service.RequestGate(gate);
        _phases.Data = new FlightDataSnapshot { IsValid = true, OnGround = false, GroundSpeedKt = 300, AnyEngineRunning = true };
        _phases.SetPhase(FlightPhase.Descent);
        _api.Mirror.ApplyState("airport", new JsonObject { ["icao"] = "EFHK" });
        _api.Readiness = GsxReadiness.Ready;
        await service.TryDispatchAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private IEnumerable<string?> GateSelectTokens()
        => _api.Commands.Where(c => c.Verb == "gate.select").Select(c => c.Args?["gate"]?.ToString());

    [Fact]
    public async Task NothingDecides_ThePilotIsAsked_AndTheWebAnswerIsSent()
    {
        GsxSplitsThirtySix(accepted: "Gate W 36B");
        using var service = Service();

        await ArmAndDispatchAsync(service, "W36");

        Assert.Equal(GsxGateRequestStatus.Armed, service.Status);
        Assert.Contains("36A or 36B", service.StatusDetail, StringComparison.Ordinal);
        var question = _choices.Snapshot().Question;
        Assert.NotNull(question);
        Assert.Equal("W36 is 36A or 36B in this scenery — which?", question.Prompt);
        Assert.Equal(["36A", "36B"], question.Candidates.Select(c => c.Label));
        Assert.DoesNotContain("Gate W 36A", GateSelectTokens()); // never a guess while the question stands

        Assert.True(_choices.Choose("36B", "web"));
        await WaitUntilAsync(() => service.Status == GsxGateRequestStatus.Assigned);

        Assert.Equal("Gate W 36B", GateSelectTokens().Last());
        Assert.Equal("Gate W 36B", service.AssignedGate?.GsxName);
    }

    [Fact]
    public async Task NoAnswerByTaxiIn_TheFirstVariantGoes()
    {
        GsxSplitsThirtySix(accepted: "Gate W 36A");
        using var service = Service();
        await ArmAndDispatchAsync(service, "W36");
        Assert.NotNull(_choices.Snapshot().Question);

        _phases.Data = new FlightDataSnapshot { IsValid = true, OnGround = true, GroundSpeedKt = 15, AnyEngineRunning = true };
        _phases.SetPhase(FlightPhase.TaxiIn);
        await WaitUntilAsync(() => service.Status == GsxGateRequestStatus.Assigned);

        Assert.Equal("Gate W 36A", GateSelectTokens().Last());
        Assert.Equal("default at taxi-in", _choices.Snapshot().Choice?.Source);
        Assert.Null(_choices.Snapshot().Question);
    }

    [Fact]
    public async Task ThePilotsSuffix_DecidesWithoutAQuestion()
    {
        GsxSplitsThirtySix(accepted: "Gate W 36B");
        using var service = Service();

        await ArmAndDispatchAsync(service, "W36B");

        Assert.Equal(GsxGateRequestStatus.Assigned, service.Status);
        Assert.Equal("Gate W 36B", GateSelectTokens().Last());
        Assert.Null(_choices.Snapshot().Question);
    }

    [Fact]
    public async Task TheOnlyVariantWithAJetway_DecidesWithoutAQuestion()
    {
        _catalogue.JetwayOn36B = true;
        GsxSplitsThirtySix(accepted: "Gate W 36B");
        using var service = Service();

        await ArmAndDispatchAsync(service, "W36");

        Assert.Equal(GsxGateRequestStatus.Assigned, service.Status);
        Assert.Equal("Gate W 36B", GateSelectTokens().Last());
        Assert.Null(_choices.Snapshot().Question);
    }

    [Fact]
    public async Task AskingOff_TakesTheFirstVariantAtOnce()
    {
        _options.AskPilotOnAmbiguousGate = false;
        GsxSplitsThirtySix(accepted: "Gate W 36A");
        using var service = Service();

        await ArmAndDispatchAsync(service, "W36");

        Assert.Equal(GsxGateRequestStatus.Assigned, service.Status);
        Assert.Equal("Gate W 36A", GateSelectTokens().Last());
        Assert.Null(_choices.Snapshot().Question);
    }

    [Fact]
    public void GsxAnchoredOnAVariant_IsAdopted()
    {
        using var service = Service();
        _api.Readiness = GsxReadiness.ConnectedGsxNotRunning;
        service.RequestGate("W36");

        Assert.False(service.TryAdoptFacility("Gate 40"));
        Assert.True(service.TryAdoptFacility("Apron 1W (Gates W34-W48)   Gate 36B with Safedock©"));

        Assert.Equal(GsxGateRequestStatus.Assigned, service.Status);
        Assert.Equal("Gate 36B", service.AssignedGate?.GsxName);
        Assert.Equal(36, service.AssignedGate?.Number);
        Assert.Contains("GSX picked Gate 36B for W36", service.StatusDetail, StringComparison.Ordinal);
        Assert.Empty(_api.Commands); // adoption sends nothing
    }

    [Fact]
    public void NoRequest_NothingIsAdopted()
    {
        using var service = Service();
        Assert.False(service.TryAdoptFacility("Gate 36B"));
    }

    [Fact]
    public async Task NoAirport_KeepsTheRequestArmed_AndDoesNotResendAtOnce()
    {
        _api.OnCommand = (verb, _) => verb == "gate.select"
            ? new GsxCommandResult(false, "no_airport", null, new JsonObject { ["code"] = "no_airport", ["message"] = "no airport loaded" })
            : new GsxCommandResult(true, "ok", null, null);
        using var service = Service();

        await ArmAndDispatchAsync(service, "W36");

        Assert.Equal(GsxGateRequestStatus.Armed, service.Status);
        Assert.Contains("airport pending", service.StatusDetail, StringComparison.Ordinal);
        var sends = _api.Commands.Count(c => c.Verb == "gate.select");
        Assert.Equal(1, sends);

        await service.TryDispatchAsync();
        Assert.Equal(sends, _api.Commands.Count(c => c.Verb == "gate.select"));
    }

    public void Dispose()
    {
        _choices.Dispose();
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>EFHK as the two tiers see it: the profile names Gate 36, the scenery has 36A/36B.</summary>
    private sealed class FakeCatalogue : IAirportParkingCatalog
    {
        public bool JetwayOn36B { get; set; }

        public Task<AirportParkings?> GetAsync(string icao, bool refresh = false, CancellationToken cancellationToken = default)
        {
            static AirportParking Stand(ParkingName name, int number, string suffix, ParkingDataSources sources, string? gateName = null, string? uiName = null)
                => AirportParking.Bare(new ParkingIdentity(name, number, suffix), sources) with { GsxGateName = gateName, GsxUiName = uiName };

            var parkings = new List<AirportParking>
            {
                Stand(ParkingName.GateW, 36, "", ParkingDataSources.GsxPy, "Gate 36", "Apron 1W (Gates W34-W48) | Gate 36"),
                Stand(ParkingName.GateW, 36, "A", ParkingDataSources.Facility),
                Stand(ParkingName.GateW, 36, "B", ParkingDataSources.Facility) with { HasJetway = JetwayOn36B ? true : null },
            };
            return Task.FromResult<AirportParkings?>(new AirportParkings(
                "EFHK", parkings, ParkingDataSources.GsxPy | ParkingDataSources.Facility, [], DateTimeOffset.UnixEpoch));
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
