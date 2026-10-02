using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class TouchdownRecorderCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 11, 0, 0, TimeSpan.Zero);

    /// <summary>Drives the core at the recorder's 100 ms cadence.</summary>
    private sealed class Flight
    {
        private readonly TouchdownRecorderCore _core = new();
        private DateTimeOffset _now = T0;

        public List<TouchdownReport> Reports { get; } = [];

        public double Ias { get; set; } = 138;
        public double GroundSpeed { get; set; } = 132;
        public double? Pitch { get; set; } = 4.5;
        public double? Bank { get; set; } = -0.8;
        public double? Accel { get; set; } = 1.0;

        public TouchdownRecorderCore Core => _core;

        /// <summary>Feeds samples for <paramref name="seconds"/> at 100 ms.</summary>
        public Flight Run(double seconds, bool rawOnGround, bool groundContact, double verticalSpeed)
        {
            var samples = (int)Math.Round(seconds * 10);
            for (var i = 0; i < samples; i++)
            {
                Step(rawOnGround, groundContact, verticalSpeed);
            }

            return this;
        }

        public Flight Step(bool rawOnGround, bool groundContact, double verticalSpeed)
        {
            var report = _core.Process(new TouchdownSample(
                _now, rawOnGround, groundContact, verticalSpeed, Ias, GroundSpeed, Pitch, Bank, Accel));
            if (report is not null)
            {
                Reports.Add(report);
            }

            _now = _now.AddMilliseconds(100);
            return this;
        }

        public Flight Airborne(double seconds, double verticalSpeed = -700)
            => Run(seconds, rawOnGround: false, groundContact: false, verticalSpeed);

        /// <summary>The wheels touch; the committed ground contact follows 0.5 s later (the
        /// engine's two-sample agreement at its 250 ms tick).</summary>
        public Flight Touch(double verticalSpeedAfter = 0)
            => Run(0.5, rawOnGround: true, groundContact: false, verticalSpeedAfter);

        public Flight OnGround(double seconds)
            => Run(seconds, rawOnGround: true, groundContact: true, verticalSpeed: 0);
    }

    [Fact]
    public void SmoothLanding_ReportsOnce_AfterTheLandingHasSettled()
    {
        var flight = new Flight().Airborne(10).Airborne(2, verticalSpeed: -120).Touch().OnGround(4.9);
        Assert.Empty(flight.Reports);   // held back: the bounce window is still open

        flight.OnGround(1);

        var report = Assert.Single(flight.Reports);
        Assert.Equal(-120, report.VerticalSpeedFpm);
        Assert.Equal(138, report.IasKt);
        Assert.Equal(132, report.GroundSpeedKt);
        Assert.Equal(4.5, report.PitchDeg);
        Assert.Equal(-0.8, report.BankDeg);
        Assert.Equal(0, report.Bounces);
        Assert.False(report.WentAround);

        flight.OnGround(60);
        Assert.Single(flight.Reports);   // one landing, one report — however long the rollout
    }

    [Fact]
    public void FirmLanding_TakesTheMostNegativeRateOfTheLastSecond_NotTheSampleAtContact()
    {
        // The flare arrests the sink just before the wheels touch, and the first on-ground
        // sample already reads near zero: neither is the rate the aircraft arrived at.
        var flight = new Flight()
            .Airborne(10, verticalSpeed: -750)
            .Airborne(0.3, verticalSpeed: -620)     // 0.7–0.4 s before contact
            .Airborne(0.4, verticalSpeed: -380)     // the flare
            .Touch(verticalSpeedAfter: -40)
            .OnGround(6);

        var report = Assert.Single(flight.Reports);
        Assert.Equal(-750, report.VerticalSpeedFpm);    // still inside the 1 s window
    }

    [Fact]
    public void Rate_IgnoresWhatHappenedMoreThanASecondBeforeContact()
    {
        var flight = new Flight()
            .Airborne(10, verticalSpeed: -1500)     // the approach, long before
            .Airborne(1.5, verticalSpeed: -250)
            .Touch()
            .OnGround(6);

        Assert.Equal(-250, Assert.Single(flight.Reports).VerticalSpeedFpm);
    }

    [Fact]
    public void ContactValues_AreTakenAtTheFirstRawContact_NotAtTheCommittedEdge()
    {
        var flight = new Flight().Airborne(10, verticalSpeed: -200);
        flight.Ias = 136;
        flight.Pitch = 5.5;
        flight.Step(rawOnGround: true, groundContact: false, verticalSpeed: -30);   // wheels touch here
        flight.Ias = 120;                                                           // already slowing
        flight.Pitch = 1.0;                                                         // nose coming down
        flight.Run(0.4, rawOnGround: true, groundContact: false, verticalSpeed: 0).OnGround(6);

        var report = Assert.Single(flight.Reports);
        Assert.Equal(136, report.IasKt);
        Assert.Equal(5.5, report.PitchDeg);
        Assert.Equal(T0.AddSeconds(10), report.ContactAt);
    }

    [Fact]
    public void Bounce_IsCounted_AndTheLandingStillReportsOnce()
    {
        var flight = new Flight()
            .Airborne(10, verticalSpeed: -400)
            .Touch()
            .OnGround(0.5)
            .Run(1.2, rawOnGround: false, groundContact: false, verticalSpeed: 150)   // back in the air
            .OnGround(4.9);
        Assert.Empty(flight.Reports);   // the window restarted at the second contact

        flight.OnGround(1);

        var report = Assert.Single(flight.Reports);
        Assert.Equal(1, report.Bounces);
        Assert.False(report.WentAround);
        Assert.Equal(-400, report.VerticalSpeedFpm);    // the first touch is the landing's rate
    }

    [Fact]
    public void TwoBounces_AreTwo()
    {
        var flight = new Flight()
            .Airborne(10, verticalSpeed: -500)
            .Touch().OnGround(0.4)
            .Run(1.0, false, false, 200).OnGround(0.6)
            .Run(0.8, false, false, 100).OnGround(6);

        Assert.Equal(2, Assert.Single(flight.Reports).Bounces);
    }

    [Fact]
    public void GoAroundAfterTouch_ReportsTheTouch_ThenTheRealLandingSeparately()
    {
        var flight = new Flight()
            .Airborne(10, verticalSpeed: -300)
            .Touch()
            .OnGround(2)
            .Airborne(5, verticalSpeed: 1800);
        Assert.Empty(flight.Reports);   // exactly 5 s airborne could still be a (long) bounce

        flight.Airborne(0.2, verticalSpeed: 1800);

        var touch = Assert.Single(flight.Reports);
        Assert.True(touch.WentAround);
        Assert.Equal(0, touch.Bounces);
        Assert.Equal(-300, touch.VerticalSpeedFpm);

        // Round the circuit and land: armed again without waiting, and a clean second report.
        flight.Airborne(120, verticalSpeed: -650).Airborne(1, verticalSpeed: -180).Touch().OnGround(6);

        Assert.Equal(2, flight.Reports.Count);
        Assert.False(flight.Reports[1].WentAround);
        Assert.Equal(-180, flight.Reports[1].VerticalSpeedFpm);
    }

    [Fact]
    public void GroundFlicker_OfTheRawFlag_IsNeitherATouchdownNorABounce()
    {
        // One raw sample flips on short final (the committed contact never follows), and
        // one flips off during the rollout: the engine's agreement filter swallowed both,
        // and so does the recorder.
        var flight = new Flight()
            .Airborne(10, verticalSpeed: -700)
            .Step(rawOnGround: true, groundContact: false, verticalSpeed: -700)
            .Airborne(3, verticalSpeed: -160)
            .Touch()
            .OnGround(1)
            .Step(rawOnGround: false, groundContact: true, verticalSpeed: 0)
            .OnGround(6);

        var report = Assert.Single(flight.Reports);
        Assert.Equal(0, report.Bounces);
        Assert.Equal(-160, report.VerticalSpeedFpm);
        Assert.Equal(T0.AddMilliseconds(13100), report.ContactAt);    // the real contact, not the flicker
    }

    [Fact]
    public void CommittedFlicker_OnTheTakeoffRoll_IsNotALanding()
    {
        // Ground contact lost for under the arming time (a bump on the roll, a rejected
        // rotation): the aircraft never flew, so coming back down is not a touchdown.
        var flight = new Flight()
            .OnGround(20)
            .Run(2, rawOnGround: false, groundContact: false, verticalSpeed: 300)
            .OnGround(20);

        Assert.Empty(flight.Reports);
    }

    [Fact]
    public void ParkedAndTaxiing_NeverReport()
    {
        var flight = new Flight().OnGround(120);

        Assert.Empty(flight.Reports);
    }

    [Fact]
    public void NormalFlight_TakeoffThenLanding_ReportsOnlyTheLanding()
    {
        var flight = new Flight()
            .OnGround(30)
            .Airborne(600, verticalSpeed: 1500)
            .Airborne(1, verticalSpeed: -210)
            .Touch()
            .OnGround(10);

        Assert.Equal(-210, Assert.Single(flight.Reports).VerticalSpeedFpm);
    }

    [Fact]
    public void RawAcceleration_IsRecordedAsBaselineAndPeaks_Unconverted()
    {
        var flight = new Flight { Accel = 1.02 };
        flight.Airborne(10, verticalSpeed: -300);
        flight.Accel = 1.9;                                    // the oleos compress
        flight.Step(rawOnGround: true, groundContact: false, verticalSpeed: -20);
        flight.Accel = 0.7;                                    // rebound
        flight.Run(0.4, rawOnGround: true, groundContact: false, verticalSpeed: 0);
        flight.Accel = 1.0;
        flight.OnGround(4);
        flight.Accel = 3.5;                                    // a bump late in the rollout: outside the window
        flight.OnGround(5);

        var report = Assert.Single(flight.Reports);
        Assert.Equal(1.02, report.AccelerationYRawBeforeContact!.Value, 6);
        Assert.Equal(1.9, report.AccelerationYRawMax);
        Assert.Equal(0.7, report.AccelerationYRawMin);
    }

    [Fact]
    public void MissingAttitudeAndAcceleration_StayNull()
    {
        var flight = new Flight { Pitch = null, Bank = null, Accel = null };
        flight.Airborne(10, verticalSpeed: -200).Touch().OnGround(6);

        var report = Assert.Single(flight.Reports);
        Assert.Null(report.PitchDeg);
        Assert.Null(report.BankDeg);
        Assert.Null(report.AccelerationYRawMax);
        Assert.Null(report.AccelerationYRawMin);
        Assert.Null(report.AccelerationYRawBeforeContact);
    }

    [Fact]
    public void Reset_DropsAPendingTouchdown_AndDisarms()
    {
        var flight = new Flight().Airborne(10, verticalSpeed: -200).Touch().OnGround(2);

        flight.Core.Reset();
        flight.OnGround(10);

        Assert.Empty(flight.Reports);
    }
}
