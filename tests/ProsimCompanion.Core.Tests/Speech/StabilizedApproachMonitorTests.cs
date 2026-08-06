using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class StabilizedApproachMonitorTests : IDisposable
{
    private readonly SopOptions _sop = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeFlightSource _source = new();
    private readonly StabilizedApproachMonitor _monitor;

    public StabilizedApproachMonitorTests()
    {
        _monitor = new StabilizedApproachMonitor(
            SpeechTestSupport.SopMonitor(_sop),
            _arbiter,
            _source,
            _phase,
            SpeechTestSupport.TempEventLog(),
            NullLogger<StabilizedApproachMonitor>.Instance);
        _monitor.Start();
        _phase.SetPhase(FlightPhase.Approach);
    }

    public void Dispose() => _monitor.Dispose();

    /// <summary>A stable, configured final at the given radio altitude (VLS 130, IAS 135).</summary>
    private static FlightDataSnapshot Stable(double radio, double sinkFpm = 700)
        => new()
        {
            IsValid = true,
            OnGround = false,
            RadioAltitudeFt = radio,
            IndicatedAirspeedKt = 135,
            VlsKt = 130,
            VerticalSpeedFpm = -sinkFpm,
            GearDown = true,
            FlapHandle = 5,
            AltitudeFt = radio + 500,
        };

    [Fact]
    public void StableGateAtThousand_AnnouncesStabilized()
    {
        _monitor.ProcessSample(Stable(1100));
        _monitor.ProcessSample(Stable(990));

        var stable = Assert.Single(_arbiter.Requests);
        Assert.Equal("stabilized", stable.Text);
        Assert.Equal("stabilized:stable", stable.Tag);
        Assert.Equal(SpeechPriority.High, stable.Priority);
    }

    [Fact]
    public void StableGateAtFiveHundred_SilentByDefault()
    {
        _monitor.ProcessSample(Stable(1100));
        _monitor.ProcessSample(Stable(990));
        _arbiter.Requests.Clear();

        _monitor.ProcessSample(Stable(520));
        _monitor.ProcessSample(Stable(480));

        Assert.Empty(_arbiter.Requests); // 500 gate has AnnounceStabilized=false
    }

    [Fact]
    public void ExcessiveSink_AnnouncesUnstableGoAround_Critical()
    {
        _monitor.ProcessSample(Stable(1100, sinkFpm: 1400));
        _monitor.ProcessSample(Stable(990, sinkFpm: 1400));

        var unstable = Assert.Single(_arbiter.Requests);
        Assert.Equal("unstable, go around", unstable.Text);
        Assert.Equal(SpeechPriority.Critical, unstable.Priority);
    }

    [Fact]
    public void GearUp_Unstable()
    {
        var gearUp = Stable(990) with { GearDown = false };
        _monitor.ProcessSample(Stable(1100) with { GearDown = false });
        _monitor.ProcessSample(gearUp);

        Assert.Equal("unstable, go around", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void MissingVls_Indeterminate_Silent()
    {
        _monitor.ProcessSample(Stable(1100) with { VlsKt = 0 });
        _monitor.ProcessSample(Stable(990) with { VlsKt = 0 });

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void MissingVls_WithFailingGear_StillUnstable()
    {
        _monitor.ProcessSample(Stable(1100) with { VlsKt = 0, GearDown = false });
        _monitor.ProcessSample(Stable(990) with { VlsKt = 0, GearDown = false });

        Assert.Equal("unstable, go around", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void Climb_PassesSinkCriterion()
    {
        // Sink is sign-flipped: a climb (positive VS) yields negative sink and passes.
        _monitor.ProcessSample(Stable(1100, sinkFpm: -500));
        _monitor.ProcessSample(Stable(990, sinkFpm: -500));

        Assert.Equal("stabilized", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public void Gate_FiresOncePerApproach_RearmsOnNewApproach()
    {
        _monitor.ProcessSample(Stable(1100));
        _monitor.ProcessSample(Stable(990));
        _monitor.ProcessSample(Stable(1100)); // climb back above and descend again
        _monitor.ProcessSample(Stable(990));
        Assert.Single(_arbiter.Requests);

        // Go-around → new approach re-arms gates and latches.
        _phase.SetPhase(FlightPhase.InitialClimb);
        _phase.SetPhase(FlightPhase.Approach);
        _monitor.ProcessSample(Stable(1100));
        _monitor.ProcessSample(Stable(990));

        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public void OutsideApproachOrDescent_Ignored()
    {
        _phase.SetPhase(FlightPhase.Cruise);
        _monitor.ProcessSample(Stable(1100));
        _monitor.ProcessSample(Stable(990));

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void UnstableAnnouncedOnce_LaterStableGateStillAnnounces()
    {
        _sop.ApproachGates[1].AnnounceStabilized = true; // let the 500 gate announce too

        _monitor.ProcessSample(Stable(1100, sinkFpm: 1400));
        _monitor.ProcessSample(Stable(990, sinkFpm: 1400)); // unstable at 1000
        _monitor.ProcessSample(Stable(520));
        _monitor.ProcessSample(Stable(480));               // recovered by 500

        Assert.Equal(2, _arbiter.Requests.Count);
        Assert.Equal("unstable, go around", _arbiter.Requests[0].Text);
        Assert.Equal("stabilized", _arbiter.Requests[1].Text);
    }
}
