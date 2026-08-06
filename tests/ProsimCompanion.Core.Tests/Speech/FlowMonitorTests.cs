using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class FlowMonitorTests : IDisposable
{
    private readonly SopOptions _sop = new();
    private readonly SpeechOptions _speech = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeFlightSource _source = new();
    private readonly FlowMonitor _monitor;
    private long _now = 1_000_000;

    public FlowMonitorTests()
    {
        _monitor = new FlowMonitor(
            SpeechTestSupport.SopMonitor(_sop),
            SpeechTestSupport.SpeechMonitor(_speech),
            _arbiter,
            _source,
            _phase,
            SpeechTestSupport.TempEventLog(),
            NullLogger<FlowMonitor>.Instance);
    }

    public void Dispose() => _monitor.Dispose();

    private void Tick(FlightDataSnapshot s, long advanceMs = 1000)
    {
        _now += advanceMs;
        _source.Snapshot = s; // validity predicates re-sample from here
        _monitor.ProcessSample(s, _now);
    }

    private static FlightDataSnapshot ClimbOut(double aglFt, bool gearDown = false, int flapHandle = 0)
        => new()
        {
            IsValid = true,
            OnGround = false,
            AltitudeAglFt = aglFt,
            AltitudeFt = aglFt + 500,
            GearDown = gearDown,
            FlapHandle = flapHandle,
        };

    [Fact]
    public void GearStillDown_EdgeTriggered_ResolvesAndRespectsRateLimit()
    {
        _phase.SetPhase(FlightPhase.Climb);

        Tick(ClimbOut(1500, gearDown: true));
        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("gear still down", request.Text);
        Assert.Equal(SpeechPriority.High, request.Priority);

        // true→true is silent.
        Tick(ClimbOut(1600, gearDown: true));
        Assert.Single(_arbiter.Requests);

        // Resolve, then re-trigger inside the 60 s rate limit: active again but not spoken.
        Tick(ClimbOut(1700, gearDown: false));
        Tick(ClimbOut(1800, gearDown: true));
        Assert.Single(_arbiter.Requests);

        // Resolve and re-trigger after the window: speaks again.
        Tick(ClimbOut(1900, gearDown: false));
        Tick(ClimbOut(2000, gearDown: true), advanceMs: 61_000);
        Assert.Equal(2, _arbiter.Requests.Count(r => r.Tag == "gearStillDown"));
    }

    [Fact]
    public void FlapsNotRetracted_FiresAboveCleanAltitude()
    {
        _phase.SetPhase(FlightPhase.Climb);
        Tick(ClimbOut(2500, flapHandle: 1)); // below 3000 — quiet
        Tick(ClimbOut(3200, flapHandle: 1));

        Assert.Single(_arbiter.Requests, r => r.Tag == "flapsNotRetracted");
    }

    [Fact]
    public void LandingLights_OnAboveCeiling_Fires_OffBelowCeilingDisabledByDefault()
    {
        _phase.SetPhase(FlightPhase.Climb);
        Tick(ClimbOut(9000) with { AltitudeFt = 11_000, AnyLandingLightOn = true });
        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("landingLightsOn", request.Tag);
        Assert.Equal(SpeechPriority.Low, request.Priority);

        _arbiter.Requests.Clear();
        _phase.SetPhase(FlightPhase.Descent);
        Tick(ClimbOut(5000) with { AltitudeFt = 8000, AnyLandingLightOn = false });
        Assert.Empty(_arbiter.Requests); // landingLightsBelowCeiling ships disabled
    }

    [Fact]
    public void ParkingBrakeWithThrust_NoPhaseGate()
    {
        _phase.SetPhase(FlightPhase.TaxiOut);
        Tick(new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, ParkBrakeSet = true, AverageN1Percent = 60,
        });

        Assert.Single(_arbiter.Requests, r => r.Tag == "parkingBrakeWithThrust");
    }

    [Fact]
    public void Beacon_DisabledByDefault_FiresWhenOptedIn()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, BeaconOn = false, AnyEngineRunningRaw = true,
        };
        _phase.SetPhase(FlightPhase.TaxiOut);

        Tick(snapshot);
        Assert.Empty(_arbiter.Requests);

        _sop.FlowMonitor.BeaconOffEngineRunning.Enabled = true;
        Tick(snapshot);
        Assert.Single(_arbiter.Requests, r => r.Tag == "beaconOffEngineRunning");
    }

    [Fact]
    public void Icing_RequiresMoistureAndColdTat_AntiIceOnSilences()
    {
        _phase.SetPhase(FlightPhase.TaxiOut);
        var icing = new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, TatC = 5, InCloud = true,
        };

        Tick(icing);
        Assert.Single(_arbiter.Requests, r => r.Tag == "icingConditions");

        // Anti-ice on resolves it; turning it off again within the rate limit stays quiet.
        Tick(icing with { EngineAntiIce1On = true });
        Tick(icing);
        Assert.Single(_arbiter.Requests, r => r.Tag == "icingConditions");
    }

    [Fact]
    public void Icing_NoVisibleMoisture_Silent()
    {
        _phase.SetPhase(FlightPhase.TaxiOut);
        Tick(new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, TatC = 5, InCloud = false, VisibilityM = 9999,
        });

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void AntiIceLeftOn_RequiresDwell_InterruptionRestartsClock()
    {
        _phase.SetPhase(FlightPhase.Cruise);
        var warm = new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, WingAntiIceOn = true, TatC = 20, AltitudeFt = 10_000,
        };

        Tick(warm);                    // starts the dwell clock — never fires first observation
        Tick(warm, advanceMs: 60_000); // 60 s — still short of 120 s
        Assert.DoesNotContain("antiIceLeftOn", _arbiter.Tags);

        Tick(warm, advanceMs: 61_000); // 121 s sustained
        Assert.Single(_arbiter.Requests, r => r.Tag == "antiIceLeftOn");

        // A cold interruption resets the clock entirely.
        _arbiter.Requests.Clear();
        Tick(warm with { TatC = 10 });   // raw condition false → dwell removed, advisory resolves
        Tick(warm);                      // dwell restarts (first observation, records only)
        Tick(warm, advanceMs: 61_000);   // 61 s sustained — still short of 120 s
        Assert.Empty(_arbiter.Requests);

        Tick(warm, advanceMs: 61_000);   // 122 s sustained, rate-limit window long elapsed
        Assert.Single(_arbiter.Requests, r => r.Tag == "antiIceLeftOn");
    }

    [Fact]
    public void IsaDeviation_OncePerCruise_RearmsOnLeavingCruise()
    {
        _phase.SetPhase(FlightPhase.Cruise);
        // ISA at 35,000 ft = 15 − 1.98×35 = −54.3 °C; OAT −40 → deviation +14.3 → "plus 14".
        var cruise = new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, AltitudeFt = 35_000, OatC = -40,
        };

        Tick(cruise);
        var isa = Assert.Single(_arbiter.Requests);
        Assert.Equal("ISA plus 14 today. Climb performance will be reduced.", isa.Text);
        Assert.Equal(SpeechPriority.Low, isa.Priority);

        Tick(cruise); // latched
        Assert.Single(_arbiter.Requests);

        // Step climb re-arms: one non-cruise pass, then cruise again.
        _phase.SetPhase(FlightPhase.Climb);
        Tick(cruise with { AltitudeFt = 36_000 });
        _phase.SetPhase(FlightPhase.Cruise);
        Tick(cruise with { AltitudeFt = 37_000, OatC = -45 });

        Assert.Equal(2, _arbiter.Requests.Count);
    }

    [Fact]
    public void IsaDeviation_Cold_UsesIcingSentence()
    {
        _phase.SetPhase(FlightPhase.Cruise);
        // ISA at 30,000 ft = 15 − 59.4 = −44.4; OAT −56 → deviation −11.6 → "minus 12".
        Tick(new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, AltitudeFt = 30_000, OatC = -56,
        });

        var isa = Assert.Single(_arbiter.Requests);
        Assert.Equal("ISA minus 12 today. Colder than standard; watch for icing.", isa.Text);
    }

    [Fact]
    public void InvalidSample_Ignored()
    {
        _phase.SetPhase(FlightPhase.Climb);
        Tick(new FlightDataSnapshot { IsValid = false, GearDown = true, AltitudeAglFt = 2000 });

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void BlankText_NeverFires()
    {
        _sop.FlowMonitor.GearStillDown.Text = "";
        _phase.SetPhase(FlightPhase.Climb);
        Tick(ClimbOut(1500, gearDown: true));

        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void AdvisoryValidity_ResamplesLiveCondition()
    {
        _phase.SetPhase(FlightPhase.Climb);
        Tick(ClimbOut(1500, gearDown: true));
        var request = Assert.Single(_arbiter.Requests);

        Assert.True(request.IsStillValid!());          // condition still true live
        _source.Snapshot = ClimbOut(1600, gearDown: false);
        Assert.False(request.IsStillValid!());         // gear raised → advisory stale
    }
}
