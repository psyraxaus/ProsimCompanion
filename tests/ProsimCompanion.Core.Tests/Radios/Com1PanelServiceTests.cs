using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Radios;

/// <summary>Radios tile (owner request 2026-10-25): COM1 mirrored into the store, and the tune
/// written standby → verify → swap, never the active frequency first.</summary>
public sealed class Com1PanelServiceTests : IDisposable
{
    private readonly Dictionary<string, FakeSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly List<(string Name, object? Value)> _writes = [];
    private readonly RadioStore _store = new();
    private readonly Com1PanelService _service;

    public Com1PanelServiceTests()
    {
        var dataRefs = new Mock<IProsimDataRefs>();
        dataRefs.Setup(d => d.SubscribeDynamic(It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns((string name, DataRefTier _) =>
            {
                if (!_subscriptions.TryGetValue(name, out var sub))
                {
                    sub = new FakeSubscription(name);
                    _subscriptions[name] = sub;
                }

                return sub;
            });
        dataRefs.Setup(d => d.WriteAsync(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Returns((string name, object? value, CancellationToken _) =>
            {
                _writes.Add((name, value));
                // ProSim echoes the write back through the push model.
                _subscriptions[name].RaisePush(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            });

        _service = new Com1PanelService(dataRefs.Object, _store, SpeechTestSupport.TempEventLog(), NullLogger<Com1PanelService>.Instance);
        _service.Start();
    }

    public void Dispose() => _service.Dispose();

    private FakeSubscription Active => _subscriptions[ProsimDataRefNames.RadioCom1Active.Name];
    private FakeSubscription Standby => _subscriptions[ProsimDataRefNames.RadioCom1Standby.Name];

    [Fact]
    public void Com1Pushes_LandInTheStore_NotLiveUntilBothArrive()
    {
        Assert.False(_store.Snapshot().Com1Live);

        Active.RaisePush(121_800.0);
        Assert.False(_store.Snapshot().Com1Live);

        Standby.RaisePush(118_125.0);
        var snapshot = _store.Snapshot();
        Assert.True(snapshot.Com1Live);
        Assert.Equal(121_800, snapshot.Com1ActiveKhz);
        Assert.Equal(118_125, snapshot.Com1StandbyKhz);
    }

    [Fact]
    public async Task Tune_WritesStandbyFirst_ThenSwaps_OldActiveGoesToStandby()
    {
        Active.RaisePush(121_800.0);
        Standby.RaisePush(118_125.0);

        var outcome = await _service.TuneCom1Async(119_700, "assigned");

        Assert.Equal(RadioTuneOutcome.Tuned, outcome);
        Assert.Equal(
            [
                (ProsimDataRefNames.RadioCom1Standby.Name, (object?)119_700),
                (ProsimDataRefNames.RadioCom1Active.Name, 119_700),
                (ProsimDataRefNames.RadioCom1Standby.Name, 121_800),
            ],
            _writes);
        var snapshot = _store.Snapshot();
        Assert.Equal(119_700, snapshot.Com1ActiveKhz);
        Assert.Equal(121_800, snapshot.Com1StandbyKhz);
    }

    [Fact]
    public async Task Tune_WithoutProsim_WritesNothing()
    {
        var outcome = await _service.TuneCom1Async(119_700, "station");

        Assert.Equal(RadioTuneOutcome.NotConnected, outcome);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Tune_OutOfBand_WritesNothing()
    {
        Active.RaisePush(121_800.0);
        Standby.RaisePush(118_125.0);

        Assert.Equal(RadioTuneOutcome.OutOfBand, await _service.TuneCom1Async(99_000, "station"));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Tune_AlreadyActive_IsANoOp()
    {
        Active.RaisePush(119_700.0);
        Standby.RaisePush(118_125.0);

        Assert.Equal(RadioTuneOutcome.Tuned, await _service.TuneCom1Async(119_700, "assigned"));
        Assert.Empty(_writes);
    }

    [Fact]
    public void OnAssignedFrequency_NullUntilBothSidesKnown()
    {
        Assert.Null(_store.Snapshot().OnAssignedFrequency);

        Active.RaisePush(118_125.0);
        Standby.RaisePush(121_800.0);
        Assert.Null(_store.Snapshot().OnAssignedFrequency);

        var since = DateTimeOffset.UtcNow;
        _store.Update(s => s with { Assigned = new RadioAssignment("GROUND", 118_125, RadioAssignmentSource.CorrectFrequency, since) });
        Assert.True(_store.Snapshot().OnAssignedFrequency);

        Active.RaisePush(121_800.0);
        Assert.False(_store.Snapshot().OnAssignedFrequency);
    }

    internal sealed class FakeSubscription(string name) : IDataRefSubscription
    {
        public string Name => name;
        public object? RawValue { get; private set; }
        public bool IsStale => false;
        public DateTimeOffset? LastUpdatedUtc { get; private set; }

        public event EventHandler? ValueChanged;

        public T GetValue<T>(T fallback) => DataRefCoercion.Coerce(RawValue, fallback);

        public void RaisePush(object value)
        {
            RawValue = value;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
