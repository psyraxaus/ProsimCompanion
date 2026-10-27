using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Diagnostics.Ecam;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The catering door is learned from GSX's service-door toggles (owner flight 2026-10-27: the
/// truck came to 4R while the diagnostics page, reading a "forward" setting, drew it at 1R).
/// SERVICE_2 → aft, SERVICE_1 → forward, written to settings.json once per real change, and
/// learned even while door automation is off — the toggle is information, not an action.
/// </summary>
public sealed class GsxDoorServiceCateringDoorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-door-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly GsxOptions _options = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly TableSimVars _simVars = new();
    private readonly List<(string Name, object? Value)> _writes = [];

    public GsxDoorServiceCateringDoorTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Service2Toggle_LearnsAft_WritesTheFile_AndRecordsADecision()
    {
        _options.DoorAutomationEnabled = false;   // learning does not need the automation
        using var service = Build();

        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);

        Assert.Equal("aft", LearnedInFile());
        var decision = Assert.Single(_diagnostics.Snapshot().RecentDecisions, d => d.Action == "doors");
        Assert.Equal("catering door learned: 4R (GSX service-2 toggle)", decision.Reason);
        Assert.Empty(_writes);   // automation off: no door was written
    }

    [Fact]
    public void Service1Toggle_LearnsForward()
    {
        using var service = Build();

        _simVars.Push(GsxLvarNames.DoorToggleService1.Name, 1.0);

        Assert.Equal("forward", LearnedInFile());
    }

    [Fact]
    public void SameDoorAgain_WritesNothingMore_AndAStayingHighToggleIsOneEdge()
    {
        using var service = Build();

        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);
        _options.CateringDoorLearned = "aft";                    // the options monitor would reload this
        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);   // still high — not an edge
        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 0.0);
        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);   // a second real pulse, same door

        Assert.Single(_diagnostics.Snapshot().RecentDecisions, d => d.Reason.StartsWith("catering door learned", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitPick_StillRecordsTheLearnedDoor_ButTheDiagnosticsPageFollowsThePick()
    {
        _options.CateringDoor = GsxOptions.CateringDoorForward;
        using var service = Build();

        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);

        Assert.Equal("aft", LearnedInFile());
        _options.CateringDoorLearned = "aft";
        Assert.Equal(GsxOptions.CateringDoorForward, GsxOptions.EffectiveCateringDoor(_options));
    }

    [Fact]
    public void WithAutomationOn_TheServiceToggleStillOpensTheDoor_Once()
    {
        using var service = Build();

        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);
        _simVars.Push(GsxLvarNames.DoorToggleService2.Name, 1.0);

        var write = Assert.Single(_writes);
        Assert.Equal(ProsimDataRefNames.Door4R.Name, write.Name);
        Assert.Equal(true, write.Value);
    }

    private GsxDoorService Build()
    {
        var gateway = new Mock<IProsimGateway>();
        gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) => _writes.Add((name, value)))
            .ReturnsAsync(true);
        var writer = new GsxProsimWriter(gateway.Object, _diagnostics, NullLogger<GsxProsimWriter>.Instance);
        var settings = new SettingsWriter(new JsonSettingsFile(_path));
        return new GsxDoorService(
            new TableDataRefs(),
            _simVars,
            writer,
            new GsxServiceLifecycleTracker(NullLogger<GsxServiceLifecycleTracker>.Instance),
            new FixedOptionsMonitor<GsxOptions>(_options),
            _diagnostics,
            settings,
            NullLogger<GsxDoorService>.Instance);
    }

    private string? LearnedInFile()
    {
        var root = new JsonSettingsFile(_path).Read();
        return root[GsxOptions.SectionName]?["cateringDoorLearned"]?.GetValue<string>();
    }

    /// <summary>An in-memory LVAR feed over the real subscription table, so a pushed value
    /// raises <c>ValueChanged</c> exactly as the SimConnect layer does.</summary>
    private sealed class TableSimVars : ISimVars
    {
        private readonly DataRefSubscriptionTable _table = new();

        public IDataRefSubscription SubscribeDynamic(string simVarName, string unit, DataRefTier tier) => _table.Subscribe(simVarName, tier);

        public Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Push(string name, double value) => _table.UpdateValue(name, value, DateTimeOffset.UtcNow);
    }
}
