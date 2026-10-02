using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #148, module 2: the takeoff gross-error check core and the shell's
/// once-per-loadsheet-edition gating.</summary>
public sealed class GrossErrorCheckTests
{
    private static readonly GrossErrorCheckOptions Options = new() { Enabled = true };

    private static LoadsheetSlotView Final(double zfw = 58800, double fuel = 8100, int edition = 1, LoadsheetSlotStatus status = LoadsheetSlotStatus.Sent)
        => new(status, edition, null, zfw, zfw + fuel, 28, 27, fuel, 150, null);

    private static TakeoffPerfSnapshot Perf(int conf = 2, int? flex = 55, int v1 = 141, int vr = 144, int v2 = 147)
        => new(conf, flex, v1, vr, v2, 66900, "27R", DateTimeOffset.UtcNow);

    [Fact]
    public void Evaluate_EverythingAgrees_SaysChecked()
    {
        var report = GrossErrorCheckCore.Evaluate(
            new GrossErrorInputs(58950, 8300, Final(), Perf(), 2, 56, 142, 144, 149), Options);

        Assert.True(report.Checked);
        Assert.Empty(report.Mismatches);
        Assert.Equal("Gross error check: checked.", report.Text);
    }

    [Fact]
    public void Evaluate_NamesEveryMismatch()
    {
        var report = GrossErrorCheckCore.Evaluate(
            new GrossErrorInputs(59700, 7500, Final(), Perf(), 1, 60, 135, 144, 152), Options);

        Assert.False(report.Checked);
        Assert.Equal(
            "Gross error check: zero fuel weight is 900 kilos above the loadsheet; "
            + "fuel on board is 600 kilos below the loadsheet block fuel; "
            + "flaps: FMS config 1, performance config 2; flex: FMS 60, performance 55; "
            + "V one: FMS 135, performance 141, V two: FMS 152, performance 147.",
            report.Text);
    }

    [Fact]
    public void Evaluate_WithoutAPerfResultOrLoadsheet_SaysWhatWasNotCompared()
    {
        var noPerf = GrossErrorCheckCore.Evaluate(new GrossErrorInputs(58800, 8100, Final(), null, 2, 55, 141, 144, 147), Options);
        var noSheet = GrossErrorCheckCore.Evaluate(new GrossErrorInputs(58800, 8100, Final(status: LoadsheetSlotStatus.None), Perf(), 2, 55, 141, 144, 147), Options);
        var nothing = GrossErrorCheckCore.Evaluate(new GrossErrorInputs(null, null, null, null, 0, 0, 0, 0, 0), Options);

        Assert.Equal("Gross error check: checked. Takeoff performance not compared.", noPerf.Text);
        Assert.Equal("Gross error check: checked. Final loadsheet not compared.", noSheet.Text);
        Assert.Equal("Gross error check: checked. Final loadsheet and takeoff performance not compared.", nothing.Text);
    }

    [Fact]
    public void Evaluate_FmsEntriesNotMade_AreNotMismatches_TogaIsNotAFlexMismatch()
    {
        // Flaps 0 / flex 0 / speeds 0 = nothing entered yet; a TOGA perf result (null flex) never argues with an FMS flex.
        var notEntered = GrossErrorCheckCore.Evaluate(new GrossErrorInputs(58800, 8100, Final(), Perf(), 0, 0, 0, 0, 0), Options);
        var toga = GrossErrorCheckCore.Evaluate(new GrossErrorInputs(58800, 8100, Final(), Perf(flex: null), 2, 48, 141, 144, 147), Options);

        Assert.True(notEntered.Checked);
        Assert.True(toga.Checked);
    }

    [Fact]
    public void Evaluate_TolerancesAreInclusive()
    {
        var atEdge = GrossErrorCheckCore.Evaluate(
            new GrossErrorInputs(58800 + 500, 8100 - 300, Final(), Perf(), 2, 57, 144, 147, 150), Options);
        var pastEdge = GrossErrorCheckCore.Evaluate(
            new GrossErrorInputs(58800 + 501, 8100, Final(), Perf(), 2, 55, 141, 144, 147), Options);

        Assert.True(atEdge.Checked);
        Assert.False(pastEdge.Checked);
        Assert.Single(pastEdge.Mismatches);
    }

    // ---- the shell ----

    private sealed class Harness
    {
        public FakePhaseSource Phases { get; } = new();
        public FakeArbiter Arbiter { get; } = new();
        public FakeDataRefs Refs { get; } = new();
        public LoadsheetStore Loadsheet { get; } = new();
        public TakeoffPerfStore Perf { get; } = new();
        public GroundOpsSignals Signals { get; } = new();
        public SopOptions Sop { get; } = new();
        public GrossErrorCheckMonitor Monitor { get; }

        public Harness()
        {
            Sop.Monitoring.GrossErrorCheck.Enabled = true;
            Refs.Values["aircraft.weight.zfw"] = 58800.0;
            Refs.Values["aircraft.fuel.total.amount.kg"] = 8100.0;
            Refs.Values["aircraft.fms.perf.takeOff.flaps"] = 2;
            Refs.Values["aircraft.fms.perf.takeOff.flexTemp"] = 55;
            Refs.Values["aircraft.fms.perf.takeOff.v1"] = 141;
            Refs.Values["aircraft.fms.perf.takeOff.vr"] = 144;
            Refs.Values["aircraft.fms.perf.takeOff.v2"] = 147;
            Perf.Set(GrossErrorCheckTests.Perf());
            Phases.SetPhase(FlightPhase.Preflight);
            Monitor = new GrossErrorCheckMonitor(
                SpeechTestSupport.SopMonitor(Sop), Phases, Loadsheet, Perf, Signals, Refs, Arbiter,
                SpeechTestSupport.TempEventLog(), NullLogger<GrossErrorCheckMonitor>.Instance);
        }
    }

    [Fact]
    public void Automatic_FiresOnceWhenTheFinalIsSent_AndAgainForANewEdition()
    {
        var h = new Harness();
        Assert.Null(h.Monitor.ProcessTick());                  // no final yet

        h.Loadsheet.SetFinal(Final(edition: 1));
        var first = h.Monitor.ProcessTick();
        Assert.NotNull(first);
        Assert.True(first.Checked);
        Assert.Null(h.Monitor.ProcessTick());                  // same edition: silent
        Assert.Null(h.Monitor.ProcessTick());

        h.Loadsheet.SetFinal(Final(zfw: 60000, edition: 2));   // a revised final
        var second = h.Monitor.ProcessTick();
        Assert.NotNull(second);
        Assert.False(second.Checked);
        Assert.Equal(2, h.Arbiter.Requests.Count);
        Assert.Equal(SpeechPriority.Normal, h.Arbiter.Requests[0].Priority);
        Assert.Equal(SpeechPriority.High, h.Arbiter.Requests[1].Priority);
        Assert.All(h.Arbiter.Requests, r => Assert.Equal("fo.gross-error-check", r.Tag));
    }

    [Fact]
    public void Automatic_StaysSilent_WhenDisabled_Manual_NotLive_Airborne_OrSpeedsMissing()
    {
        var h = new Harness();
        h.Loadsheet.SetFinal(Final());

        h.Sop.Monitoring.GrossErrorCheck.Enabled = false;
        Assert.Null(h.Monitor.ProcessTick());
        h.Sop.Monitoring.GrossErrorCheck.Enabled = true;

        h.Sop.Monitoring.GrossErrorCheck.Automatic = false;
        Assert.Null(h.Monitor.ProcessTick());
        h.Sop.Monitoring.GrossErrorCheck.Automatic = true;

        h.Phases.SetLive(false);
        Assert.Null(h.Monitor.ProcessTick());
        h.Phases.SetLive(true);

        h.Phases.SetPhase(FlightPhase.Climb);
        Assert.Null(h.Monitor.ProcessTick());
        h.Phases.SetPhase(FlightPhase.Preflight);

        h.Refs.Values["aircraft.fms.perf.takeOff.v2"] = 0;
        Assert.Null(h.Monitor.ProcessTick());                  // waits for the PERF page to be filled
        Assert.Empty(h.Arbiter.Requests);

        h.Refs.Values["aircraft.fms.perf.takeOff.v2"] = 147;
        Assert.NotNull(h.Monitor.ProcessTick());
    }

    [Fact]
    public void Automatic_ReArmsOnAFlightCycleReset_AndForgetsThePerfResult()
    {
        var h = new Harness();
        h.Loadsheet.SetFinal(Final());
        Assert.NotNull(h.Monitor.ProcessTick());

        h.Signals.RaiseFlightCycleReset();
        Assert.Null(h.Perf.Snapshot());
        var again = h.Monitor.ProcessTick();                   // the same edition is checked again on the new cycle

        Assert.NotNull(again);
        Assert.Contains("Takeoff performance not compared", again.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_RunsTheCheckOnRequest_OrSaysWhyNot()
    {
        var h = new Harness();
        h.Sop.Monitoring.GrossErrorCheck.Automatic = false;
        h.Loadsheet.SetFinal(Final());

        Assert.True(h.Monitor.TryHandle("gross error check"));
        Assert.Equal("Gross error check: checked.", h.Arbiter.Requests[^1].Text);

        h.Phases.SetLive(false);
        Assert.True(h.Monitor.TryHandle("gross error check"));
        Assert.Equal("No flight data for a gross error check.", h.Arbiter.Requests[^1].Text);

        Assert.False(h.Monitor.TryHandle("fuel check"));
    }
}
