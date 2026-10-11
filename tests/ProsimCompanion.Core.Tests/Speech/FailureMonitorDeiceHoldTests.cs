using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Abnormals;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #123 reopened (EFHK 2026-10-10): the crew switched the packs OFF for the
/// de-icing treatment and the FO read the 3.98 s PACK FAULT lines as an abnormal. A
/// definition flagged holdDuringDeice is held while the GSX de-icing crew is Requested/Active
/// and arms again afterwards — the hold delays, it never suppresses a real fault.</summary>
public sealed class FailureMonitorDeiceHoldTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 20, 14, 0, TimeSpan.Zero);

    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeDataRefs _dataRefs = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly DeiceActivityStore _activity;
    private readonly FailureMonitor _monitor;

    public FailureMonitorDeiceHoldTests()
    {
        _activity = new DeiceActivityStore(_signals);
        _monitor = new FailureMonitor(
            _arbiter, _dataRefs, _phase,
            SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance,
            deiceActivity: _activity);
        _phase.SetPhase(FlightPhase.Departure);
        _monitor.Load([PackFault()]);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        _activity.Dispose();
    }

    private static AbnormalDefinition PackFault() => new()
    {
        Id = "pack-1-fault",
        Title = "AIR PACK 1 FAULT",
        Severity = "caution",
        Announce = "ECAM, air, pack 1 fault.",
        Trigger = new AbnormalTrigger
        {
            EwdText = ["PACK 1 FAULT"],
            Logic = "any",
            DebounceSeconds = 5.0,
            HoldDuringDeice = true,
        },
    };

    private void Ewd(string text) => _dataRefs.Values[Core.Aircraft.ProsimDataRefNames.FwcContentLeft.Name] = text;

    [Fact]
    public void WhileDeicing_ThePackFaultIsHeld_AndArmsAgainAfterCompletion()
    {
        _activity.SetStage(DeiceStage.Active);
        Ewd("PACK 1 FAULT");

        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(6)); // past the 5 s debounce — still held
        Assert.Empty(_arbiter.Requests);

        _activity.SetStage(DeiceStage.Completed);
        _monitor.ProcessTick(T0.AddSeconds(10)); // the rising edge starts fresh here
        Assert.Empty(_arbiter.Requests);
        _monitor.ProcessTick(T0.AddSeconds(16)); // held 6 s after the treatment — a real fault
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void ATransientShorterThanTheDebounce_NeverFires()
    {
        Ewd("PACK 1 FAULT");
        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(3.98)); // the EFHK blip
        Ewd("");
        _monitor.ProcessTick(T0.AddSeconds(4.5));

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void WithoutTheFlag_DeicingDoesNotHold()
    {
        var plain = PackFault();
        plain.Trigger!.HoldDuringDeice = false;
        _monitor.Load([plain]);
        _activity.SetStage(DeiceStage.Active);
        Ewd("PACK 1 FAULT");

        _monitor.ProcessTick(T0);
        _monitor.ProcessTick(T0.AddSeconds(6));

        Assert.Single(_arbiter.Requests);
    }
}
