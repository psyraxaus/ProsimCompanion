using System.Reflection;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.DataRefs;
using ProsimCompanion.Speech.FoSide;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #188: the FO-side-only gate (every captain-side name refused), the baro-follow
/// state machine with the clock and its do-nothing paths, the phrase parser (the bare file
/// commands are never taken), the cross-check composer and the auto trigger.</summary>
public sealed class FoSideDutiesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    // ---- the gate ----

    [Fact]
    public void Gate_AllowsExactlyTheFourFoSideNames_DefaultSeat()
    {
        foreach (var name in FoSideDutiesGate.AuthoredNames)
        {
            Assert.True(FoSideDutiesGate.IsAllowed(name, humanIsRightSeat: false), name);
            Assert.True(ProsimWriteGate.IsAllowed(name), name + " must also pass the application gate");
        }

        Assert.Equal(4, FoSideDutiesGate.AuthoredNames.Count);
    }

    [Fact]
    public void Gate_RefusesEveryCaptainSideEfisName_DefaultSeat()
    {
        var efis1 = typeof(ProsimDataRefNames).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.Contains("Efis1", StringComparison.Ordinal))
            .Select(f => f.GetValue(null) switch { string s => s, DataRef<double> d => d.Name, DataRef<int> i => i.Name, DataRef<bool> b => b.Name, _ => null })
            .Where(n => n is not null)
            .ToList();
        Assert.NotEmpty(efis1);
        foreach (var name in efis1)
        {
            Assert.False(FoSideDutiesGate.IsAllowed(name!, humanIsRightSeat: false), name);
            Assert.Throws<InvalidOperationException>(() => FoSideDutiesGate.EnsureAllowed(name!, humanIsRightSeat: false));
        }
    }

    [Theory]
    [InlineData("system.switches.S_FCU_EFIS2_FD")]
    [InlineData("system.switches.S_FCU_EFIS2_LS")]
    [InlineData("system.switches.S_FCU_EFIS2_BARO_MODE")]
    [InlineData("system.switches.S_FCU_EFIS2_NAV1")]
    [InlineData("system.numerical.N_FCU_EFIS2_BARO_HPA")]
    [InlineData("system.analog.A_FCU_EFIS2_BARO_INCH")]
    [InlineData("system.encoders.E_FCU_EFIS2_BARO")]
    [InlineData("system.analog.A_FCU_HEADING")]
    [InlineData("system.switches.S_MIP_GEAR")]
    [InlineData("system.analog.A_FC_FO_PITCH")]
    [InlineData("")]
    public void Gate_RefusesEverythingElse_EvenOtherFoSideNames(string name)
    {
        Assert.False(FoSideDutiesGate.IsAllowed(name, humanIsRightSeat: false));
        Assert.False(FoSideDutiesGate.IsAllowed(name, humanIsRightSeat: true));
    }

    [Fact]
    public void Gate_RightSeat_SwapsToEfis1_AndRefusesEfis2()
    {
        Assert.True(FoSideDutiesGate.IsAllowed("system.switches.S_FCU_EFIS1_BARO_STD", humanIsRightSeat: true));
        Assert.True(FoSideDutiesGate.IsAllowed("system.analog.A_FCU_EFIS1_BARO_HPA", humanIsRightSeat: true));
        Assert.False(FoSideDutiesGate.IsAllowed("system.switches.S_FCU_EFIS2_BARO_STD", humanIsRightSeat: true));
        Assert.False(FoSideDutiesGate.IsAllowed("system.switches.S_FCU_EFIS1_BARO_STD", humanIsRightSeat: false));
    }

    // ---- baro follow ----

    private static BaroSample S(bool captStd, double captHpa, bool foStd, double foHpa, DateTimeOffset at, bool live = true, bool hpaMode = true, FlightPhase phase = FlightPhase.Climb)
        => new(live, captStd, captHpa, foStd, foHpa, hpaMode, phase, at);

    [Fact]
    public void CaptainToStd_FoFollowsAfterTheDebounce_Once()
    {
        var core = new BaroFollowCore();
        Assert.Null(core.Evaluate(S(false, 1013, false, 1013, T0)));
        Assert.Null(core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(1)))); // the edge arms the candidate
        Assert.Equal(BaroActionKind.SetStd, core.Pending!.Kind);
        Assert.Null(core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(2))));
        var action = core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(3.1)));
        Assert.Equal(BaroActionKind.SetStd, action!.Kind);
        Assert.Null(core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(10)))); // fired once; no re-fire without a new edge
    }

    [Fact]
    public void CaptainBackToQnh_FoSetsTheCaptainsValue()
    {
        var core = new BaroFollowCore();
        core.Evaluate(S(true, 1013, true, 1013, T0));
        core.Evaluate(S(false, 1008, true, 1013, T0.AddSeconds(1)));
        var action = core.Evaluate(S(false, 1008, true, 1013, T0.AddSeconds(3.5)));
        Assert.Equal(new BaroAction(BaroActionKind.SetQnh, 1008), action);
    }

    [Fact]
    public void CaptainChangesQnh_BothOnQnh_FoCopiesTheValue()
    {
        var core = new BaroFollowCore();
        core.Evaluate(S(false, 1013, false, 1013, T0));
        core.Evaluate(S(false, 1015, false, 1013, T0.AddSeconds(1)));
        Assert.Null(core.Evaluate(S(false, 1015, false, 1013, T0.AddSeconds(2.5))));
        Assert.Equal(new BaroAction(BaroActionKind.SetValue, 1015), core.Evaluate(S(false, 1015, false, 1013, T0.AddSeconds(3.1))));
    }

    [Fact]
    public void DoNothing_WhenTheCaptainNeverChanged_EvenIfTheSidesDiffer()
    {
        var core = new BaroFollowCore();
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(i)))); // mismatch from the start — the FO never leads
        }
    }

    [Fact]
    public void DoNothing_WhenTheFoSideCaughtUpDuringTheDebounce()
    {
        var core = new BaroFollowCore();
        core.Evaluate(S(false, 1013, false, 1013, T0));
        core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(1)));
        Assert.Null(core.Evaluate(S(true, 1013, true, 1013, T0.AddSeconds(2))));   // the pilot set the FO side too
        Assert.Null(core.Pending);
        Assert.Null(core.Evaluate(S(true, 1013, true, 1013, T0.AddSeconds(4))));
    }

    [Fact]
    public void DoNothing_WhenNotLive_OrColdAndDark_OrAlreadySet_OrInhibited()
    {
        var core = new BaroFollowCore();
        core.Evaluate(S(false, 1013, false, 1013, T0));
        Assert.Null(core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(1), live: false)));
        Assert.Null(core.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(4), live: false)));

        var cold = new BaroFollowCore();
        cold.Evaluate(S(false, 0, false, 0, T0, phase: FlightPhase.ColdAndDark));
        Assert.Null(cold.Evaluate(S(true, 0, false, 0, T0.AddSeconds(5), phase: FlightPhase.ColdAndDark)));

        var same = new BaroFollowCore();
        same.Evaluate(S(false, 1013, false, 1013, T0));
        same.Evaluate(S(true, 1013, true, 1013, T0.AddSeconds(1))); // both went STD together (a linked sim)
        Assert.Null(same.Evaluate(S(true, 1013, true, 1013, T0.AddSeconds(4))));

        var inhibited = new BaroFollowCore();
        inhibited.Evaluate(S(false, 1013, false, 1013, T0));
        inhibited.Inhibit();
        inhibited.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(1)));
        Assert.Null(inhibited.Evaluate(S(true, 1013, false, 1013, T0.AddSeconds(4))));
        inhibited.Reset();
        Assert.False(inhibited.Inhibited);
    }

    [Fact]
    public void FoSideInInches_SaysSoOnce_AndWritesNothing()
    {
        var core = new BaroFollowCore();
        core.Evaluate(S(false, 1013, false, 29.92, T0, hpaMode: false));
        var first = core.Evaluate(S(true, 1013, false, 29.92, T0.AddSeconds(1), hpaMode: false));
        Assert.Equal(BaroActionKind.InchesNotice, first!.Kind);
        Assert.Null(core.Evaluate(S(true, 1013, false, 29.92, T0.AddSeconds(4), hpaMode: false)));
        Assert.Null(core.Evaluate(S(false, 1013, false, 29.92, T0.AddSeconds(5), hpaMode: false))); // noticed already
    }

    [Fact]
    public void ImplausibleCaptainValue_IsNotFollowed()
    {
        var core = new BaroFollowCore();
        core.Evaluate(S(false, 1013, false, 1013, T0));
        core.Evaluate(S(false, 0, false, 1013, T0.AddSeconds(1)));
        Assert.Null(core.Evaluate(S(false, 0, false, 1013, T0.AddSeconds(4))));
    }

    // ---- phrases ----

    [Theory]
    [InlineData("qnh one zero one three", 1013)]
    [InlineData("set qnh 1013", 1013)]
    [InlineData("QNH one zero zero eight on your side", 1008)]
    [InlineData("qnh niner niner eight", 998)]
    public void QnhWithAValue_IsParsed(string heard, int expected)
    {
        Assert.True(FoSidePhrases.TryParseQnh(heard, out var hpa));
        Assert.Equal(expected, hpa);
    }

    [Theory]
    [InlineData("set qnh")]
    [InlineData("qnh set")]
    [InlineData("set standard")]
    [InlineData("altimeters standard")]
    [InlineData("qnh one two")]
    [InlineData("qnh two zero zero zero")]
    public void BareFileCommands_AndBadValues_AreNotTaken(string heard)
    {
        Assert.False(FoSidePhrases.TryParseQnh(heard, out _));
        Assert.False(FoSidePhrases.IsYourSideStandard(heard));
    }

    [Theory]
    [InlineData("your side standard", true)]
    [InlineData("standard on your side", true)]
    [InlineData("set standard", false)]
    public void YourSideStandard(string heard, bool expected)
    {
        Assert.Equal(expected, FoSidePhrases.IsYourSideStandard(heard));
    }

    [Theory]
    [InlineData("your range forty", 2)]
    [InlineData("range eighty on your side", 3)]
    [InlineData("your side range one sixty", 4)]
    [InlineData("ND range ten", 0)]
    public void Range_WithAYourSideMarker(string heard, int expected)
    {
        Assert.True(FoSidePhrases.TryParseRange(heard, out var index, out _));
        Assert.Equal(expected, index);
    }

    [Fact]
    public void Range_WithoutAMarker_OrOffTheKnob_IsNotTaken()
    {
        Assert.False(FoSidePhrases.TryParseRange("range forty", out _, out var unknown));
        Assert.False(unknown);
        Assert.False(FoSidePhrases.TryParseRange("your range fifty", out _, out unknown));
        Assert.True(unknown);
    }

    [Theory]
    [InlineData("nd to arc", 3)]
    [InlineData("your nd rose nav", 2)]
    [InlineData("plan on your side", 4)]
    [InlineData("your side rose ils", 0)]
    [InlineData("rose vor on your nd", 1)]
    public void Mode_WithAMarker(string heard, int expected)
    {
        Assert.True(FoSidePhrases.TryParseMode(heard, out var index));
        Assert.Equal(expected, index);
    }

    [Fact]
    public void Mode_WithoutAMarker_IsNotTaken()
    {
        Assert.False(FoSidePhrases.TryParseMode("arc", out _));
        Assert.False(FoSidePhrases.TryParseMode("rose nav", out _));
    }

    [Theory]
    [InlineData("cross check", true)]
    [InlineData("cross-check", true)]
    [InlineData("FMGC cross-check", true)]
    [InlineData("adirs check", true)]
    [InlineData("check the weather", false)]
    public void CrossCheckWords(string heard, bool expected)
    {
        Assert.Equal(expected, FoSidePhrases.IsCrossCheck(heard));
    }

    // ---- composer ----

    private static readonly IReadOnlyList<AdiruState> AllAligned = [new(1, false, 0, true), new(2, false, 0, true), new(3, false, 0, true)];

    [Fact]
    public void Composer_AllGood_ReadsTheFiguresAndNoDifferences()
    {
        var ofp = new OfpData { OriginIcao = "EGLL", DestinationIcao = "LIRF", CostIndex = "35", CruiseFlightLevel = 370 };
        var perf = new TakeoffPerfSnapshot(2, 55, 142, 145, 149, 68000, "27R", T0);
        var readout = CrossCheckComposer.Compose(AllAligned, new FmgcFacts("EGLL", "LIRF", 37000, 142, 145, 149, 55), ofp, perf);

        Assert.StartsWith("ADIRS one, two and three aligned.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("Cost index per the plan three five.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("Cruise flight level three seven zero.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("PERF: V one one four two, V R one four five, V two one four niner, flex five five.", readout.Text, StringComparison.Ordinal);
        Assert.EndsWith("Cross-check complete.", readout.Text, StringComparison.Ordinal);
        Assert.Empty(readout.Mismatches);
        Assert.True(readout.Complete);
        Assert.Equal([35, 370, 142, 145, 149, 55], readout.Numbers);
    }

    [Fact]
    public void Composer_StatesEveryMismatch_AndAligningAdiru()
    {
        var adirus = new List<AdiruState> { new(1, false, 0, true), new(2, true, 3.4, false), new(3, false, 0, false) };
        var ofp = new OfpData { OriginIcao = "EGLL", DestinationIcao = "LIML", CruiseFlightLevel = 370 };
        var perf = new TakeoffPerfSnapshot(2, null, 145, 148, 151, null, null, T0);
        var readout = CrossCheckComposer.Compose(adirus, new FmgcFacts("EGLL", "LIRF", 35000, 142, 148, 151, 0), ofp, perf);

        Assert.Contains("ADIRS two aligning, four minutes to go.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("ADIRS three — no attitude.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("The plan says", readout.Text, StringComparison.Ordinal);
        Assert.Contains("The plan has flight level three seven zero.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("TOGA", readout.Text, StringComparison.Ordinal);
        Assert.Contains("V one one four two — the computed figure is one four five.", readout.Text, StringComparison.Ordinal);
        Assert.EndsWith("check the differences.", readout.Text, StringComparison.Ordinal);
        Assert.Equal(5, readout.Mismatches.Count);
    }

    [Fact]
    public void Composer_NothingEntered_SaysSo_AndIsIncomplete()
    {
        var readout = CrossCheckComposer.Compose(AllAligned, new FmgcFacts(null, null, 0, 0, 0, 0, 0), null, null);
        Assert.Contains("INIT page: no route entered.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("No cruise level entered.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("PERF: V speeds not entered.", readout.Text, StringComparison.Ordinal);
        Assert.False(readout.Complete);
        Assert.Empty(readout.Numbers);
    }

    [Theory]
    [InlineData(FlightPhase.Preflight, true, "EGLL", "LIRF", 142, false, true)]
    [InlineData(FlightPhase.Preflight, true, "EGLL", "LIRF", 142, true, false)]   // done this cycle
    [InlineData(FlightPhase.Preflight, true, "EGLL", "LIRF", 0, false, false)]    // V1 not entered yet
    [InlineData(FlightPhase.Preflight, true, "", "LIRF", 142, false, false)]      // no route
    [InlineData(FlightPhase.TaxiOut, true, "EGLL", "LIRF", 142, false, false)]    // past the gate
    [InlineData(FlightPhase.Preflight, false, "EGLL", "LIRF", 142, false, false)] // not live
    public void AutoCrossCheck_OncePerCycle_AtTheGate_WhenTheInitSettled(FlightPhase phase, bool live, string origin, string destination, int v1, bool done, bool expected)
    {
        Assert.Equal(expected, FmgcCrossCheckService.AutoDue(phase, live, origin, destination, v1, done));
    }
}
