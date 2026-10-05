using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Issue #157 (EFHK→LKPR 2026-10-04): gate.select 29 answered "Gate C 29" in the air, GSX then
/// showed "Change parking or service" with "Change Facility [Gate C 29]" while the mirror named
/// no parking, and the catalogue published a parking conflict — the FO warned of an unknown
/// parking at the gate GSX had prepared. The facility that names the assigned gate is not a
/// conflict; any other facility still is.
/// </summary>
public sealed class ParkingChangeMenuAssignedGateTests : IDisposable
{
    private readonly FakeGsxApi _api = new();
    private readonly FakePhaseSource _phases = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly GsxQuestionDispatcher _dispatcher = new(NullLogger<GsxQuestionDispatcher>.Instance);
    private readonly StubAssignedGate _assigned = new();

    public ParkingChangeMenuAssignedGateTests()
    {
        var options = new FakeOptionsMonitor(new GsxOptions());
        var executor = new GsxMenuIntentExecutor(
            _api,
            new GsxMenuOpener(_api, Moq.Mock.Of<ProsimCompanion.Core.Aircraft.ISimVars>(), NullLogger<GsxMenuOpener>.Instance),
            options,
            NullLogger<GsxMenuIntentExecutor>.Instance);
        var catalog = new GsxQuestionCatalog(
            _api, executor, options, _diagnostics, _phases, _eventLog,
            NullLogger<GsxQuestionCatalog>.Instance, pushbackChoice: null, assignedGate: _assigned);
        catalog.RegisterAll(_dispatcher);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private Task ShowParkingChangeMenuAsync(string facility)
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = "Change parking or service",
            ["entries"] = new JsonArray($"Change Facility [{facility}]", "Request Deboarding"),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
        return _dispatcher.OnMenuUpdatedAsync(true, "Change parking or service");
    }

    [Fact]
    public async Task FacilityIsTheAssignedGate_NoConflict()
    {
        _assigned.AssignedGate = new GsxAssignedGate("C29", "Gate C 29", 29);

        await ShowParkingChangeMenuAsync("Gate C 29");

        Assert.Null(_diagnostics.Snapshot().ParkingConflict);
        Assert.Contains(
            _diagnostics.Snapshot().RecentDecisions,
            d => d.Action == "parking-change menu" && d.Reason.Contains("not a parking conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FacilityIsAnotherStand_StillAConflict()
    {
        _assigned.AssignedGate = new GsxAssignedGate("C29", "Gate C 29", 29);

        await ShowParkingChangeMenuAsync("Gate B 29");

        Assert.Equal("Gate B 29", _diagnostics.Snapshot().ParkingConflict?.GsxFacility);
    }

    [Fact]
    public async Task NoAssignmentStands_StillAConflict()
    {
        await ShowParkingChangeMenuAsync("Gate C 29");

        Assert.Equal("Gate C 29", _diagnostics.Snapshot().ParkingConflict?.GsxFacility);
    }

    [Theory]
    // GSX's own name for the assigned gate (gate.select payload) against the facility text.
    [InlineData("Gate C 29", "C29", "Gate C 29", true)]
    [InlineData("Gate C 29", "29", "Pier C | Gate C 29", true)]
    [InlineData("Terminal 5B (531-548) Stand 547 with Safedock©", "547", "Stand 547", true)]
    [InlineData("Terminal 5B (531-548) Stand 547 with Safedock©", "547", "Terminal 5B (531-548) | Stand 547", true)]
    [InlineData("Gate B 29", "C29", "Gate C 29", false)]
    [InlineData("Gate C 2", "C29", "Gate C 29", false)]
    [InlineData("Gate C 291", "C29", "Gate C 29", false)]
    [InlineData("Terminal 2", "C29", "Terminal 2 | Gate C 29", false)]
    // No GSX name (position-menu pick): the typed token decides, on whole words.
    [InlineData("Gate C 29", "C29", null, true)]
    [InlineData("Gate C 29", "Gate C29", null, true)]
    [InlineData("Apron 1W (Gates W34-W48) Gate W40", "W40", null, true)]
    [InlineData("Terminal 5B (531-548) Stand 547 with Safedock©", "547", null, true)]
    [InlineData("Terminal 5B (531-548) Stand 547 with Safedock©", "548", null, false)]
    [InlineData("Gate C 29", "B29", null, false)]
    [InlineData("Gate C 29", "2", null, false)]
    [InlineData("Terminal 29 Gate 5", "29", null, false)]
    [InlineData("", "C29", "Gate C 29", false)]
    public void FacilityNamesGate_ComparesWholeWords(string facility, string requested, string? gsxName, bool expected)
        => Assert.Equal(expected, GsxGateResolver.FacilityNamesGate(facility, requested, gsxName));

    [Fact]
    public void AssignedFrom_ReadsTheGateRef_NestedOrAtTheRoot()
    {
        var nested = GsxGateSelectionService.AssignedFrom("C29", new JsonObject
        {
            ["code"] = "prepared",
            ["gate"] = new JsonObject { ["uiName"] = "Gate C 29", ["number"] = 29 },
        });
        var root = GsxGateSelectionService.AssignedFrom("C29", new JsonObject { ["uiName"] = "Gate C 29", ["number"] = 29 });
        var none = GsxGateSelectionService.AssignedFrom("C29", null);

        Assert.Equal(new GsxAssignedGate("C29", "Gate C 29", 29), nested);
        Assert.Equal(new GsxAssignedGate("C29", "Gate C 29", 29), root);
        Assert.Equal(new GsxAssignedGate("C29", null, null), none);
    }

    private sealed class StubAssignedGate : IGsxAssignedGateSource
    {
        public GsxAssignedGate? AssignedGate { get; set; }
    }

    private sealed class FakeGsxApi : IGsxRemoteApi
    {
#pragma warning disable CS0067
        public event Action<GsxReadiness>? ReadinessChanged;
#pragma warning restore CS0067

        public GsxReadiness Readiness { get; set; } = GsxReadiness.Ready;
        public GsxStateMirror Mirror { get; } = new();

        public bool HasCapability(string token) => true;

        public Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default)
            => Task.FromResult(new GsxCommandResult(true, "ok", null, null));
    }

    private sealed class FakeOptionsMonitor(GsxOptions value) : IOptionsMonitor<GsxOptions>
    {
        public GsxOptions CurrentValue { get; } = value;
        public GsxOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GsxOptions, string?> listener) => null;
    }
}
