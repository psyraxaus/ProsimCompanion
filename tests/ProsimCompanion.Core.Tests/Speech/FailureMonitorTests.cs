using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Settable dataref fake shared by write-capable engine tests.</summary>
internal sealed class FakeDataRefs : IProsimDataRefs
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public List<(string Name, object? Value)> Writes { get; } = [];

    public IDataRefSubscription Subscribe(string name, DataRefTier tier) => new Sub(this, name);

    public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
    {
        Writes.Add((name, value));
        Values[name] = value;
        return Task.CompletedTask;
    }

    public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
    {
        Writes.Add((name, "press"));
        return Task.CompletedTask;
    }

    private sealed class Sub(FakeDataRefs owner, string name) : IDataRefSubscription
    {
        public string Name => name;

        public object? RawValue => owner.Values.GetValueOrDefault(name);

        public bool IsStale => false;

        public DateTimeOffset? LastUpdatedUtc => DateTimeOffset.UtcNow;

        public event EventHandler? ValueChanged
        {
            add { }
            remove { }
        }

        public T GetValue<T>(T fallback)
        {
            var raw = owner.Values.GetValueOrDefault(name);
            if (raw is null)
            {
                return fallback;
            }

            try
            {
                return (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public void Dispose()
        {
        }
    }
}

public sealed class FailureMonitorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeDataRefs _dataRefs = new();
    private readonly FailureMonitor _monitor;

    public FailureMonitorTests()
    {
        _monitor = new FailureMonitor(
            _arbiter, _dataRefs, _phase,
            SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance);
        _phase.SetPhase(FlightPhase.Cruise);
    }

    public void Dispose() => _monitor.Dispose();

    private static AbnormalDefinition EngineFire() => new()
    {
        Id = "eng-1-fire",
        Title = "ENG 1 FIRE",
        Severity = "warning",
        Announce = "ECAM, engine 1 fire.",
        Trigger = new AbnormalTrigger
        {
            EwdText = ["ENG 1 FIRE"],
            Condition = new VerifyCondition
            {
                Dataref = "system.indicators.I_ENG_FIRE_1",
                Op = ComparisonOp.GreaterThan,
                Value = 0,
            },
            Logic = "any",
            Corroborate = "system.indicators.I_MIP_MASTER_WARNING_FO",
            DebounceSeconds = 1.0,
        },
    };

    [Fact]
    public void DatarefTrigger_WithCorroboratingLight_FiresOnceAfterDebounce_CriticalWithMasterPrefix()
    {
        _monitor.Load([EngineFire()]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;

        _monitor.ProcessTick(T0);                       // rising edge — debounce starts
        Assert.Empty(_arbiter.Requests);

        _monitor.ProcessTick(T0.AddSeconds(1.5));       // held past 1 s
        var fire = Assert.Single(_arbiter.Requests);
        Assert.Equal("Master warning. ECAM, engine 1 fire.", fire.Text);
        Assert.Equal(SpeechPriority.Critical, fire.Priority);

        _monitor.ProcessTick(T0.AddSeconds(2));         // latched — no re-announce
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void MissingCorroboratingLight_Suppresses()
    {
        _monitor.Load([EngineFire()]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0; // light NOT lit

        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(2));

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void ClearedTrigger_RearmsForNextFire()
    {
        _monitor.Load([EngineFire()]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(1.5));
        Assert.Single(_arbiter.Requests);

        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 0.0;
        _monitor.ProcessTick(T0.AddSeconds(3));         // clears + re-arms
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _monitor.ProcessTick(T0.AddSeconds(4));
        _monitor.ProcessTick(T0.AddSeconds(5.5));

        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public void PhaseGate_BlocksOutsideArmedPhases()
    {
        var definition = EngineFire();
        definition.Phases = ["Approach"];
        _monitor.Load([definition]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;

        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(2));
        Assert.Empty(_arbiter.Requests);

        _phase.SetPhase(FlightPhase.Approach);
        _monitor.ProcessTick(T0.AddSeconds(3));
        _monitor.ProcessTick(T0.AddSeconds(4.5));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public async Task Drill_SpeaksAnnounceItemsAndStatus_Critical()
    {
        var drill = new AbnormalDefinition
        {
            Id = "drill-stall-recovery",
            Class = "memoryDrill",
            Severity = "warning",
            Announce = "Stall!",
            VoiceTriggers = ["stall drill"],
            Actions = [new AbnormalAction { Say = "Nose down." }, new AbnormalAction { Say = "Wings level." }],
            Status = ["Recover smoothly."],
        };
        _monitor.Load([drill]);

        Assert.True(_monitor.TryRunDrillByPhrase("stall drill"));
        await Task.Delay(1800); // 3 × 350 ms gaps + margin

        Assert.Equal(["Stall!", "Nose down.", "Wings level.", "Recover smoothly."],
            _arbiter.Requests.Select(r => r.Text).ToArray());
        Assert.All(_arbiter.Requests, r => Assert.Equal(SpeechPriority.Critical, r.Priority));
    }

    [Fact]
    public void ShippedAbnormals_AllLoad()
    {
        var folder = Path.Combine(FindRepoRoot(), "src", "ProsimCompanion.App", "config", "abnormals");
        var definitions = AbnormalLoader.LoadFolder(folder);

        Assert.Equal(30, definitions.Count);
        Assert.Equal(4, definitions.Count(d => d.IsDrill));
        Assert.All(definitions, d => Assert.False(string.IsNullOrWhiteSpace(d.Announce)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
