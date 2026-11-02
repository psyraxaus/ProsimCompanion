using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Abnormals;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #228: a trigger condition that cannot be read is false (fail-closed, as a
/// malformed leaf always was) and says so ONCE per definition — not silently "never fires"
/// for the session, and not a warning on every 500 ms tick.</summary>
public sealed class FailureMonitorConditionFaultTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly ThrowingDataRefs _dataRefs = new();
    private readonly RecordingLogger<FailureMonitor> _logger = new();
    private readonly FailureMonitor _monitor;

    public FailureMonitorConditionFaultTests()
    {
        _monitor = new FailureMonitor(_arbiter, _dataRefs, _phase, SpeechTestSupport.TempEventLog(), _logger);
        _phase.SetPhase(FlightPhase.Cruise);
    }

    public void Dispose() => _monitor.Dispose();

    private static AbnormalDefinition Unreadable() => new()
    {
        Id = "hyd-g-low",
        Title = "HYD G RSVR LO LVL",
        Severity = "caution",
        Announce = "ECAM, green hydraulic low level.",
        Trigger = new AbnormalTrigger
        {
            Condition = new VerifyCondition
            {
                Dataref = "system.gauge.NOT_A_DATAREF",
                Op = ComparisonOp.GreaterThan,
                Value = 0,
            },
            Logic = "any",
            DebounceSeconds = 0.5,
        },
    };

    [Fact]
    public void UnreadableCondition_LogsOnceAcrossManyTicks_AndNeverFires()
    {
        _monitor.Load([Unreadable()]);

        for (var tick = 0; tick < 20; tick++)
        {
            _monitor.ProcessTick(T0.AddSeconds(tick * 0.5));
        }

        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("hyd-g-low", warning.Message, StringComparison.Ordinal);
        Assert.Contains("system.gauge.NOT_A_DATAREF GreaterThan 0", warning.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void Reload_ClearsTheOnceSet_SoACorrectedFileThatStillFailsWarnsAgain()
    {
        _monitor.Load([Unreadable()]);
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(1));

        _monitor.Load([Unreadable()]);
        _monitor.ProcessTick(T0.AddSeconds(2));
        _monitor.ProcessTick(T0.AddSeconds(3));

        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void NullChildInACompound_IsTheEvaluatorsOwnFault_OneWarningNamesTheTree()
    {
        // A readable dataref source: the fault here is the JSON's "conditions": [ {...}, null ].
        var dataRefs = new FakeDataRefs();
        var logger = new RecordingLogger<FailureMonitor>();
        using var monitor = new FailureMonitor(_arbiter, dataRefs, _phase, SpeechTestSupport.TempEventLog(), logger);
        var definition = Unreadable();
        definition.Trigger!.Condition = new VerifyCondition
        {
            Logic = ConditionLogic.Or,
            Conditions =
            [
                new VerifyCondition { Dataref = "system.indicators.I_ENG_FIRE_1", Op = ComparisonOp.Equals, Value = 1 },
                null!,
            ],
        };
        dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        monitor.Load([definition]);

        monitor.ProcessTick(T0);
        monitor.ProcessTick(T0.AddSeconds(1));
        monitor.ProcessTick(T0.AddSeconds(2));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("(system.indicators.I_ENG_FIRE_1 Equals 1 OR null)", warning.Message, StringComparison.Ordinal);
        Assert.Empty(_arbiter.Requests); // fail-closed: the readable half did not fire it either
    }

    /// <summary>A dataref source that refuses the abnormal's own dataref (the table will not
    /// take the name) while the monitor's housekeeping subscriptions (the E/WD text) still
    /// resolve — the fault must be charged to the definition, not to the tick.</summary>
    private sealed class ThrowingDataRefs : IProsimDataRefs
    {
        private readonly FakeDataRefs _inner = new();

        public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier)
            => name.StartsWith("system.gauge.", StringComparison.Ordinal)
                ? throw new InvalidOperationException("ProSim backend refused the dataref")
                : _inner.SubscribeDynamic(name, tier);

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(name, value, cancellationToken);

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
            => _inner.PressMomentaryAsync(name, cancellationToken);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
