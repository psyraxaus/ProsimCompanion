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
    private bool _radioFollowsWrites = true;
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
                var written = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                // A write of the value the input already holds changes nothing in ProSim (rig
                // 2026-11-16 01:09Z: 121705 written over 121705, the radio stayed on 119.000).
                var changed = _subscriptions[name].RawValue is not double held || Math.Abs(held - written) > 0.5;
                // ProSim echoes the write back through the push model.
                _subscriptions[name].RaisePush(written);
                // The radio follows a CHANGE of the active input (rig 2026-11-16: after the
                // tile tune from 0, R_COM1_ACTIVE and N_FREQ_COM1 both read 121705).
                if (_radioFollowsWrites && changed && name == ProsimDataRefNames.RadioCom1Active.Name)
                {
                    Radio.RaisePush(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                }

                return Task.CompletedTask;
            });

        _service = new Com1PanelService(dataRefs.Object, _store, SpeechTestSupport.TempEventLog(), NullLogger<Com1PanelService>.Instance);
        _service.Start();
    }

    public void Dispose() => _service.Dispose();

    private FakeSubscription Active => _subscriptions[ProsimDataRefNames.RadioCom1Active.Name];
    private FakeSubscription Standby => _subscriptions[ProsimDataRefNames.RadioCom1Standby.Name];
    private FakeSubscription Radio => _subscriptions[ProsimDataRefNames.RadioCom1Frequency.Name];

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

    // ---- the read fallback (2026-11-16, EGLL stand 313) ----

    /// <summary>The recorded case: R_COM1_ACTIVE and R_COM1_STANDBY read 0 while the radio was
    /// on 124.850 (N_FREQ_COM1 = 124850).</summary>
    [Fact]
    public void InputsAtZero_TheRadiosOwnFrequencyIsCom1Active()
    {
        Active.RaisePush(0.0);
        Standby.RaisePush(0.0);
        Radio.RaisePush(124_850.0);

        var snapshot = _store.Snapshot();
        Assert.True(snapshot.Com1Live);
        Assert.Equal(124_850, snapshot.Com1ActiveKhz);
        Assert.Equal(124_850, _service.ActiveKhz);

        _store.Update(s => s with { Assigned = new RadioAssignment("GROUND", 121_705, RadioAssignmentSource.CorrectFrequency, DateTimeOffset.UtcNow) });
        Assert.False(_store.Snapshot().OnAssignedFrequency);
    }

    [Fact]
    public void InputsNeverPushed_TheRadiosOwnFrequencyStillCounts()
    {
        Radio.RaisePush(124_850.0);

        Assert.True(_store.Snapshot().Com1Live);
        Assert.Equal(124_850, _store.Snapshot().Com1ActiveKhz);
    }

    /// <summary>The second recording (about 00:57Z): the app had written 121705, the pilot
    /// turned the hardware knob to 119.000 — R_COM1_ACTIVE kept 121705, N_FREQ_COM1 read 119000.
    /// The radio's own frequency is COM1; the app must not see a match with Ground.</summary>
    [Fact]
    public void TheRadiosOwnFrequency_WinsOverTheInput_TheInputIsOnlyTheLastSoftwareWrite()
    {
        Active.RaisePush(121_705.0);
        Standby.RaisePush(124_850.0);
        Radio.RaisePush(119_000.0);

        Assert.Equal(119_000, _store.Snapshot().Com1ActiveKhz);
        Assert.Equal(119_000, _service.ActiveKhz);
        _store.Update(s => s with { Assigned = new RadioAssignment("GROUND", 121_705, RadioAssignmentSource.CorrectFrequency, DateTimeOffset.UtcNow) });
        Assert.False(_store.Snapshot().OnAssignedFrequency);
    }

    [Fact]
    public void WithoutTheRadiosOwnFrequency_TheInputIsTheFallback()
    {
        Active.RaisePush(121_705.0);
        Standby.RaisePush(124_850.0);
        Assert.Equal(121_705, _store.Snapshot().Com1ActiveKhz);

        Radio.RaisePush(0.0);
        Assert.Equal(121_705, _store.Snapshot().Com1ActiveKhz);
    }

    /// <summary>The knob case as a tune (owner's rig 2026-11-16 01:09Z, "Unable to tune COM1"):
    /// the input already holds the wanted frequency, the radio is elsewhere. Writing the same
    /// value again changes nothing, so the input is first set to the radio's own frequency and
    /// then to the new one; the real frequency goes to standby.</summary>
    [Fact]
    public async Task Tune_WhenTheInputAlreadyHoldsTheFrequencyButTheRadioDoesNot_StillTunes()
    {
        Active.RaisePush(121_705.0);
        Standby.RaisePush(124_850.0);
        Radio.RaisePush(119_000.0);

        var outcome = await _service.TuneCom1Async(121_705, "fo-offer");

        Assert.Equal(RadioTuneOutcome.Tuned, outcome);
        Assert.Equal(
            [
                (ProsimDataRefNames.RadioCom1Standby.Name, (object?)121_705),
                (ProsimDataRefNames.RadioCom1Active.Name, 119_000),
                (ProsimDataRefNames.RadioCom1Active.Name, 121_705),
                (ProsimDataRefNames.RadioCom1Standby.Name, 119_000),
            ],
            _writes);
        Assert.Equal(121_705, _store.Snapshot().Com1ActiveKhz);
    }

    [Fact]
    public async Task Tune_IsUnverified_WhenTheRadioDoesNotFollowTheWrite()
    {
        _radioFollowsWrites = false;
        Active.RaisePush(121_705.0);
        Standby.RaisePush(124_850.0);
        Radio.RaisePush(119_000.0);

        Assert.Equal(RadioTuneOutcome.Unverified, await _service.TuneCom1Async(118_505, "station"));
    }

    [Fact]
    public async Task Tune_WithTheInputsAtZero_StillWritesOnlyTheInputPair_OldRadioFrequencyGoesToStandby()
    {
        Active.RaisePush(0.0);
        Standby.RaisePush(0.0);
        Radio.RaisePush(124_850.0);

        var outcome = await _service.TuneCom1Async(121_705, "fo-offer");

        Assert.Equal(RadioTuneOutcome.Tuned, outcome);
        Assert.Equal(
            [
                (ProsimDataRefNames.RadioCom1Standby.Name, (object?)121_705),
                (ProsimDataRefNames.RadioCom1Active.Name, 121_705),
                (ProsimDataRefNames.RadioCom1Standby.Name, 124_850),
            ],
            _writes);
        Assert.DoesNotContain(_writes, w => w.Name == ProsimDataRefNames.RadioCom1Frequency.Name);
    }

    [Theory]
    [InlineData(true, 121_705, true, 119_000, 119_000, true, true)]    // 2026-11-16 00:57Z: the radio, not the last software write
    [InlineData(true, 0, true, 124_850, 124_850, true, true)]          // 2026-11-16 at the gate: input 0, the radio has it
    [InlineData(false, 0, true, 124_850, 124_850, true, true)]         // inputs not pushed
    [InlineData(true, 121_705, false, 0, 121_705, true, false)]        // no radio value: the input is the fallback
    [InlineData(true, 121_705, true, 0, 121_705, true, false)]         // radio reads 0: the input is the fallback
    [InlineData(false, 121_705, false, 0, 0, false, false)]            // nothing live: a held-over value is not read
    [InlineData(true, 0, false, 124_850, 0, true, false)]              // a stale radio value is not read
    [InlineData(true, 0, true, 0, 0, true, false)]                     // radio off
    public void Reading_TheOneRule(bool inputsLive, int inputKhz, bool radioLive, int radioKhz, int expectedKhz, bool expectedLive, bool fromRadio)
        => Assert.Equal(new Com1Reading(expectedKhz, expectedLive, fromRadio), Com1Reading.From(inputsLive, inputKhz, radioLive, radioKhz));

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
