using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The SIAI radio-clear gate (2026-10-08): clear passes after the settle, busy
/// holds until quiet, and a frequency that never clears lets the request through at
/// <see cref="RadioClearGate.MaxWait"/>. The L:var values are scripted — no sim.</summary>
public sealed class RadioClearGateTests
{
    [Fact]
    public async Task NoSimVars_IsTheFixedSettle()
    {
        var gate = new RadioClearGate(null, NullLogger.Instance);

        var (waited, timedOut) = await gate.WaitForClearAsync();

        Assert.False(timedOut);
        Assert.InRange(waited, RadioClearGate.FallbackSettle - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task Clear_PassesAfterTheSettle()
    {
        var sim = new ScriptedSimVars();
        using var gate = new RadioClearGate(sim, NullLogger.Instance);

        var (waited, timedOut) = await gate.WaitForClearAsync();

        Assert.False(timedOut);
        Assert.True(waited >= RadioClearGate.Settle - TimeSpan.FromMilliseconds(50), waited.ToString());
        Assert.True(waited < TimeSpan.FromSeconds(2), waited.ToString());
    }

    [Fact]
    public async Task Busy_HoldsUntilQuiet()
    {
        var sim = new ScriptedSimVars();
        sim.Set("L:SIAI_COM1_RECEIVING", 1);
        using var gate = new RadioClearGate(sim, NullLogger.Instance);
        Assert.True(gate.IsBusy);

        var wait = gate.WaitForClearAsync();
        await Task.Delay(700);
        Assert.False(wait.IsCompleted);
        sim.Set("L:SIAI_COM1_RECEIVING", 0);

        var (waited, timedOut) = await wait;
        Assert.False(timedOut);
        Assert.True(waited >= TimeSpan.FromMilliseconds(1000), waited.ToString());
    }

    [Fact]
    public void OwnPtt_CountsAsBusy_StaleDoesNot()
    {
        var sim = new ScriptedSimVars();
        sim.Set("L:SIAI_RADIO_PTT", 1);
        using var gate = new RadioClearGate(sim, NullLogger.Instance);
        Assert.True(gate.IsBusy);

        sim.Stale = true;
        Assert.False(gate.IsBusy);
    }

    private sealed class ScriptedSimVars : ISimVars
    {
        private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
        private readonly List<Subscription> _subscriptions = [];

        public bool Stale { get; set; }

        public void Set(string name, double value) => _values[name] = value;

        public IDataRefSubscription SubscribeDynamic(string simVarName, string unit, DataRefTier tier)
        {
            var subscription = new Subscription(this, simVarName);
            _subscriptions.Add(subscription);
            return subscription;
        }

        public Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private double Read(string name) => _values.TryGetValue(name, out var v) ? v : 0;

        private sealed class Subscription(ScriptedSimVars owner, string name) : IDataRefSubscription
        {
            public string Name => name;
            public object? RawValue => owner.Read(name);
            public bool IsStale => owner.Stale;
            public DateTimeOffset? LastUpdatedUtc => DateTimeOffset.UtcNow;
            public event EventHandler? ValueChanged { add { } remove { } }
            public T GetValue<T>(T fallback) => (T)Convert.ChangeType(owner.Read(name), typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            public void Dispose() { }
        }
    }
}
