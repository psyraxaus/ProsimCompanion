using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Issue #225: OperateJetways / OperateStairs are toggles, and four senders can ask for the
/// retraction within the same seconds — the beacon sequence (RequestRemovalAsync), the on-final
/// hook (same), the stairs-after-departure step and the crew (RetractAsync). The jetway service
/// holds one per-cycle "retraction sent" latch per service in front of the slot, so each toggle
/// goes out once per cycle whatever the senders do.
/// </summary>
public sealed class GsxJetwayStairsRetractionTests : IDisposable
{
    private const string Jetway = GsxServiceIds.OperateJetways;
    private const string Stairs = GsxServiceIds.OperateStairs;

    private readonly Mock<IGsxRemoteApi> _api = new();
    private readonly GsxStateMirror _mirror = new();
    private readonly Mock<IGsxTriggerSlot> _slot = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly List<GsxTriggerRequest> _requests = [];
    private GsxTriggerDispatch _dispatch = new(GsxTriggerDispatchStatus.Dispatched);
    private GsxJetwayStairsService? _service;

    public void Dispose() => _service?.Dispose();

    private GsxJetwayStairsService CreateService()
    {
        _api.SetupGet(a => a.Mirror).Returns(_mirror);
        _slot
            .Setup(s => s.TryDispatchAsync(It.IsAny<GsxTriggerRequest>(), It.IsAny<CancellationToken>()))
            .Callback((GsxTriggerRequest request, CancellationToken _) =>
            {
                lock (_requests)
                {
                    _requests.Add(request);
                }
            })
            .ReturnsAsync(() => _dispatch);

        var lvar = new Mock<IDataRefSubscription>();
        lvar.Setup(s => s.GetValue(It.IsAny<double>())).Returns(0.0);
        var simVars = new Mock<ISimVars>();
        simVars
            .Setup(s => s.SubscribeDynamic(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns(lvar.Object);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new GsxOptions());

        _service = new GsxJetwayStairsService(
            _api.Object,
            _slot.Object,
            simVars.Object,
            _signals,
            options.Object,
            _diagnostics,
            NullLogger<GsxJetwayStairsService>.Instance);
        return _service;
    }

    /// <summary>Seeds the mirror's service list (semantic wire states: "completed" = docked,
    /// "available" = retracted / callable).</summary>
    private void SeedServices(params (string Id, string State)[] services)
        => _mirror.ApplyState("services", new JsonArray(
            [.. services.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["state"] = s.State,
                ["canTrigger"] = true,
            })]));

    private string[] Sent
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests.Select(r => r.ServiceId)];
            }
        }
    }

    [Fact]
    public async Task RequestRemoval_Twice_SendsEachToggleOnce()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"));

        await service.RequestRemovalAsync(); // the beacon sequence step
        await service.RequestRemovalAsync(); // the on-final hook, a tick later

        Assert.Equal([Jetway], Sent);
        var request = Assert.Single(_requests);
        Assert.True(request.NoConfirm);
        Assert.Equal("jetway/stairs removal", request.Source); // the #225 probe matches this literally
    }

    /// <summary>The audit's example: DepartureComplete fires the stairs-after-departure step at
    /// the same moment the pilot says "retract stairs".</summary>
    [Fact]
    public async Task CrewRetraction_BehindTheSequenceRetraction_IsRefusedNamingTheService()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"), (Stairs, "completed"));

        await service.RemoveStairsAfterDepartureAsync("onlyJetway");
        var crew = await service.RetractAsync(Stairs, "voice");

        Assert.Equal(GsxTriggerDispatchStatus.Busy, crew.Status);
        Assert.Equal(Stairs, crew.BusyServiceId);
        Assert.Equal([Stairs], Sent);
    }

    [Fact]
    public async Task StairsAfterDeparture_ThenSequenceRemoval_SendsStairsOnce_AndTheJetway()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"), (Stairs, "completed"));

        await service.RemoveStairsAfterDepartureAsync("always");
        await service.RequestRemovalAsync();

        Assert.Equal([Stairs, Jetway], Sent);
    }

    [Fact]
    public async Task FlightCycleReset_ReArmsTheLatch()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"));

        await service.RequestRemovalAsync();
        _signals.RaiseFlightCycleReset();
        await service.RequestRemovalAsync();

        Assert.Equal([Jetway, Jetway], Sent);
    }

    /// <summary>The evidence-based release: once the mirror shows the jetway gone, a later
    /// retraction (after a re-connect) is a new one.</summary>
    [Fact]
    public async Task MirrorShowingTheServiceRetracted_ReleasesTheLatch()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"));
        await service.RequestRemovalAsync();

        SeedServices((Jetway, "available")); // GSX retracted it
        SeedServices((Jetway, "completed")); // re-connected for the next departure
        await service.RequestRemovalAsync();

        Assert.Equal([Jetway, Jetway], Sent);
    }

    /// <summary>While the mirror still reads the jetway docked, a second mirror patch does not
    /// release the latch — the retraction is still playing out.</summary>
    [Fact]
    public async Task MirrorStillShowingConnected_KeepsTheLatch()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"));
        await service.RequestRemovalAsync();

        SeedServices((Jetway, "completed"), (Stairs, "available")); // an unrelated services patch
        await service.RequestRemovalAsync();

        Assert.Equal([Jetway], Sent);
    }

    [Fact]
    public async Task SlotRefusal_DoesNotLatch_SoTheNextAttemptSends()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"));
        _dispatch = new(GsxTriggerDispatchStatus.Busy, "Refueling");

        await service.RequestRemovalAsync();
        _dispatch = new(GsxTriggerDispatchStatus.Dispatched);
        await service.RequestRemovalAsync();

        Assert.Equal([Jetway, Jetway], Sent);
    }

    [Fact]
    public async Task CrewRetraction_First_IsSent_AndBlocksTheSequence()
    {
        var service = CreateService();
        SeedServices((Jetway, "completed"));

        var crew = await service.RetractAsync(Jetway, "voice");
        await service.RequestRemovalAsync();

        Assert.Equal(GsxTriggerDispatchStatus.Dispatched, crew.Status);
        Assert.Equal([Jetway], Sent);
    }
}
