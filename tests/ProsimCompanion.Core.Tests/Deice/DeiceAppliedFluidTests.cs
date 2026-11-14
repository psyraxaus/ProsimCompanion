using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Deice;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Deice;

/// <summary>Issue #166, the holdover fluid (EGLL 2026-11-14, session 043241): 04:42:06Z the app
/// picked "Fluid Type IV" among "Fluid Type I [GSX choice]" / II / III / IV; 04:42:07Z
/// <c>L:FSDT_GSX_DEICING_TYPE</c> read 3 with state 5; 04:44:49Z the card armed as
/// "Type III 75%" from "GSX" and the ground crew said "Type three". On EFHK 2026-10-10 GSX
/// sprayed Type I and the LVAR read 0 throughout. The LVAR is read as a zero-based index —
/// an inference — and the app's own menu answer outranks it.</summary>
public sealed class DeiceAppliedFluidTests
{
    [Theory]
    [InlineData(0.0, 1)] // EFHK 2026-10-10: Type I sprayed, LVAR 0
    [InlineData(1.0, 2)]
    [InlineData(2.0, 3)]
    [InlineData(3.0, 4)] // EGLL 2026-11-14: Type IV picked, LVAR 3
    public void FromLvar_ReadsAZeroBasedIndex(double raw, int expected)
        => Assert.Equal(expected, DeiceAppliedFluid.FromLvar(raw));

    [Theory]
    [InlineData(4.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void FromLvar_OutsideTheIndex_IsNoFluid(double raw)
        => Assert.Null(DeiceAppliedFluid.FromLvar(raw));

    [Fact]
    public void FromLvar_NotDelivered_IsNoFluid()
        => Assert.Null(DeiceAppliedFluid.FromLvar(null));

    [Fact]
    public void Egll20261114_TheMenuAnswerIsTheFluid_AndTheLvarAgreesAsAZeroBasedIndex()
    {
        var fluid = DeiceAppliedFluid.Resolve(lvarRaw: 3, menuFluidType: 4);

        Assert.Equal(4, fluid.FluidType);
        Assert.Equal(DeiceFluidWitness.MenuAnswer, fluid.Witness);
        Assert.Equal(3, fluid.LvarRaw);
        Assert.Equal(4, fluid.LvarFluidType);
        Assert.False(fluid.Disagrees);

        // The card and the ground crew's call: Type IV 75%, not the flight's "Type III 75%".
        Assert.Equal((4, 75, "menu answer", false), DeiceHoldoverService.ResolveFluid(fluid.FluidType, 4, 75, "Type IV", "75"));
    }

    [Fact]
    public void TheMenuAnswerWins_EvenOverALvarThatNamesAnotherFluid_AndTheDisagreementIsFlagged()
    {
        var fluid = DeiceAppliedFluid.Resolve(lvarRaw: 1, menuFluidType: 4);

        Assert.Equal(4, fluid.FluidType);
        Assert.Equal(DeiceFluidWitness.MenuAnswer, fluid.Witness);
        Assert.Equal(2, fluid.LvarFluidType);
        Assert.True(fluid.Disagrees);

        // A value outside the index is a disagreement too — both values go to the log.
        Assert.True(DeiceAppliedFluid.Resolve(lvarRaw: 4, menuFluidType: 4).Disagrees);
        // A LVAR that was never delivered is not: there is nothing to compare.
        Assert.False(DeiceAppliedFluid.Resolve(lvarRaw: null, menuFluidType: 4).Disagrees);
        Assert.Equal(4, DeiceAppliedFluid.Resolve(lvarRaw: null, menuFluidType: 4).FluidType);
    }

    [Fact]
    public void Efhk20261010_NoMenuAnswer_LvarZeroIsTypeOne_NotNotReported()
    {
        var fluid = DeiceAppliedFluid.Resolve(lvarRaw: 0, menuFluidType: null);

        Assert.Equal(1, fluid.FluidType);
        Assert.Equal(DeiceFluidWitness.LvarZeroBased, fluid.Witness);
        Assert.False(fluid.Disagrees);

        // That flight's card armed "Type IV 75%" from the settings while GSX sprayed Type I.
        Assert.Equal((1, 75, "GSX", false), DeiceHoldoverService.ResolveFluid(fluid.FluidType, null, null, "Type IV", "75"));
    }

    [Fact]
    public void NoMenuAnswer_AndNoUsableLvar_IsUnknown_AndTheCardFallsBackToTheSettingsWithAWarning()
    {
        foreach (var raw in new double?[] { null, 4, -1 })
        {
            var fluid = DeiceAppliedFluid.Resolve(raw, menuFluidType: null);

            Assert.Equal(0, fluid.FluidType);
            Assert.Equal(DeiceFluidWitness.None, fluid.Witness);
            Assert.Equal((4, 75, "settings (unconfirmed)", true), DeiceHoldoverService.ResolveFluid(fluid.FluidType, null, null, "Type IV", "75"));
        }
    }

    [Fact]
    public void ResolveFluid_TakesTheMenuAnswerBeforeTheSignalsFluid()
    {
        // The order before 2026-11-14 was the LVAR first: (3, menu 4) armed as Type III.
        Assert.Equal((4, 75, "menu answer", false), DeiceHoldoverService.ResolveFluid(3, 4, 75, "Type IV", "75"));
        Assert.Equal((3, 75, "GSX", false), DeiceHoldoverService.ResolveFluid(3, null, 75, "Type IV", "100"));
    }

    [Theory]
    [InlineData(5, 3, "Type IV")]  // 04:42:07Z "state 5 (5 Active), fluid type 3"
    [InlineData(6, 3, "Type IV")]  // 04:43:57Z
    [InlineData(5, 0, "Type I")]   // EFHK 2026-10-10
    [InlineData(0, 3, null)]       // 04:44:59Z: at rest the LVAR still holds the last treatment's value
    [InlineData(0, 0, null)]       // session start: 0 is not "Type I" while nothing is sprayed
    [InlineData(4, 0, null)]       // 04:40:51Z: Requested, the fluid not chosen yet
    [InlineData(5, 7, null)]
    public void Monitor_NamesTheAssumedFluid_OnlyWhileActiveOrCompleted(int state, int type, string? expected)
        => Assert.Equal(expected, GsxDeiceStateMonitor.AssumedFluid(state, type));

    [Fact]
    public void TheCard_ArmsWithTheFluidTheAppPicked_Egll20261114()
    {
        var signals = new GroundOpsSignals();
        using var activity = new DeiceActivityStore(signals);
        using var holdover = new DeiceHoldoverService(
            signals,
            activity,
            OptionsSupport.Monitor(new GsxOptions { DeIceFluidType = "Type IV", DeIceConcentration = "75" }),
            NullLogger<DeiceHoldoverService>.Instance);

        activity.RecordMenuAnswer(4, null);  // 04:42:06.60Z picked 'Fluid Type IV'
        activity.RecordMenuAnswer(null, 75); // 04:42:06.94Z picked '75% '
        signals.RaiseDeiceCompleted(DeiceAppliedFluid.Resolve(3, activity.Snapshot().MenuFluidType).FluidType); // 04:44:49Z

        var card = holdover.Snapshot();
        Assert.True(card.Active);
        Assert.Equal("Type IV 75%", card.FluidLabel);
        Assert.Equal("menu answer", card.FluidSource);
    }
}
