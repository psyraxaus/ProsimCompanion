using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #240 — EGLL–LOWW 2026-11-14, the app restarted in the climb. session-started
/// 05:08:24.569Z; the phase committed (Unknown → Climb) at 05:08:34.6Z, the first tick that
/// read the abnormal datarefs; five abnormals "detected" at 05:08:36.619Z (the 2 s debounce on
/// fallback zeros), spoken from 05:08:40Z, cleared 05:08:43–45Z as the values arrived; at the
/// end "5 abnormals handled" and five tech-log offers. A dataref that has delivered nothing is
/// unknown, not 0 — and a detection that clears before its announcement began is taken back.
/// </summary>
public sealed class FailureMonitorEarlyReadTests : IAsyncDisposable
{
    private const string Gen1Line = "aircraft.electrical.relay.Gen1Line";
    private const string Engine1Running = "aircraft.engines.1.running";
    private const string BlueQuantity = "aircraft.hydraulics.blue.quantity";

    private static readonly DateTimeOffset SessionStarted = new(2026, 11, 14, 5, 8, 24, 569, TimeSpan.Zero);
    private static readonly DateTimeOffset PhaseCommitted = new(2026, 11, 14, 5, 8, 34, 600, TimeSpan.Zero);
    private static readonly DateTimeOffset FalselyDetected = new(2026, 11, 14, 5, 8, 36, 619, TimeSpan.Zero);
    private static readonly DateTimeOffset ValuesArrived = new(2026, 11, 14, 5, 8, 43, 0, TimeSpan.Zero);

    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeDataRefs _dataRefs = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly FailureMonitor _monitor;
    private readonly List<string> _detected = [];
    private readonly List<string> _retracted = [];

    public FailureMonitorEarlyReadTests()
    {
        _monitor = new FailureMonitor(_arbiter, _dataRefs, _phase, _eventLog, NullLogger<FailureMonitor>.Instance);
        _monitor.FailureDetected += (id, _) => _detected.Add(id);
        _monitor.FailureRetracted += (id, _) => _retracted.Add(id);
    }

    public async ValueTask DisposeAsync()
    {
        _monitor.Dispose();
        await _eventLog.DisposeAsync();
    }

    /// <summary>The shipped elec-gen-1-fault trigger: generator off line with the engine running.</summary>
    private static AbnormalDefinition Gen1Fault() => new()
    {
        Id = "elec-gen-1-fault",
        Title = "ELEC GEN 1 FAULT",
        Severity = "caution",
        Announce = "ECAM, electrical, generator 1 fault.",
        Trigger = new AbnormalTrigger
        {
            EwdText = ["ELEC GEN 1 FAULT", "GEN 1 FAULT"],
            Condition = new VerifyCondition
            {
                Logic = ConditionLogic.And,
                Conditions =
                [
                    new VerifyCondition { Dataref = Gen1Line, Op = ComparisonOp.Equals, Value = 0 },
                    new VerifyCondition { Dataref = Engine1Running, Op = ComparisonOp.Equals, Value = 1 },
                ],
            },
            Logic = "any",
            DebounceSeconds = 2.0,
        },
        ClearedWhen = new VerifyCondition { Dataref = Gen1Line, Op = ComparisonOp.Equals, Value = 1 },
    };

    /// <summary>The shipped hyd-blue-rsvr-lo-lvl trigger: quantity below 3 — and no clearedWhen.</summary>
    private static AbnormalDefinition BlueReservoirLow() => new()
    {
        Id = "hyd-blue-rsvr-lo-lvl",
        Title = "HYD B RSVR LO LVL",
        Severity = "caution",
        Announce = "ECAM, hydraulic, blue reservoir low level.",
        Trigger = new AbnormalTrigger
        {
            EwdText = ["HYD B RSVR LO LVL"],
            Condition = new VerifyCondition { Dataref = BlueQuantity, Op = ComparisonOp.LessThan, Value = 3.0 },
            Logic = "any",
            DebounceSeconds = 2.0,
        },
    };

    private void TickEveryHalfSecond(DateTimeOffset from, DateTimeOffset to)
    {
        for (var at = from; at <= to; at = at.AddMilliseconds(500))
        {
            _monitor.ProcessTick(at);
        }
    }

    [Fact]
    public void RestartInTheClimb_DatarefsNotYetDelivered_DetectNothing_AndNothingWhenTheyArrive()
    {
        _monitor.Load([Gen1Fault(), BlueReservoirLow()]);
        _phase.SetPhase(FlightPhase.Unknown);
        _dataRefs.Values[Engine1Running] = 1.0;          // shared with other readers — already in
        TickEveryHalfSecond(SessionStarted.AddSeconds(1), PhaseCommitted);

        _phase.SetPhase(FlightPhase.Climb);              // 05:08:34.6Z — Gen1Line and the quantity still undelivered
        TickEveryHalfSecond(PhaseCommitted, ValuesArrived);

        Assert.True(FalselyDetected < ValuesArrived);    // the old build had fired in this window
        Assert.Empty(_detected);
        Assert.Empty(_arbiter.Requests);

        _dataRefs.Values[Gen1Line] = 1.0;                // 05:08:43Z — the values arrive: a healthy aircraft
        _dataRefs.Values[BlueQuantity] = 14.2;
        TickEveryHalfSecond(ValuesArrived, ValuesArrived.AddSeconds(30));

        Assert.Empty(_detected);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void RealFailure_OnDeliveredValues_IsDetectedAfterItsDebounce()
    {
        _monitor.Load([Gen1Fault(), BlueReservoirLow()]);
        _phase.SetPhase(FlightPhase.Climb);
        _dataRefs.Values[Engine1Running] = 1.0;
        _dataRefs.Values[Gen1Line] = 0.0;                // delivered, and off line
        _dataRefs.Values[BlueQuantity] = 14.2;

        _monitor.ProcessTick(PhaseCommitted);
        Assert.Empty(_detected);
        _monitor.ProcessTick(PhaseCommitted.AddSeconds(2));

        Assert.Equal(["elec-gen-1-fault"], _detected);
        Assert.Equal("ECAM, electrical, generator 1 fault.", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void ValuesArriveFaulty_AfterTheUnknownSpell_AreDetectedFromThatMoment()
    {
        _monitor.Load([BlueReservoirLow()]);
        _phase.SetPhase(FlightPhase.Climb);
        TickEveryHalfSecond(PhaseCommitted, ValuesArrived);
        Assert.Empty(_detected);

        _dataRefs.Values[BlueQuantity] = 1.0;            // a real low level, now that it is known
        _monitor.ProcessTick(ValuesArrived);
        Assert.Empty(_detected);                         // the debounce starts at the first KNOWN reading
        _monitor.ProcessTick(ValuesArrived.AddSeconds(2));

        Assert.Equal(["hyd-blue-rsvr-lo-lvl"], _detected);
    }

    [Fact]
    public void EwdText_StillTriggers_WhileTheConditionIsUnknown()
    {
        _monitor.Load([BlueReservoirLow()]);
        _phase.SetPhase(FlightPhase.Climb);
        _dataRefs.Values[ProsimDataRefNames.FwcContentLeft.Name] = "HYD B RSVR LO LVL";

        _monitor.ProcessTick(PhaseCommitted);
        _monitor.ProcessTick(PhaseCommitted.AddSeconds(2));

        Assert.Equal(["hyd-blue-rsvr-lo-lvl"], _detected);
    }

    [Fact]
    public void LatchedFault_WithoutAClearedRule_DoesNotClear_WhenItsConditionGoesUnknown()
    {
        _monitor.Load([BlueReservoirLow()]);
        _phase.SetPhase(FlightPhase.Climb);
        _dataRefs.Values[BlueQuantity] = 1.0;
        _monitor.ProcessTick(PhaseCommitted);
        _monitor.ProcessTick(PhaseCommitted.AddSeconds(2));
        Assert.Equal(["HYD B RSVR LO LVL"], _monitor.ActiveAbnormalTitles);

        _dataRefs.Values.Remove(BlueQuantity);           // no value any more — not "quantity is fine"
        _monitor.ProcessTick(PhaseCommitted.AddSeconds(3));

        Assert.Equal(["HYD B RSVR LO LVL"], _monitor.ActiveAbnormalTitles);
        Assert.Empty(_retracted);
    }

    [Theory]
    // condition readable: the authored logic, as before
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(null, true, false, false, true)]
    [InlineData(true, null, false, false, true)]
    [InlineData(null, null, false, false, false)]
    // condition unknown: the text decides only where the logic lets it decide alone
    [InlineData(true, null, true, false, true)]      // any + text hit = hit
    [InlineData(false, null, true, false, null)]     // any + text miss = unknown
    [InlineData(null, null, true, false, null)]      // no text half = unknown
    [InlineData(false, null, true, true, false)]     // all + text miss = miss
    [InlineData(true, null, true, true, null)]       // all + text hit = unknown
    public void CombineSignal_IsThreeValued(bool? textHit, bool? conditionHit, bool conditionUnknown, bool all, bool? expected)
        => Assert.Equal(expected, FailureMonitor.CombineSignal(textHit, conditionHit, conditionUnknown, all));

    [Fact]
    public async Task ClearedBeforeItsAnnouncementBegan_IsNotAnnounced_NotCounted_NotOffered()
    {
        _monitor.Load([Gen1Fault()]);
        _phase.SetPhase(FlightPhase.Climb);
        _dataRefs.Values[Engine1Running] = 1.0;
        _dataRefs.Values[Gen1Line] = 0.0;
        _monitor.ProcessTick(FalselyDetected.AddSeconds(-2));
        _monitor.ProcessTick(FalselyDetected);           // 05:08:36.619Z — detected, announcement queued
        var announcement = Assert.Single(_arbiter.Requests);
        Assert.Equal(["elec-gen-1-fault"], _detected);

        _dataRefs.Values[Gen1Line] = 1.0;                // 05:08:43Z — cleared, the queue has not reached it
        _monitor.ProcessTick(ValuesArrived);

        Assert.Equal(["elec-gen-1-fault"], _retracted);
        Assert.False(announcement.IsStillValid!());      // the arbiter drops it at dequeue
        Assert.Empty(_monitor.ActiveAbnormalTitles);

        var path = _eventLog.Path;
        await _eventLog.DisposeAsync();
        var facts = new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance).Extract(path);
        Assert.Empty(facts.Abnormals);                   // not "1 abnormal handled"
    }

    [Fact]
    public async Task ClearedAfterItsAnnouncementBegan_Stands()
    {
        _monitor.Load([Gen1Fault()]);
        _phase.SetPhase(FlightPhase.Climb);
        _dataRefs.Values[Engine1Running] = 1.0;
        _dataRefs.Values[Gen1Line] = 0.0;
        _monitor.ProcessTick(FalselyDetected.AddSeconds(-2));
        _monitor.ProcessTick(FalselyDetected);
        var announcement = Assert.Single(_arbiter.Requests);

        Assert.True(announcement.IsStillValid!());       // the arbiter takes it off the queue: it is spoken
        _dataRefs.Values[Gen1Line] = 1.0;
        _monitor.ProcessTick(ValuesArrived);

        Assert.Empty(_retracted);
        Assert.True(announcement.IsStillValid!());       // a restart after a pre-emption still plays

        var path = _eventLog.Path;
        await _eventLog.DisposeAsync();
        var facts = new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance).Extract(path);
        Assert.Equal(new AbnormalFact("ELEC GEN 1 FAULT", Cleared: true), Assert.Single(facts.Abnormals));
    }

    [Fact]
    public void FlightGoesAway_WithTheAnnouncementStillWaiting_RetractsIt()
    {
        _monitor.Load([Gen1Fault()]);
        _phase.SetPhase(FlightPhase.Climb);
        _dataRefs.Values[Engine1Running] = 1.0;
        _dataRefs.Values[Gen1Line] = 0.0;
        _monitor.ProcessTick(FalselyDetected.AddSeconds(-2));
        _monitor.ProcessTick(FalselyDetected);
        var announcement = Assert.Single(_arbiter.Requests);

        _phase.SetLive(false);
        _monitor.ProcessTick(FalselyDetected.AddSeconds(1));

        Assert.Equal(["elec-gen-1-fault"], _retracted);
        Assert.False(announcement.IsStillValid!());
    }

    [Fact]
    public void AnnouncementLatch_WhoeverComesFirstWins()
    {
        var spoken = new AnnouncementLatch();
        Assert.True(spoken.TryBegin());
        Assert.False(spoken.TryRetract());
        Assert.True(spoken.TryBegin());

        var takenBack = new AnnouncementLatch();
        Assert.True(takenBack.TryRetract());
        Assert.False(takenBack.TryBegin());
        Assert.False(takenBack.TryRetract());            // one retraction, one event
    }

    [Fact]
    public void EveryDatarefIsRegistered_OnTheFirstTick_BeforeAnyPhaseArms()
    {
        var recording = new RecordingDataRefs();
        using var monitor = new FailureMonitor(_arbiter, recording, _phase, _eventLog, NullLogger<FailureMonitor>.Instance);
        monitor.Load([Gen1Fault(), BlueReservoirLow()]);
        _phase.SetPhase(FlightPhase.Unknown);            // nothing armed, as in the first ten seconds of 2026-11-14
        _phase.SetLive(false);

        monitor.ProcessTick(SessionStarted.AddSeconds(1));

        Assert.Contains(Gen1Line, recording.Subscribed);
        Assert.Contains(Engine1Running, recording.Subscribed);
        Assert.Contains(BlueQuantity, recording.Subscribed);
    }

    private sealed class RecordingDataRefs : IProsimDataRefs
    {
        private readonly FakeDataRefs _inner = new();

        public List<string> Subscribed { get; } = [];

        public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier)
        {
            Subscribed.Add(name);
            return _inner.SubscribeDynamic(name, tier);
        }

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(name, value, cancellationToken);

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
            => _inner.PressMomentaryAsync(name, cancellationToken);
    }
}
