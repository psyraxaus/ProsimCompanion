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

    public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier) => new Sub(this, name);

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
    public void LatchedFault_TriggerSignalDropping_DoesNotClear_WhileClearedWhenReadsFalse()
    {
        // Issue #103 verbatim (2026-08-23): a live GEN fault "cleared" four times because
        // the corroborate light went out when the pilot deselected the ECAM page — the
        // authored clearedWhen (generator back on line) must be the only thing that clears.
        var definition = EngineFire();
        definition.ClearedWhen = new VerifyCondition
        {
            Dataref = "system.indicators.I_ENG_FIRE_1",
            Op = ComparisonOp.Equals,
            Value = 0,
        };
        _monitor.Load([definition]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(1.5));
        Assert.Single(_arbiter.Requests);

        // The corroborate light goes out (signal drops) while the fault itself persists:
        // must stay latched — no clear, no re-fire when the light returns.
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 0.0;
        _monitor.ProcessTick(T0.AddSeconds(3));
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;
        _monitor.ProcessTick(T0.AddSeconds(4));
        _monitor.ProcessTick(T0.AddSeconds(5.5));
        Assert.Single(_arbiter.Requests);

        // The fault actually resolving clears and re-arms.
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 0.0;
        _monitor.ProcessTick(T0.AddSeconds(7));
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _monitor.ProcessTick(T0.AddSeconds(8));
        _monitor.ProcessTick(T0.AddSeconds(9.5));
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
    public void FlightNotLive_HoldsEvenWithFaultIndicated_AndResetsRisingLatch()
    {
        // Issue #114: ProSim alone pushes live fault indications with no MSFS session.
        _monitor.Load([EngineFire()]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;

        _phase.SetLive(false);
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(5));
        _monitor.ProcessTick(T0.AddSeconds(10));
        Assert.Empty(_arbiter.Requests);

        // Going live re-starts the debounce from scratch — the held-over rise must not
        // count, or the FO fires the instant the session opens.
        _phase.SetLive(true);
        _monitor.ProcessTick(T0.AddSeconds(11));
        Assert.Empty(_arbiter.Requests);
        _monitor.ProcessTick(T0.AddSeconds(12.5));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void FlightGoesNotLive_ClearsFiredLatch_SoNextSessionRedetects()
    {
        _monitor.Load([EngineFire()]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(1.5));
        Assert.Single(_arbiter.Requests);

        _phase.SetLive(false);
        _monitor.ProcessTick(T0.AddSeconds(2));

        _phase.SetLive(true);
        _monitor.ProcessTick(T0.AddSeconds(3));
        _monitor.ProcessTick(T0.AddSeconds(4.5));
        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public void EmptyPhaseList_IsNotArmedInColdAndDark()
    {
        // Issue #116: an unpowered aircraft has no ECAM — a firing trigger is held, and the
        // hold is logged once; the same fault fires as soon as the aircraft is powered.
        var definition = EngineFire();
        definition.Phases = [];
        _monitor.Load([definition]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;

        _phase.SetPhase(FlightPhase.ColdAndDark);
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(2));
        _monitor.ProcessTick(T0.AddSeconds(4));
        Assert.Empty(_arbiter.Requests);

        _phase.SetPhase(FlightPhase.Preflight);
        _monitor.ProcessTick(T0.AddSeconds(6));
        _monitor.ProcessTick(T0.AddSeconds(8));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void ExplicitColdAndDarkPhase_StillArms()
    {
        var definition = EngineFire();
        definition.Phases = ["ColdAndDark"];
        _monitor.Load([definition]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;

        _phase.SetPhase(FlightPhase.ColdAndDark);
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(2));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void EmptyPhaseList_IsNotArmedInUnknownPhase()
    {
        var definition = EngineFire();
        definition.Phases = [];
        _monitor.Load([definition]);
        _dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;
        _dataRefs.Values["system.indicators.I_MIP_MASTER_WARNING_FO"] = 1.0;

        _phase.SetPhase(FlightPhase.Unknown);
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(2));
        Assert.Empty(_arbiter.Requests);

        _phase.SetPhase(FlightPhase.Preflight);
        _monitor.ProcessTick(T0.AddSeconds(3));
        _monitor.ProcessTick(T0.AddSeconds(4.5));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void ShippedAbnormals_AllLoad()
    {
        var folder = Path.Combine(FindRepoRoot(), "src", "ProsimCompanion.App", "config", "abnormals");
        var definitions = AbnormalLoader.LoadFolder(folder);

        Assert.Equal(30, definitions.Count);
        Assert.Equal(4, definitions.Count(d => d.IsDrill));
        Assert.All(definitions, d => Assert.False(string.IsNullOrWhiteSpace(d.Announce)));

        // Issue #103: ECAM page-button lights (I_ECAM_*) only illuminate on MANUAL page
        // selection — corroborating on one blinds the monitor to the real fault. No shipped
        // definition may ever reintroduce that trap.
        Assert.All(definitions, d => Assert.DoesNotContain(
            "I_ECAM", d.Trigger?.Corroborate ?? "", StringComparison.Ordinal));
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
