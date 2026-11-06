using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The transition reminder's SHELL (review 2026-11-05 P7: the 272-line service — the
/// DFD memo, the QNH source, the "no DFD → silent" rule — had only a core test): the tick
/// through the real service with fakes for the aircraft, the flight and the arbiter.</summary>
public sealed class TransitionReminderServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly FakePhaseSource _phases = new();
    private readonly FakeFlightSource _source = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakeDataRefs _refs = new();
    private readonly SopOptions _sop = new() { Transition = new TransitionReminderOptions { Enabled = true } };
    private readonly TransitionReminderService _service;

    public TransitionReminderServiceTests()
    {
        _service = new TransitionReminderService(
            SpeechTestSupport.SopMonitor(_sop),
            SpeechTestSupport.SpeechMonitor(new SpeechOptions()),
            _refs,
            _source,
            _phases,
            _arbiter,
            SpeechTestSupport.TempEventLog(),
            new GroundOpsSignals(),
            NullLogger<TransitionReminderService>.Instance)
        {
            Clock = () => T0,
        };
    }

    public void Dispose() => _service.Dispose();

    private void Climbing(bool live = true, double altitudeFt = 7_500)
    {
        _phases.SetPhase(FlightPhase.Climb);
        _phases.SetLive(live);
        _source.Snapshot = new FlightDataSnapshot { IsValid = true, AltitudeFt = altitudeFt, IndicatedAltitudeFt = altitudeFt };
    }

    [Fact]
    public void Disabled_SaysNothing_WhateverTheAircraftDoes()
    {
        _sop.Transition.Enabled = false;
        Climbing();

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(_service.ProcessTick(T0.AddSeconds(30 * i)));
        }

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void NoNavData_NoReminder_TheSilentRule()
    {
        // No DFD provider (and no OFP): the transition altitude is unknown — the FO says
        // nothing rather than guessing one, however far the climb goes on QNH.
        Climbing();
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(_service.ProcessTick(T0.AddSeconds(30 * i)));
        }

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void NotLive_NoReminder()
    {
        Climbing(live: false);
        Assert.Null(_service.ProcessTick(T0));
        _phases.SetLive(true);
        _source.Snapshot = new FlightDataSnapshot { IsValid = false, AltitudeFt = 7_500 };
        Assert.Null(_service.ProcessTick(T0.AddSeconds(30)));
        Assert.Empty(_arbiter.Requests);
    }
}
