using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Speech.Abnormals;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #187: the rejected-takeoff tables (word below / above 100 kt, levers to idle,
/// the airborne refusal, the callout order with a clock), the EFATO item pacing, the drill
/// trigger window and the logbook count.</summary>
public sealed class RejectedTakeoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static RtoSample Roll(double ias, double? levers = 0.9, bool onGround = true, bool reversers = false, bool brake = false,
        FlightPhase phase = FlightPhase.TakeoffRoll, double? groundSpeed = null, DateTimeOffset? at = null)
        => new(phase, ias, groundSpeed ?? ias, onGround, reversers, brake, levers, levers, at ?? T0);

    private static RejectedTakeoffCore Armed(double ias = 60)
    {
        var core = new RejectedTakeoffCore();
        core.Process(Roll(10));
        core.Process(Roll(ias));
        Assert.Equal(RtoState.Armed, core.State);
        return core;
    }

    [Fact]
    public void Idle_UntilTheRoll_ThenArmed_AndTheWordIsIgnoredBeforeIt()
    {
        var core = new RejectedTakeoffCore();
        Assert.Equal(StopVerdict.Ignored, core.OnStopWord(Roll(0, phase: FlightPhase.TaxiOut)));
        core.Process(Roll(5, phase: FlightPhase.TaxiOut));
        Assert.Equal(RtoState.Idle, core.State);
        core.Process(Roll(20));
        Assert.Equal(RtoState.Armed, core.State);
    }

    [Theory]
    [InlineData(60, StopVerdict.Rejected)]
    [InlineData(99, StopVerdict.Rejected)]
    [InlineData(101, StopVerdict.RejectedWithReadback)]
    [InlineData(140, StopVerdict.RejectedWithReadback)]
    public void StopWord_RejectsBelowAndReadsBackAbove100(double ias, StopVerdict expected)
    {
        var core = Armed(ias);
        Assert.Equal(expected, core.OnStopWord(Roll(ias)));
        Assert.Equal(RtoState.Rejecting, core.State);
        Assert.Equal(ias, core.RejectSpeedKt);
        Assert.Equal("word", core.Trigger);
        Assert.Equal(StopVerdict.AlreadyRejecting, core.OnStopWord(Roll(ias - 5)));
    }

    [Fact]
    public void StopWord_AfterTheAirborneEdge_IsRefused()
    {
        var core = Armed(150);
        core.Process(Roll(160, onGround: false, phase: FlightPhase.InitialClimb));
        Assert.Equal(RtoState.Idle, core.State);
        Assert.Equal(StopVerdict.RefusedAirborne, core.OnStopWord(Roll(165, onGround: false, phase: FlightPhase.InitialClimb)));
    }

    [Fact]
    public void LeversToIdle_Above40Kt_AfterTheyWereUp_Rejects()
    {
        var core = Armed(60);
        Assert.Empty(core.Process(Roll(90, levers: 0.95)));
        Assert.Equal(RtoState.Armed, core.State);
        core.Process(Roll(95, levers: 0.05));
        Assert.Equal(RtoState.Rejecting, core.State);
        Assert.Equal("levers", core.Trigger);
        Assert.Equal(95, core.RejectSpeedKt);
    }

    [Fact]
    public void LeversToIdle_Below40Kt_OrNeverUp_DoesNotReject()
    {
        var slow = Armed(20);
        slow.Process(Roll(30, levers: 0.95));
        slow.Process(Roll(35, levers: 0.0));
        Assert.Equal(RtoState.Armed, slow.State);

        var neverUp = new RejectedTakeoffCore();
        neverUp.Process(Roll(10, levers: 0.1));
        neverUp.Process(Roll(60, levers: 0.1));
        Assert.Equal(RtoState.Armed, neverUp.State);
    }

    [Fact]
    public void Callouts_RunInOrder_WithTheClock_AndTheOfferClosesIt()
    {
        var core = Armed(120);
        core.OnStopWord(Roll(120));

        Assert.Equal([RtoCallout.ReverseGreen], core.Process(Roll(118, reversers: true)));
        Assert.Empty(core.Process(Roll(114, reversers: true)));
        Assert.Equal([RtoCallout.Decel], core.Process(Roll(109, reversers: true)));
        Assert.Equal([RtoCallout.SeventyKnots], core.Process(Roll(69, reversers: true)));
        Assert.Empty(core.Process(Roll(30, reversers: false)));
        Assert.Equal([RtoCallout.Stopped], core.Process(Roll(0, groundSpeed: 0, at: T0.AddSeconds(30))));
        Assert.Empty(core.Process(Roll(0, groundSpeed: 0, at: T0.AddSeconds(31))));
        Assert.Equal([RtoCallout.EcamOffer], core.Process(Roll(0, groundSpeed: 0, at: T0.AddSeconds(34))));
        Assert.Equal(RtoState.Done, core.State);
    }

    [Fact]
    public void ParkBrake_BringsTheOfferForward_AndASecondRollReArms()
    {
        var core = Armed(90);
        core.OnStopWord(Roll(90));
        core.Process(Roll(0, groundSpeed: 0, at: T0.AddSeconds(20)));
        Assert.Equal([RtoCallout.EcamOffer], core.Process(Roll(0, groundSpeed: 0, brake: true, at: T0.AddSeconds(21))));
        Assert.Equal(RtoState.Done, core.State);

        // Taxi back, another roll on the same cycle.
        core.Process(Roll(10, phase: FlightPhase.TaxiOut));
        core.Process(Roll(15));
        Assert.Equal(RtoState.Armed, core.State);
        Assert.Equal(StopVerdict.Rejected, core.OnStopWord(Roll(50)));
    }

    [Fact]
    public void LowSpeedReject_SkipsSeventyKnots()
    {
        var core = Armed(60);
        core.OnStopWord(Roll(60));
        var due = new List<RtoCallout>();
        foreach (var ias in new[] { 55, 45, 30, 10 })
        {
            due.AddRange(core.Process(Roll(ias)));
        }

        Assert.Equal([RtoCallout.Decel], due);
    }

    [Fact]
    public void ContinuingAfterAReject_IntoTheAir_EndsTheCallouts()
    {
        var core = Armed(110);
        core.OnStopWord(Roll(110));
        core.Process(Roll(150, onGround: false, phase: FlightPhase.InitialClimb));
        Assert.Equal(RtoState.Idle, core.State);
    }

    // ---- EFATO ----

    private static EfatoSample Air(double ias, bool onGround, double vs, double radio, double agl, bool gearDown, DateTimeOffset at)
        => new(ias, onGround, vs, radio, agl, gearDown, VrKt: 140, V2Kt: 145, at);

    [Fact]
    public void Efato_PacesTheItemsOnTheFlightData_AndHandsOverAt400()
    {
        var core = new EfatoCore();
        core.Arm(n2Engine1: 30, n2Engine2: 92, T0);
        Assert.Equal(1, core.FailedEngine);

        Assert.Equal([EfatoItem.Rudder], core.Process(Air(132, true, 0, 0, 0, true, T0)));
        Assert.Empty(core.Process(Air(138, true, 0, 0, 0, true, T0.AddSeconds(1))));
        Assert.Equal([EfatoItem.Rotate], core.Process(Air(141, true, 0, 0, 0, true, T0.AddSeconds(2))));
        Assert.Empty(core.Process(Air(141, false, 300, 5, 5, true, T0.AddSeconds(4))));
        // Radio altitude not yet rising on this tick (5 → 5): V2 alone; gear up on the next climb sample.
        Assert.Equal([EfatoItem.V2], core.Process(Air(145, false, 500, 5, 5, true, T0.AddSeconds(5))));
        Assert.Equal([EfatoItem.GearUp], core.Process(Air(147, false, 800, 30, 30, true, T0.AddSeconds(6))));
        Assert.Empty(core.Process(Air(150, false, 1200, 300, 300, false, T0.AddSeconds(20))));
        Assert.Equal([EfatoItem.EcamActions], core.Process(Air(152, false, 1200, 420, 420, false, T0.AddSeconds(26))));
        Assert.False(core.Running);
    }

    [Fact]
    public void Efato_AlreadyAirborneWithGearUp_SkipsRotateAndGear()
    {
        var core = new EfatoCore();
        core.Arm(n2Engine1: 90, n2Engine2: 20, T0);
        Assert.Equal(2, core.FailedEngine);
        var due = core.Process(Air(150, false, 600, 80, 80, false, T0));
        Assert.Equal([EfatoItem.Rudder, EfatoItem.V2], due);
        Assert.Equal([EfatoItem.EcamActions], core.Process(Air(155, false, 900, 410, 410, false, T0.AddSeconds(10))));
    }

    [Fact]
    public void Efato_AnItemWhoseCueNeverComes_FallsThroughAfterTheTimeout()
    {
        var core = new EfatoCore();
        core.Arm(30, 92, T0);
        core.Process(Air(130, true, 0, 0, 0, true, T0));
        Assert.Empty(core.Process(Air(130, true, 0, 0, 0, true, T0.AddSeconds(24))));
        Assert.Equal([EfatoItem.Rotate], core.Process(Air(130, true, 0, 0, 0, true, T0.AddSeconds(25))));
    }

    [Theory]
    [InlineData(true, 150, 140, true, 0, true)]      // on the roll past V1
    [InlineData(true, 130, 140, true, 0, false)]     // before V1
    [InlineData(true, 130, 0, true, 0, false)]       // no V1 entered
    [InlineData(true, 150, 140, false, 200, true)]   // airborne under 400
    [InlineData(true, 150, 140, false, 450, false)]  // above 400
    public void TriggerWindow_AfterV1Below400(bool afterV1, double ias, int v1, bool onGround, double agl, bool open)
    {
        var window = new TriggerWindow { AfterV1 = afterV1, MaxAglFt = 400 };
        var data = new FlightDataSnapshot { IsValid = true, IndicatedAirspeedKt = ias, V1Kt = v1, OnGround = onGround, AltitudeAglFt = agl };
        Assert.Equal(open, FailureMonitor.WindowOpen(window, data));
        Assert.False(FailureMonitor.WindowOpen(window, null));
    }

    [Fact]
    public void ShippedEfatoDrill_Parses_WithItsWindowAndSequencer()
    {
        var folder = Path.Combine(RepoRoot(), "src", "ProsimCompanion.App", "config", "abnormals");
        var drill = AbnormalLoader.LoadFolder(folder).SingleOrDefault(d => d.Id == EfatoService.DrillId);
        Assert.NotNull(drill);
        Assert.True(drill!.IsDrill);
        Assert.True(drill.IsSequenced);
        Assert.Equal("efato", drill.Sequencer);
        Assert.True(drill.Trigger!.Window!.AfterV1);
        Assert.Equal(400, drill.Trigger.Window.MaxAglFt);
        Assert.Equal(["TakeoffRoll", "InitialClimb"], drill.Phases);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }

    [Fact]
    public void Extractor_CountsRejectedTakeoffs()
    {
        var dir = Directory.CreateTempSubdirectory("pc-rto-").FullName;
        try
        {
            var path = new SessionLogBuilder()
                .At("09:00:00").Phase("TaxiOut", "TakeoffRoll")
                .At("09:00:40").Event("takeoff.rejected", new { speedKt = 95, trigger = "word", confirmed = false })
                .At("09:01:30").Phase("TakeoffRoll", "TaxiOut")
                .At("09:20:00").Phase("TakeoffRoll", "InitialClimb", iasKt: 150)
                .At("11:00:00").Phase("Approach", "LandingRollout", groundSpeedKt: 131)
                .At("11:10:00").Phase("TaxiIn", "Shutdown")
                .Write(dir);

            var facts = new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance).Extract(path);
            Assert.Equal(1, facts.RejectedTakeoffs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
